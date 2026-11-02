using System.Net;
using System.Net.Sockets;
using Monitoring.Protocol;

namespace Monitoring.Net;

/// <summary>
/// UDP request/response client implementing docs/protocol_spec.md §5:
/// sequence matching, 500ms response timeout, up to 2 retransmits with the
/// same sequence, malformed datagrams discarded.
/// A single instance is safe for sequential use (one outstanding request at a time).
/// </summary>
public sealed class UdpDeviceClient : IDeviceClient
{
    private readonly UdpClient _udp;
    private readonly IPEndPoint _device;
    private readonly int _responseTimeoutMs;
    private readonly int _maxRetries;
    private ushort _nextSequence;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public UdpDeviceClient(IPEndPoint deviceEndPoint, int responseTimeoutMs = 500, int maxRetries = 2, int localPort = 0)
    {
        _device = deviceEndPoint;
        _responseTimeoutMs = responseTimeoutMs;
        _maxRetries = maxRetries;
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, localPort));
        _udp.Client.ReceiveTimeout = responseTimeoutMs;
        _udp.Connect(_device);
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

    private async Task<byte[]> RequestAsync(
        MessageType reqType, byte[] reqPayload, MessageType expectedResponse, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ushort seq = _nextSequence++;
            byte[] request = FrameCodec.Encode(reqType, seq, reqPayload);
            for (int attempt = 0; attempt <= _maxRetries; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                await _udp.SendAsync(request, endPoint: null, ct).ConfigureAwait(false);
                var response = await ReceiveMatchingAsync(seq, expectedResponse, ct).ConfigureAwait(false);
                if (response is not null)
                    return response;
            }
            throw new DeviceTimeoutException(
                $"No response from {_device} after {_maxRetries + 1} attempts ({reqType}, seq={seq})");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<byte[]?> ReceiveMatchingAsync(ushort seq, MessageType expected, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_responseTimeoutMs);
        try
        {
            while (true)
            {
                var result = await _udp.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
                if (!FrameCodec.TryDecode(result.Buffer, out var type, out var rxSeq, out var payload))
                    continue; // malformed datagram: discard
                if (type == MessageType.ErrorResponse)
                    throw new DeviceProtocolException(
                        $"Device returned ErrorResponse (code {payload.FirstOrDefault()})");
                if (rxSeq == seq && type == expected)
                    return payload;
                // stale/mismatched datagram: keep waiting within the timeout window
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // per-attempt timeout
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _udp.Dispose();
        _gate.Dispose();
        await Task.CompletedTask;
    }
}
