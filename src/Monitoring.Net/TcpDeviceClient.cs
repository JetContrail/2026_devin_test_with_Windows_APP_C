using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Monitoring.Protocol;

namespace Monitoring.Net;

/// <summary>
/// Persistent TCP client for docs/protocol_spec.md v0.2:
/// length-prefixed frames on a stream socket, sequence-matched responses,
/// resync on malformed bytes, TCP+application keepalive, and an automatic
/// reconnect loop with exponential backoff (1s → 30s cap, ±20% jitter)
/// that never gives up (24h operation).
/// </summary>
public sealed class TcpDeviceClient : IDeviceClient
{
    private const int MaxConsecutiveMalformed = 10;
    private const int ReadChunk = 4096;

    private readonly IPEndPoint _device;
    private readonly int _responseTimeoutMs;
    private readonly int _failuresBeforeReconnect;
    private readonly CommunicationLog? _log;
    private readonly TimeSpan _backoffBase;
    private readonly TimeSpan _backoffMax;
    private readonly Random _jitter = new();

    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly ConcurrentDictionary<ushort, TaskCompletionSource<byte[]>> _pending = new();
    private readonly List<byte> _rx = new();
    private readonly byte[] _readBuf = new byte[ReadChunk];

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private Task? _reader;
    private ushort _nextSequence;
    private int _consecutiveTimeouts;
    private int _consecutiveMalformed;
    private int _consecutiveConnectFailures;
    private volatile bool _disposed;

    public event EventHandler<TransportLinkState>? LinkChanged;

    public TcpDeviceClient(IPEndPoint deviceEndPoint,
        int responseTimeoutMs = 500,
        int failuresBeforeReconnect = 3,
        CommunicationLog? log = null,
        TimeSpan? backoffBase = null,
        TimeSpan? backoffMax = null)
    {
        _device = deviceEndPoint;
        _responseTimeoutMs = responseTimeoutMs;
        _failuresBeforeReconnect = failuresBeforeReconnect;
        _log = log;
        _backoffBase = backoffBase ?? TimeSpan.FromSeconds(1);
        _backoffMax = backoffMax ?? TimeSpan.FromSeconds(30);
    }

    public async Task<DeviceStatus> PollStatusAsync(CancellationToken ct)
    {
        var payload = await RequestAsync(MessageType.PollStatus, [], MessageType.StatusResponse, ct)
            .ConfigureAwait(false);
        return FrameCodec.DecodeStatusResponse(payload);
    }

    public async Task<IReadOnlyList<CardEvent>> GetCardEventsAsync(uint lastSeq, CancellationToken ct)
    {
        var payload = await RequestAsync(MessageType.GetCardEvents,
            FrameCodec.BuildGetCardEventsPayload(lastSeq), MessageType.CardEventsResponse, ct)
            .ConfigureAwait(false);
        return FrameCodec.DecodeCardEvents(payload);
    }

    public async Task<CommandResponse> SendLockCommandAsync(
        LockCommandKind command, uint unlockDurationSec, CancellationToken ct)
    {
        var payload = await RequestAsync(MessageType.LockCommand,
            FrameCodec.BuildLockPayload(command, unlockDurationSec), MessageType.CommandResponse, ct)
            .ConfigureAwait(false);
        return FrameCodec.DecodeCommandResponse(payload);
    }

    // ---- transport ----

    private async Task<byte[]> RequestAsync(
        MessageType reqType, byte[] reqPayload, MessageType expectedResponse, CancellationToken ct)
    {
        await _requestGate.WaitAsync(ct).ConfigureAwait(false);
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        ushort seq = 0;
        try
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);
            seq = _nextSequence++;
            var frame = FrameCodec.Encode(reqType, seq, reqPayload);
            _pending[seq] = tcs;
            try
            {
                await _stream!.WriteAsync(frame, ct).ConfigureAwait(false);
                _log?.Frame("tx", (byte)reqType, seq, reqPayload.Length, true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _pending.TryRemove(seq, out _);
                KillSocket("send failed");
                throw new DeviceTimeoutException($"Send failed: {ex.Message}");
            }

            try
            {
                var payload = await tcs.Task.WaitAsync(
                    TimeSpan.FromMilliseconds(_responseTimeoutMs), ct).ConfigureAwait(false);
                _consecutiveTimeouts = 0;
                return payload;
            }
            catch (TimeoutException)
            {
                OnResponseTimeout();
                throw new DeviceTimeoutException(
                    $"No response from {_device} within {_responseTimeoutMs}ms ({reqType}, seq={seq})");
            }
        }
        finally
        {
            _pending.TryRemove(seq, out _);
            _requestGate.Release();
        }
    }

    private void OnResponseTimeout()
    {
        if (Interlocked.Increment(ref _consecutiveTimeouts) >= _failuresBeforeReconnect)
        {
            _consecutiveTimeouts = 0;
            KillSocket("consecutive response timeouts");
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_stream is not null) return;
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (_stream is null)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var tcp = new TcpClient();
                    ConfigureKeepAlive(tcp);
                    await tcp.ConnectAsync(_device, ct).ConfigureAwait(false);
                    _tcp = tcp;
                    _stream = tcp.GetStream();
                    _consecutiveConnectFailures = 0;
                    _log?.Event($"connect {_device}");
                    _reader = Task.Run(() => ReaderLoopAsync(), CancellationToken.None);
                    Raise(TransportLinkState.Up);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    int failures = Interlocked.Increment(ref _consecutiveConnectFailures);
                    _log?.Event($"connect failed #{failures}: {ex.Message}");
                    if (failures == 1) Raise(TransportLinkState.Down);
                    Raise(TransportLinkState.Reconnecting);
                    await Task.Delay(NextBackoff(failures), ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private TimeSpan NextBackoff(int failures)
    {
        double exp = Math.Min(_backoffBase.TotalMilliseconds * Math.Pow(2, failures - 1),
            _backoffMax.TotalMilliseconds);
        double j = 1.0 + (_jitter.NextDouble() * 0.4 - 0.2); // ±20%
        return TimeSpan.FromMilliseconds(exp * j);
    }

    private static void ConfigureKeepAlive(TcpClient tcp)
    {
        try
        {
            var s = tcp.Client;
            s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15);
            s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
            s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
        catch (SocketException) { /* option unsupported on this platform — keep going */ }
    }

    private async Task ReaderLoopAsync()
    {
        try
        {
            while (!_disposed)
            {
                int n = await _stream!.ReadAsync(_readBuf).ConfigureAwait(false);
                if (n == 0) break; // peer closed
                _rx.AddRange(_readBuf.AsSpan(0, n).ToArray());
                DrainFrames();
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or NullReferenceException)
        { /* socket error or disposed mid-read — fall through to Down */ }
        KillSocket("reader ended");
    }

    /// <summary>Extract complete frames from the receive buffer; resync on bad magic/CRC.</summary>
    private void DrainFrames()
    {
        while (true)
        {
            // resync: drop bytes until buffer starts with the magic
            int idx = IndexOfMagic(_rx);
            if (idx < 0) { _rx.Clear(); return; }
            if (idx > 0)
            {
                _consecutiveMalformed++;
                _log?.Event($"resync: dropped {idx} bytes");
                _rx.RemoveRange(0, idx);
            }
            if (_rx.Count < FrameCodec.HeaderSize + FrameCodec.CrcSize) return;
            int payloadLen = (_rx[6] << 8) | _rx[7];
            int frameLen = FrameCodec.HeaderSize + payloadLen + FrameCodec.CrcSize;
            if (_rx.Count < frameLen) return;

            var frame = _rx.GetRange(0, frameLen).ToArray();
            _rx.RemoveRange(0, frameLen);
            if (!FrameCodec.TryDecode(frame, out var type, out var seq, out var payload))
            {
                _consecutiveMalformed++;
                _log?.Frame("rx", frame[3], 0, frameLen, false, "bad frame dropped");
                if (_consecutiveMalformed >= MaxConsecutiveMalformed)
                    KillSocket("too many malformed frames");
                continue;
            }
            _consecutiveMalformed = 0;
            _log?.Frame("rx", (byte)type, seq, payload.Length, true);
            if (type == MessageType.ErrorResponse)
            {
                if (_pending.TryRemove(seq, out var t0))
                    t0.TrySetException(new DeviceProtocolException(
                        $"Device ErrorResponse (code {payload.FirstOrDefault()})"));
                continue;
            }
            if (_pending.TryRemove(seq, out var t))
                t.TrySetResult(payload);
            else
                _log?.Event($"unmatched response seq={seq} type={type} dropped");
        }
    }

    private static int IndexOfMagic(List<byte> buf)
    {
        for (int i = 0; i + 1 < buf.Count; i++)
            if (buf[i] == 0x4A && buf[i + 1] == 0x43)
                return i;
        return -1;
    }

    private void KillSocket(string reason)
    {
        var stream = Interlocked.Exchange(ref _stream, null);
        if (stream is null) return;
        _log?.Event($"disconnect: {reason}");
        try { stream.Dispose(); } catch (ObjectDisposedException) { }
        try { _tcp?.Dispose(); } catch (ObjectDisposedException) { }
        _tcp = null;
        _rx.Clear();
        foreach (var kv in _pending)
            if (_pending.TryRemove(kv.Key, out var t))
                t.TrySetException(new DeviceTimeoutException($"link down: {reason}"));
        Raise(TransportLinkState.Down);
    }

    private void Raise(TransportLinkState s) => LinkChanged?.Invoke(this, s);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        KillSocket("dispose");
        if (_reader is not null)
            try { await _reader.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        _requestGate.Dispose();
        _connectGate.Dispose();
        _log?.Dispose();
    }
}
