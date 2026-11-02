// FakeDevice: test double for the door-control device (docs/protocol_spec.md v0.2).
// TCP server. Fault-injection flags reproduce link failure, corruption,
// garbage bytes and latency without a real device.
// Usage: FakeDevice [--port 50001] [--door open|closed] [--lock locked|unlocked]
//        [--reader online|offline] [--drop PCT] [--corrupt] [--garbage]
//        [--delay MS] [--events N] [--once]   (--once: serve one connection then exit)
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Monitoring.Protocol;

var opts = Args.Parse(args);
int port = opts.Get("port", 50001);
var door = opts.Get("door", "closed") == "open" ? DoorState.Open : DoorState.Closed;
var reader = opts.Get("reader", "online") == "offline" ? CardReaderStatus.Offline : CardReaderStatus.Online;
var rng = new Random();
var events = new List<(uint Seq, string CardId, bool Accepted)>();
uint eventSeq = 0;
var state = new State(
    Lock: opts.Get("lock", "locked") == "unlocked" ? LockState.Unlocked : LockState.Locked,
    DoorOpenedUtc: door == DoorState.Open ? DateTimeOffset.UtcNow : null);
var rxBuf = new List<byte>();

using var listener = new TcpListener(IPAddress.Any, port);
listener.Start();
Console.WriteLine($"FakeDevice listening on TCP {port} (door={opts.Get("door", "closed")}, lock={(byte)state.Lock})");

while (true)
{
    var client = await listener.AcceptTcpClientAsync();
    Console.WriteLine($"client connected: {client.Client.RemoteEndPoint}");
    try
    {
        await ServeAsync(client);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"connection ended: {ex.Message}");
    }
    client.Dispose();
    if (opts.Flag("once")) break;
}

async Task ServeAsync(TcpClient client)
{
    var stream = client.GetStream();
    var buf = new byte[4096];
    while (true)
    {
        int n = await stream.ReadAsync(buf);
        if (n == 0) return;
        rxBuf.AddRange(buf.AsSpan(0, n).ToArray());
        while (TryExtractFrame(out var frame))
        {
            if (opts.Get("drop", 0) > 0 && rng.Next(100) < opts.Get("drop", 0))
                return; // simulate peer/link death: kill the connection
            int delay = opts.Get("delay", 0);
            if (delay > 0) await Task.Delay(delay);
            if (opts.Flag("garbage") && rng.Next(4) == 0)
            {
                var junk = new byte[7];
                rng.NextBytes(junk);
                await stream.WriteAsync(junk); // malformed bytes before the reply
            }
            if (!FrameCodec.TryDecode(frame, out var type, out var seq, out var payload))
                continue;

            var (responseType, responsePayload) = Handle(type, payload);
            var out_ = FrameCodec.Encode(responseType, seq, responsePayload);
            if (opts.Flag("corrupt") && rng.Next(2) == 0)
                out_[^1] ^= 0xFF;
            await stream.WriteAsync(out_);
        }
    }
}

bool TryExtractFrame(out byte[] frame)
{
    frame = [];
    if (rxBuf.Count < FrameCodec.HeaderSize + FrameCodec.CrcSize) return false;
    int len = FrameCodec.HeaderSize + ((rxBuf[6] << 8) | rxBuf[7]) + FrameCodec.CrcSize;
    if (rxBuf.Count < len) return false;
    frame = rxBuf.GetRange(0, len).ToArray();
    rxBuf.RemoveRange(0, len);
    return true;
}

(MessageType, byte[]) Handle(MessageType type, byte[] payload) => type switch
{
    MessageType.PollStatus => (MessageType.StatusResponse, BuildStatus()),
    MessageType.GetCardEvents => (MessageType.CardEventsResponse,
        BuildEvents(payload.Length >= 4 ? BinaryPrimitives.ReadUInt32BigEndian(payload) : 0)),
    MessageType.LockCommand => HandleLock(payload),
    _ => (MessageType.ErrorResponse, [1, .. "unknown message type"u8]),
};

byte[] BuildStatus()
{
    var p = new byte[9];
    p[0] = (byte)state.Lock;
    p[1] = (byte)door;
    uint openSec = 0;
    if (door == DoorState.Open && state.DoorOpenedUtc is { } t0)
        openSec = (uint)Math.Max(0, (DateTimeOffset.UtcNow - t0).TotalSeconds);
    BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(2), openSec);
    p[6] = (byte)reader;
    if (opts.Get("events", 0) > 0 && rng.Next(10) < opts.Get("events", 0))
        events.Add((++eventSeq, $"CARD-{rng.Next(1000, 9999)}", rng.Next(4) != 0));
    p[7] = (byte)Math.Min(events.Count, 255);
    p[8] = 0;
    return p;
}

byte[] BuildEvents(uint lastSeq)
{
    var list = events.Where(e => e.Seq > lastSeq).ToList();
    var p = new List<byte> { (byte)list.Count };
    foreach (var e in list)
    {
        var head = new byte[9];
        BinaryPrimitives.WriteUInt32BigEndian(head, e.Seq);
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(4), (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        head[8] = (byte)e.CardId.Length;
        p.AddRange(head);
        p.AddRange(System.Text.Encoding.UTF8.GetBytes(e.CardId));
        p.Add(e.Accepted ? (byte)1 : (byte)0);
    }
    events.RemoveAll(e => list.Contains(e));
    return p.ToArray();
}

(MessageType, byte[]) HandleLock(byte[] payload)
{
    if (payload.Length < 1)
        return (MessageType.ErrorResponse, [2, .. "bad lock command"u8]);
    var cmd = (LockCommandKind)payload[0];
    if (cmd == LockCommandKind.Lock) state = state with { Lock = LockState.Locked };
    else if (cmd == LockCommandKind.Unlock) state = state with { Lock = LockState.Unlocked };
    return (MessageType.CommandResponse, [(byte)CommandResult.Ok, (byte)state.Lock]);
}

sealed record State(LockState Lock, DateTimeOffset? DoorOpenedUtc);

static class Args
{
    public static Dictionary<string, string> Parse(string[] args)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            var k = args[i].TrimStart('-');
            d[k] = (i + 1 < args.Length && !args[i + 1].StartsWith('-')) ? args[++i] : "true";
        }
        return d;
    }
    public static string Get(this Dictionary<string, string> d, string k, string def)
        => d.TryGetValue(k, out var v) ? v : def;
    public static int Get(this Dictionary<string, string> d, string k, int def)
        => d.TryGetValue(k, out var s) && int.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : def;
    public static bool Flag(this Dictionary<string, string> d, string k) => d.ContainsKey(k);
}
