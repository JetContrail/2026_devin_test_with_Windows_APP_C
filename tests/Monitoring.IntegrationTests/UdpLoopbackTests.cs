// Real-UDP loopback tests: an in-process responder answers UdpDeviceClient
// over 127.0.0.1, exercising the full frame/CRC/sequence/retry path.
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Monitoring.Net;
using Monitoring.Protocol;
using Xunit;

namespace Monitoring.IntegrationTests;

public class UdpLoopbackTests : IAsyncLifetime
{
    private readonly UdpClient _responder = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    public int Port => ((IPEndPoint)_responder.Client.LocalEndPoint!).Port;

    public int DropNext;             // drop the next N requests
    public bool ReplyCorrupt;        // break CRC on replies
    public Func<byte[], MessageType, byte[]?>? Responder;

    public Task InitializeAsync()
    {
        _loop = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                UdpReceiveResult rx;
                try { rx = await _responder.ReceiveAsync(_stop.Token); }
                catch (OperationCanceledException) { return; }
                if (!FrameCodec.TryDecode(rx.Buffer, out var type, out var seq, out var payload))
                    continue;
                if (DropNext > 0)
                {
                    DropNext--;
                    continue;
                }
                var body = Responder?.Invoke(payload, type) ?? DefaultReply(type, payload);
                if (body is null) continue;
                var frame = FrameCodec.Encode(ReplyType(type), seq, body);
                if (ReplyCorrupt) frame[^1] ^= 0xFF;
                await _responder.SendAsync(frame, frame.Length, rx.RemoteEndPoint);
            }
        });
        return Task.CompletedTask;
    }

    private static byte[]? DefaultReply(MessageType type, byte[] payload) => type switch
    {
        MessageType.PollStatus => new byte[]
        {
            (byte)LockState.Locked, (byte)DoorState.Closed, 0, 0, 0, 0,
            (byte)CardReaderStatus.Online, 0, 0,
        },
        MessageType.LockCommand => new byte[] { (byte)CommandResult.Ok, payload[0] == 2 ? (byte)2 : (byte)1 },
        MessageType.GetCardEvents => new byte[] { 0 },
        _ => null,
    };

    private static MessageType ReplyType(MessageType req) => req switch
    {
        MessageType.PollStatus => MessageType.StatusResponse,
        MessageType.LockCommand => MessageType.CommandResponse,
        MessageType.GetCardEvents => MessageType.CardEventsResponse,
        _ => MessageType.ErrorResponse,
    };

    public async Task DisposeAsync()
    {
        await _stop.CancelAsync();
        try { await _loop!; } catch { }
        _responder.Dispose();
    }

    private UdpDeviceClient NewClient(int timeoutMs = 300, int retries = 1)
        => new(new IPEndPoint(IPAddress.Loopback, Port), timeoutMs, retries);

    [Fact]
    public async Task PollStatus_RoundTripsOverRealUdp()
    {
        await using var client = NewClient();
        var s = await client.PollStatusAsync(CancellationToken.None);
        Assert.Equal(LockState.Locked, s.Lock);
        Assert.Equal(DoorState.Closed, s.Door);
        Assert.Equal(CardReaderStatus.Online, s.CardReader);
    }

    [Fact]
    public async Task LockCommand_RoundTrips()
    {
        await using var client = NewClient();
        var r = await client.SendLockCommandAsync(LockCommandKind.Unlock, 30, CancellationToken.None);
        Assert.Equal(CommandResult.Ok, r.Result);
        Assert.Equal(LockState.Unlocked, r.Lock);
    }

    [Fact]
    public async Task Timeout_RetriesThenSucceeds()
    {
        DropNext = 1; // first request lost; retry must succeed on same sequence
        await using var client = NewClient(timeoutMs: 200, retries: 2);
        var s = await client.PollStatusAsync(CancellationToken.None);
        Assert.Equal(LockState.Locked, s.Lock);
    }

    [Fact]
    public async Task Timeout_ThrowsAfterAllRetries()
    {
        DropNext = 10;
        await using var client = NewClient(timeoutMs: 100, retries: 1);
        await Assert.ThrowsAsync<DeviceTimeoutException>(
            () => client.PollStatusAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CorruptReply_IsDiscardedAndRetried()
    {
        ReplyCorrupt = true;
        int calls = 0;
        Responder = (p, t) => { calls++; return DefaultReply(t, p); };
        await using var client = NewClient(timeoutMs: 200, retries: 2);
        await Assert.ThrowsAsync<DeviceTimeoutException>(
            () => client.PollStatusAsync(CancellationToken.None));
        Assert.Equal(3, calls); // initial + 2 retries, all CRC-broken
    }
}
