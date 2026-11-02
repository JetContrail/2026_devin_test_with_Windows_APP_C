using System.Buffers.Binary;
using Monitoring.Protocol;
using Xunit;

namespace Monitoring.UnitTests;

public class FrameCodecTests
{
    [Fact]
    public void Crc16_KnownVector()
    {
        // CRC-16/CCITT-FALSE of "123456789" = 0x29B1
        Assert.Equal(0x29B1, Crc16.Compute("123456789"u8.ToArray()));
    }

    [Fact]
    public void RoundTrip_EncodeDecode()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var frame = FrameCodec.Encode(MessageType.PollStatus, 0x1234, payload);
        Assert.True(FrameCodec.TryDecode(frame, out var type, out var seq, out var decoded));
        Assert.Equal(MessageType.PollStatus, type);
        Assert.Equal(0x1234, seq);
        Assert.Equal(payload, decoded);
    }

    [Fact]
    public void Decode_RejectsBadMagic()
    {
        var frame = FrameCodec.Encode(MessageType.PollStatus, 1, []);
        frame[0] = 0x00;
        Assert.False(FrameCodec.TryDecode(frame, out _, out _, out _));
    }

    [Fact]
    public void Decode_RejectsCorruptCrc()
    {
        var frame = FrameCodec.Encode(MessageType.PollStatus, 1, [9, 9]);
        frame[^1] ^= 0xFF;
        Assert.False(FrameCodec.TryDecode(frame, out _, out _, out _));
    }

    [Fact]
    public void Decode_RejectsTruncated()
    {
        var frame = FrameCodec.Encode(MessageType.PollStatus, 1, [1, 2, 3]);
        Assert.False(FrameCodec.TryDecode(frame.AsSpan(0, frame.Length - 3), out _, out _, out _));
    }

    [Fact]
    public void StatusResponse_DecodesAllFields()
    {
        var payload = new byte[9];
        payload[0] = (byte)LockState.Unlocked;
        payload[1] = (byte)DoorState.Open;
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(2), 75);
        payload[6] = (byte)CardReaderStatus.Online;
        payload[7] = 3;
        payload[8] = 0x01; // door-left-open bit
        var s = FrameCodec.DecodeStatusResponse(payload);
        Assert.Equal(LockState.Unlocked, s.Lock);
        Assert.Equal(DoorState.Open, s.Door);
        Assert.Equal(75u, s.DoorOpenSeconds);
        Assert.Equal(CardReaderStatus.Online, s.CardReader);
        Assert.Equal(3, s.PendingCardEvents);
        Assert.True(s.DoorLeftOpenAlert);
    }

    [Fact]
    public void CardEvents_RoundTripThroughBuildAndDecode()
    {
        // Encode one event the way FakeDevice does
        var payload = new List<byte> { 1 };
        var head = new byte[9];
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(0), 42);
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(4), 1_700_000_000);
        head[8] = 4;
        payload.AddRange(head);
        payload.AddRange("AB12"u8.ToArray());
        payload.Add(1);

        var events = FrameCodec.DecodeCardEvents(payload.ToArray());
        Assert.Single(events);
        Assert.Equal(42u, events[0].EventSeq);
        Assert.Equal("AB12", events[0].CardId);
        Assert.True(events[0].Accepted);
    }
}
