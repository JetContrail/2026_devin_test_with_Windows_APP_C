// FakeDevice: test double for the door-control device (docs/protocol_spec.md).
// Usage: FakeDevice [--port 50001] [--door open|closed] [--lock locked|unlocked]
//        [--reader online|offline] [--drop PCT] [--corrupt] [--delay MS] [--events N]
// Fault-injection flags let integration tests and manual trials reproduce
// packet loss, corruption and latency without a real device.
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

using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
var state = new State(
    Lock: opts.Get("lock", "locked") == "unlocked" ? LockState.Unlocked : LockState.Locked,
    DoorOpenedUtc: door == DoorState.Open ? DateTimeOffset.UtcNow : null);
Console.WriteLine($"FakeDevice listening on UDP {port} (door={opts.Get("door", "closed")}, lock={(byte)state.Lock}, reader={opts.Get("reader", "online")})");

while (true)
{
    var rx = await udp.ReceiveAsync();
    int drop = opts.Get("drop", 0);
    if (drop > 0 && rng.Next(100) < drop)
        continue; // simulate packet loss
    int delay = opts.Get("delay", 0);
    if (delay > 0)
        await Task.Delay(delay);

    if (!FrameCodec.TryDecode(rx.Buffer, out var type, out var seq, out var payload))
        continue; // ignore malformed requests

    byte[] responsePayload;
    MessageType responseType;
    switch (type)
    {
        case MessageType.PollStatus:
            responseType = MessageType.StatusResponse;
            responsePayload = BuildStatus();
            break;
        case MessageType.GetCardEvents:
            uint lastSeq = payload.Length >= 4 ? BinaryPrimitives.ReadUInt32BigEndian(payload) : 0;
            responseType = MessageType.CardEventsResponse;
            responsePayload = BuildEvents(lastSeq);
            break;
        case MessageType.LockCommand:
            (responseType, responsePayload) = HandleLock(payload);
            break;
        default:
            responseType = MessageType.ErrorResponse;
            responsePayload = [1, .."unknown message type"u8];
            break;
    }

    var frame = FrameCodec.Encode(responseType, seq, responsePayload);
    if (opts.Flag("corrupt") && rng.Next(2) == 0)
        frame[^1] ^= 0xFF; // break the CRC
    await udp.SendAsync(frame, frame.Length, rx.RemoteEndPoint);
}

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
    p[7] = (byte)Math.Min(events.Count, 255);
    p[8] = 0;
    if (opts.Get("events", 0) > 0 && rng.Next(10) < opts.Get("events", 0))
    {
        events.Add((++eventSeq, $"CARD-{rng.Next(1000, 9999)}", rng.Next(4) != 0));
        p[7] = (byte)Math.Min(events.Count, 255);
    }
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
    events.RemoveAll(e => e.Seq <= lastSeq || list.Contains(e));
    return p.ToArray();
}

(MessageType, byte[]) HandleLock(byte[] payload)
{
    if (payload.Length < 1)
        return (MessageType.ErrorResponse, [2, .."bad lock command"u8]);
    var cmd = (LockCommandKind)payload[0];
    if (cmd == LockCommandKind.Lock) state = state with { Lock = LockState.Locked };
    else if (cmd == LockCommandKind.Unlock) state = state with { Lock = LockState.Unlocked };
    var p = new byte[] { (byte)CommandResult.Ok, (byte)state.Lock };
    return (MessageType.CommandResponse, p);
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
