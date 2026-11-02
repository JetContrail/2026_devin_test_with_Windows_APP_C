using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
    private readonly DeviceSecurityOptions _security;
    private readonly Random _jitter = new();

    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private sealed record PendingRequest(TaskCompletionSource<byte[]> Tcs, MessageType Expected);
    private readonly ConcurrentDictionary<ushort, PendingRequest> _pending = new();
    private TcpClient? _tcp;
    private Stream? _stream;
    private Task? _reader;
    private int _connGeneration;
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
        TimeSpan? backoffMax = null,
        DeviceSecurityOptions? security = null)
    {
        _device = deviceEndPoint;
        _responseTimeoutMs = responseTimeoutMs;
        _failuresBeforeReconnect = failuresBeforeReconnect;
        _log = log;
        _backoffBase = backoffBase ?? TimeSpan.FromSeconds(1);
        _backoffMax = backoffMax ?? TimeSpan.FromSeconds(30);
        _security = security ?? DeviceSecurityOptions.None;
    }

    public async Task<DeviceStatus> PollStatusAsync(CancellationToken ct)
    {
        var payload = await RequestAsync(MessageType.PollStatus, [], MessageType.StatusResponse, ct)
            .ConfigureAwait(false);
        return FrameCodec.DecodeStatusResponse(payload);
    }

    public async Task<CardEventsPage> GetCardEventsAsync(uint lastSeq, CancellationToken ct)
    {
        var payload = await RequestAsync(MessageType.GetCardEvents,
            FrameCodec.BuildGetCardEventsPayload(lastSeq), MessageType.CardEventsResponse, ct)
            .ConfigureAwait(false);
        return FrameCodec.DecodeCardEventsPage(payload);
    }

    /// <summary>Control commands must not ride an endless reconnect: if the link is
    /// down, the request fails fast instead of executing a stale command on recovery.</summary>
    private static readonly TimeSpan CommandConnectBudget = TimeSpan.FromSeconds(2);

    public async Task<CommandResponse> SendLockCommandAsync(
        LockCommandKind command, uint unlockDurationSec, CancellationToken ct)
    {
        var payload = await RequestAsync(MessageType.LockCommand,
            FrameCodec.BuildLockPayload(command, unlockDurationSec), MessageType.CommandResponse, ct,
            CommandConnectBudget).ConfigureAwait(false);
        return FrameCodec.DecodeCommandResponse(payload);
    }

    // ---- transport ----

    private async Task<byte[]> RequestAsync(
        MessageType reqType, byte[] reqPayload, MessageType expectedResponse, CancellationToken ct,
        TimeSpan? connectBudget = null)
    {
        // a bounded request (control command) gets a deadline covering the whole
        // pre-send path: gate wait, reconnect loop, TLS handshake and auth — an
        // expired command must fail, never execute late on recovery
        using var deadline = connectBudget.HasValue
            ? CancellationTokenSource.CreateLinkedTokenSource(ct) : null;
        if (connectBudget.HasValue) deadline!.CancelAfter(connectBudget.Value);
        var effCt = deadline?.Token ?? ct;
        try
        {
            await _requestGate.WaitAsync(effCt).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DeviceTimeoutException("command expired waiting for the request gate");
        }
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        ushort seq = 0;
        try
        {
            try
            {
                await EnsureConnectedAsync(effCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new DeviceTimeoutException("command expired waiting for the link");
            }
            seq = _nextSequence++;
            var frame = FrameCodec.Encode(reqType, seq, reqPayload);
            _pending[seq] = new PendingRequest(tcs, expectedResponse);
            // check the deadline right before writing: a command that expired during
            // gate/connect/TLS/auth must never reach the wire
            try
            {
                effCt.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _pending.TryRemove(seq, out _);
                throw new DeviceTimeoutException("command expired before send");
            }
            try
            {
                await _stream!.WriteAsync(frame, effCt).ConfigureAwait(false);
                _log?.Frame("tx", (byte)reqType, seq, reqPayload.Length, true);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // deadline hit mid-write: delivery is unknown, and the stream may hold
                // a partial frame — drop the connection and report it
                _pending.TryRemove(seq, out _);
                KillSocket("command deadline during send");
                throw new DeviceTimeoutException(
                    "command deadline expired while sending; the device may have received it");
            }
            catch (Exception ex)
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

    /// <summary>Each connect attempt (TCP + TLS + auth) is bounded so a peer that
    /// stalls mid-handshake cannot freeze monitoring forever.</summary>
    private static readonly TimeSpan ConnectAttemptTimeout = TimeSpan.FromSeconds(5);

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_stream is not null) return;
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (_stream is null)
            {
                ct.ThrowIfCancellationRequested();
                TcpClient? tcp = null;
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(ConnectAttemptTimeout);
                try
                {
                    tcp = new TcpClient();
                    ConfigureKeepAlive(tcp);
                    await tcp.ConnectAsync(_device, attempt.Token).ConfigureAwait(false);
                    Stream stream = tcp.GetStream();
                    if (_security.UseTls)
                        stream = await EstablishTlsAsync(stream, attempt.Token).ConfigureAwait(false);
                    _tcp = tcp;
                    _stream = stream;
                    _log?.Event($"connect {_device}");
                    // the reader owns THIS connection's stream/buffer — a dying
                    // reader from a previous generation can never kill the new link
                    var gen = Interlocked.Increment(ref _connGeneration);
                    var owned = stream;
                    _reader = Task.Run(() => ReaderLoopAsync(owned, gen), CancellationToken.None);
                    if (_security.Psk is { } psk)
                    {
                        await AuthenticateAsync(psk, attempt.Token).ConfigureAwait(false);
                        _log?.Event("auth ok");
                    }
                    // reset only after auth succeeds so persistent failures still back off
                    _consecutiveConnectFailures = 0;
                    Raise(TransportLinkState.Up);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    tcp?.Dispose();
                    KillSocket("connect attempt timed out");
                    int f = Interlocked.Increment(ref _consecutiveConnectFailures);
                    _log?.Event($"connect attempt timed out #{f}");
                    if (f == 1) Raise(TransportLinkState.Down);
                    Raise(TransportLinkState.Reconnecting);
                    await Task.Delay(NextBackoff(f), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { tcp?.Dispose(); throw; }
                catch (Exception ex)
                {
                    tcp?.Dispose();
                    KillSocket($"connect/auth failed: {ex.Message}");
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

    /// <summary>TLS handshake + certificate pinning (spec §9).</summary>
    private async Task<Stream> EstablishTlsAsync(Stream inner, CancellationToken ct)
    {
        var ssl = new SslStream(inner, leaveInnerStreamOpen: false);
        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = _security.TargetHost ?? _device.Address.ToString(),
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                // pinning replaces chain+revocation trust; without a pin, enforce
                // chain validation AND online revocation checking
                CertificateRevocationCheckMode = _security.ExpectedCertThumbprint is null
                    ? X509RevocationMode.Online : X509RevocationMode.NoCheck,
                // when a thumbprint pin is configured it IS the trust decision:
                // embedded devices use self-signed certs that chain-validation rejects
                RemoteCertificateValidationCallback =
                    _security.ExpectedCertThumbprint is { } expectedPin
                        ? (_, cert, _, _) => cert is X509Certificate2 c2
                            && DateTime.Now >= c2.NotBefore && DateTime.Now <= c2.NotAfter
                            && string.Equals(c2.GetCertHashString(), expectedPin,
                                StringComparison.OrdinalIgnoreCase)
                        : null,
            }, ct).ConfigureAwait(false);
        }
        catch
        {
            ssl.Dispose();
            throw;
        }
        var thumb = ssl.RemoteCertificate?.GetCertHashString();
        if (_security.ExpectedCertThumbprint is { } expected &&
            !string.Equals(thumb, expected, StringComparison.OrdinalIgnoreCase))
        {
            ssl.Dispose();
            throw new DeviceProtocolException(
                $"device certificate thumbprint mismatch (got {thumb}, expected {expected})");
        }
        _log?.Event($"tls ok ({ssl.SslProtocol}, cert {thumb})");
        return ssl;
    }

    /// <summary>AuthHello(nonce) → AuthAck(HMAC-SHA256(psk, nonce)) before link is Up.</summary>
    private async Task AuthenticateAsync(byte[] psk, CancellationToken ct)
    {
        var nonce = AuthCode.NewNonce();
        var hello = nonce.Concat(AuthCode.ClientProof(psk, nonce)).ToArray();
        ushort seq = _nextSequence++;
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[seq] = new PendingRequest(tcs, MessageType.AuthAck);
        try
        {
            await _stream!.WriteAsync(FrameCodec.Encode(MessageType.AuthHello, seq, hello), ct)
                .ConfigureAwait(false);
            _log?.Frame("tx", (byte)MessageType.AuthHello, seq, hello.Length, true);
            var ack = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            if (!AuthCode.VerifyServer(psk, nonce, ack))
                throw new DeviceProtocolException("device rejected authentication");
        }
        finally
        {
            _pending.TryRemove(seq, out _);
        }
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

    private async Task ReaderLoopAsync(Stream stream, int generation)
    {
        var readBuf = new byte[ReadChunk];
        var rx = new List<byte>();
        try
        {
            while (!_disposed)
            {
                int n = await stream.ReadAsync(readBuf).ConfigureAwait(false);
                if (n == 0) break; // peer closed
                rx.AddRange(readBuf.AsSpan(0, n).ToArray());
                DrainFrames(rx);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or NullReferenceException)
        { /* socket error or disposed mid-read — fall through to Down */ }
        // only kill the link if this reader still owns the current connection —
        // a stale reader ending late must not tear down a newer connection
        KillSocketIfCurrent(stream, generation, "reader ended");
    }

    /// <summary>Extract complete frames from the receive buffer; resync on bad magic/CRC.</summary>
    private void DrainFrames(List<byte> rx)
    {
        while (true)
        {
            // resync: drop bytes until buffer starts with the magic
            int idx = IndexOfMagic(rx);
            if (idx < 0)
            {
                // keep a trailing 0x4A — it may be the first half of a split magic
                bool partialMagic = rx.Count > 0 && rx[^1] == 0x4A;
                rx.Clear();
                if (partialMagic) rx.Add(0x4A);
                return;
            }
            if (idx > 0)
            {
                _consecutiveMalformed++;
                _log?.Event($"resync: dropped {idx} bytes");
                rx.RemoveRange(0, idx);
            }
            if (rx.Count < FrameCodec.HeaderSize + FrameCodec.CrcSize) return;
            int payloadLen = (rx[6] << 8) | rx[7];
            if (payloadLen > FrameCodec.MaxPayload)
            {
                // corrupt length field — drop only the magic byte and rescan
                _consecutiveMalformed++;
                _log?.Event($"bad payloadLen {payloadLen}; dropping magic byte");
                rx.RemoveAt(0);
                if (_consecutiveMalformed >= MaxConsecutiveMalformed)
                    KillSocket("too many malformed frames");
                continue;
            }
            int frameLen = FrameCodec.HeaderSize + payloadLen + FrameCodec.CrcSize;
            if (rx.Count < frameLen) return;

            var frame = rx.GetRange(0, frameLen).ToArray();
            if (!FrameCodec.TryDecode(frame, out var type, out var seq, out var payload))
            {
                _consecutiveMalformed++;
                _log?.Frame("rx", frame[3], 0, frameLen, false, "bad frame dropped");
                // drop only the leading magic byte so a valid frame hiding inside
                // the bogus length can still be recovered
                rx.RemoveAt(0);
                if (_consecutiveMalformed >= MaxConsecutiveMalformed)
                    KillSocket("too many malformed frames");
                continue;
            }
            rx.RemoveRange(0, frameLen);
            _consecutiveMalformed = 0;
            _log?.Frame("rx", (byte)type, seq, payload.Length, true);
            if (type == MessageType.ErrorResponse)
            {
                if (_pending.TryRemove(seq, out var t0))
                    t0.Tcs.TrySetException(new DeviceProtocolException(
                        $"Device ErrorResponse (code {payload.FirstOrDefault()})"));
                continue;
            }
            if (_pending.TryGetValue(seq, out var pending) && pending.Expected != type)
            {
                // same sequence but wrong message type — drop, let the request time out
                _log?.Event($"type mismatch seq={seq}: got {type}, expected {pending.Expected}");
                continue;
            }
            if (_pending.TryRemove(seq, out var t))
                t.Tcs.TrySetResult(payload);
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
        DoKillSocket(stream, reason);
    }

    /// <summary>Kill only if (stream, generation) is still the live connection.
    /// Generation is a belt-and-braces guard alongside stream identity.</summary>
    private void KillSocketIfCurrent(Stream stream, int generation, string reason)
    {
        if (generation != Volatile.Read(ref _connGeneration) || !ReferenceEquals(stream, _stream))
            return;
        KillSocket(reason);
    }

    private void DoKillSocket(Stream stream, string reason)
    {
        _log?.Event($"disconnect: {reason}");
        // dispose the socket first: it unblocks any pending stream read
        // (SslStream.Dispose can otherwise wait on a read that never returns)
        try { _tcp?.Dispose(); } catch (ObjectDisposedException) { }
        _tcp = null;
        try { stream.Dispose(); } catch (ObjectDisposedException) { }
        foreach (var kv in _pending)
            if (_pending.TryRemove(kv.Key, out var t))
                t.Tcs.TrySetException(new DeviceTimeoutException($"link down: {reason}"));
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
