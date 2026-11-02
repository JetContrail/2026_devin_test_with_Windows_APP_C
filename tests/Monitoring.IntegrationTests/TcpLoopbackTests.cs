// Real-TCP loopback tests: an in-process responder exercises TcpDeviceClient
// over 127.0.0.1 — framing, resync, reconnect, keepalive path.
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Monitoring.Net;
using Monitoring.Protocol;
using Xunit;

namespace Monitoring.IntegrationTests;

public class TcpLoopbackTests : IAsyncLifetime
{
    private TcpListener? _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _connections = new();
    private readonly List<TcpClient> _clients = new();
    private int _port;

    public int DropConnections;          // close the connection after each reply
    public bool InjectGarbage;           // junk bytes before each reply
    public bool CorruptReply;            // break CRC on replies
    public bool NeverReply;              // read requests, never answer
    public int SplitReplyBytes;          // write replies in N-byte chunks (partial frames)

    public Task InitializeAsync() => Task.CompletedTask;

    private int StartServer()
    {
        // keep the port across restarts so the client reconnects to the same endpoint
        _listener = new TcpListener(IPAddress.Loopback, _port);
        _listener.Start();
        _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
        return _port;
    }

    private void StopServer()
    {
        try { _listener?.Stop(); } catch (SocketException) { }
        _listener = null;
        lock (_clients)
        {
            foreach (var c in _clients)
                try { c.Dispose(); } catch (ObjectDisposedException) { }
            _clients.Clear();
        }
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (true)
            {
                var c = await _listener!.AcceptTcpClientAsync(_stop.Token);
                lock (_clients) _clients.Add(c);
                _connections.Add(Task.Run(() => ServeAsync(c)));
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task ServeAsync(TcpClient c)
    {
        try
        {
            var stream = c.GetStream();
            var buf = new byte[4096];
            var rx = new List<byte>();
            while (true)
            {
                int n = await stream.ReadAsync(buf, _stop.Token);
                if (n == 0) return;
                rx.AddRange(buf.AsSpan(0, n).ToArray());
                while (rx.Count >= FrameCodec.HeaderSize + FrameCodec.CrcSize)
                {
                    int len = FrameCodec.HeaderSize + ((rx[6] << 8) | rx[7]) + FrameCodec.CrcSize;
                    if (rx.Count < len) break;
                    var frame = rx.GetRange(0, len).ToArray();
                    rx.RemoveRange(0, len);
                    if (NeverReply) continue;
                    if (!FrameCodec.TryDecode(frame, out var type, out var seq, out var payload))
                        continue;
                    var body = Reply(type, payload);
                    if (body is null) continue;
                    var out_ = FrameCodec.Encode(ReplyType(type), seq, body);
                    if (CorruptReply) out_[^1] ^= 0xFF;
                    if (InjectGarbage) { var junk = new byte[5]; Random.Shared.NextBytes(junk); await stream.WriteAsync(junk); }
                    if (SplitReplyBytes > 0)
                        for (int i = 0; i < out_.Length; i += SplitReplyBytes)
                        {
                            await stream.WriteAsync(out_.AsMemory(i, Math.Min(SplitReplyBytes, out_.Length - i)));
                            await Task.Delay(5);
                        }
                    else
                        await stream.WriteAsync(out_);
                    if (DropConnections > 0 && Interlocked.Decrement(ref DropConnections) >= 0) return;
                }
            }
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
        finally { c.Dispose(); }
    }

    private static byte[]? Reply(MessageType type, byte[] payload) => type switch
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
        StopServer();
        await _stop.CancelAsync();
        try { await Task.WhenAll(_connections); } catch { }
    }

    private TcpDeviceClient NewClient(int timeoutMs = 500, TimeSpan? backoff = null)
        => new(new IPEndPoint(IPAddress.Loopback, _port), timeoutMs,
               failuresBeforeReconnect: 3,
               backoffBase: backoff ?? TimeSpan.FromMilliseconds(20),
               backoffMax: TimeSpan.FromMilliseconds(200));

    private static async Task WaitForAsync(Func<bool> cond, int ms = 10000)
    {
        for (int i = 0; i < ms / 10 && !cond(); i++)
            await Task.Delay(10);
        Assert.True(cond(), "timed out waiting for condition");
    }

    [Fact]
    public async Task PollStatus_RoundTripsOverRealTcp()
    {
        StartServer();
        await using var client = NewClient();
        var s = await client.PollStatusAsync(CancellationToken.None);
        Assert.Equal(LockState.Locked, s.Lock);
        Assert.Equal(DoorState.Closed, s.Door);
    }

    [Fact]
    public async Task LockCommand_RoundTrips()
    {
        StartServer();
        await using var client = NewClient();
        var r = await client.SendLockCommandAsync(LockCommandKind.Unlock, 30, CancellationToken.None);
        Assert.Equal(CommandResult.Ok, r.Result);
        Assert.Equal(LockState.Unlocked, r.Lock);
    }

    [Fact]
    public async Task Timeout_NoReply_Throws()
    {
        StartServer();
        NeverReply = true;
        await using var client = NewClient(timeoutMs: 200);
        await Assert.ThrowsAsync<DeviceTimeoutException>(
            () => client.PollStatusAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PeerClose_AutoReconnects()
    {
        StartServer();
        DropConnections = 1; // close after first reply
        await using var client = NewClient();
        var events = new List<TransportLinkState>();
        client.LinkChanged += (_, s) => events.Add(s);
        var s1 = await client.PollStatusAsync(CancellationToken.None); // up + closed after reply
        await WaitForAsync(() => events.Contains(TransportLinkState.Down));
        var s2 = await client.PollStatusAsync(CancellationToken.None); // reconnects on demand
        Assert.Equal(LockState.Locked, s2.Lock);
    }

    [Fact]
    public async Task PeerStopped_RecoversWhenRestarts()
    {
        StartServer();
        await using var client = NewClient();
        await client.PollStatusAsync(CancellationToken.None);
        StopServer();
        var events = new List<TransportLinkState>();
        client.LinkChanged += (_, s) => events.Add(s);
        // socket death is noticed by the reader loop → Down; then a poll drives the
        // connect loop, which retries forever by design → Reconnecting
        await WaitForAsync(() => events.Contains(TransportLinkState.Down), 8000);
        var poll = client.PollStatusAsync(CancellationToken.None);
        await WaitForAsync(() => events.Contains(TransportLinkState.Reconnecting), 8000);
        await Task.Delay(200);
        StartServer();
        var s = await poll.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(LockState.Locked, s.Lock);
    }

    [Fact]
    public async Task GarbageBytes_ResyncsAndDecodes()
    {
        StartServer();
        InjectGarbage = true;
        await using var client = NewClient();
        var s = await client.PollStatusAsync(CancellationToken.None);
        Assert.Equal(LockState.Locked, s.Lock);
    }

    [Fact]
    public async Task PartialFrames_ReassemblesStream()
    {
        StartServer();
        SplitReplyBytes = 3;
        await using var client = NewClient();
        for (int i = 0; i < 3; i++)
            Assert.Equal(LockState.Locked, (await client.PollStatusAsync(CancellationToken.None)).Lock);
    }

    [Fact]
    public async Task Reconnect100_AllSucceed()
    {
        StartServer();
        await using var client = NewClient();
        for (int i = 0; i < 100; i++)
        {
            DropConnections = 1; // server closes after this reply
            // the close can land before the reply is consumed (RST) — the point is
            // that polling always recovers, so allow a couple of link-down retries
            DeviceStatus? s = null;
            for (int attempt = 0; attempt < 3 && s is null; attempt++)
            {
                try { s = await client.PollStatusAsync(CancellationToken.None); }
                catch (DeviceTimeoutException) { }
            }
            Assert.NotNull(s);
            Assert.Equal(LockState.Locked, s!.Lock);
        }
    }

    [Fact]
    public async Task Connect_RetriesUntilServerAppears()
    {
        // reserve a free port, then close it so nothing listens
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        _port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        await using var client = NewClient();
        var events = new List<TransportLinkState>();
        client.LinkChanged += (_, s) => events.Add(s);
        var poll = client.PollStatusAsync(CancellationToken.None);
        await WaitForAsync(() => events.Contains(TransportLinkState.Reconnecting), 5000);

        _listener = new TcpListener(IPAddress.Loopback, _port);
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);

        var s = await poll.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(LockState.Locked, s.Lock);
    }
}
