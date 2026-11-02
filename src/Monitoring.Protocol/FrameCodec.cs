using System.Buffers.Binary;
using System.Text;

namespace Monitoring.Protocol;

/// <summary>Encode/decode of the JC binary frame (see docs/protocol_spec.md).</summary>
public static class FrameCodec
{
    public const ushort Magic = 0x4A43;
    public const byte Version = 0x01;
    public const int HeaderSize = 8;
    public const int CrcSize = 2;
    public const int MaxPayload = 1472;

    public static byte[] Encode(MessageType type, ushort sequence, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[HeaderSize + payload.Length + CrcSize];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0), Magic);
        frame[2] = Version;
        frame[3] = (byte)type;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), sequence);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(6), (ushort)payload.Length);
        payload.CopyTo(frame.AsSpan(HeaderSize));
        BinaryPrimitives.WriteUInt16BigEndian(
            frame.AsSpan(HeaderSize + payload.Length),
            Crc16.Compute(frame.AsSpan(0, HeaderSize + payload.Length)));
        return frame;
    }

    /// <summary>Returns false when the datagram is malformed (bad magic/version/length/CRC).</summary>
    public static bool TryDecode(ReadOnlySpan<byte> datagram,
        out MessageType type, out ushort sequence, out byte[] payload)
    {
        type = default; sequence = default; payload = [];
        if (datagram.Length < HeaderSize + CrcSize)
            return false;
        if (BinaryPrimitives.ReadUInt16BigEndian(datagram) != Magic || datagram[2] != Version)
            return false;
        int payloadLen = BinaryPrimitives.ReadUInt16BigEndian(datagram.Slice(6));
        if (datagram.Length != HeaderSize + payloadLen + CrcSize)
            return false;
        int bodyLen = HeaderSize + payloadLen;
        ushort expected = BinaryPrimitives.ReadUInt16BigEndian(datagram.Slice(bodyLen));
        if (Crc16.Compute(datagram.Slice(0, bodyLen)) != expected)
            return false;
        type = (MessageType)datagram[3];
        sequence = BinaryPrimitives.ReadUInt16BigEndian(datagram.Slice(4));
        payload = datagram.Slice(HeaderSize, payloadLen).ToArray();
        return true;
    }

    public static byte[] EncodeLockCommand(LockCommandKind command, uint unlockDurationSec, ushort sequence)
        => Encode(MessageType.LockCommand, sequence, BuildLockPayload(command, unlockDurationSec));

    public static byte[] BuildLockPayload(LockCommandKind command, uint unlockDurationSec)
    {
        var p = new byte[5];
        p[0] = (byte)command;
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(1), unlockDurationSec);
        return p;
    }

    public static DeviceStatus DecodeStatusResponse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 9)
            throw new FormatException($"StatusResponse payload too short: {payload.Length}");
        byte flags = payload[8];
        return new DeviceStatus(
            Lock: (LockState)payload[0],
            Door: (DoorState)payload[1],
            DoorOpenSeconds: BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(2)),
            CardReader: (CardReaderStatus)payload[6],
            PendingCardEvents: payload[7],
            DoorLeftOpenAlert: (flags & 0x01) != 0,
            DeviceWarning: (flags & 0x02) != 0);
    }

    public static CommandResponse DecodeCommandResponse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2)
            throw new FormatException($"CommandResponse payload too short: {payload.Length}");
        return new CommandResponse((CommandResult)payload[0], (LockState)payload[1]);
    }

    public static byte[] BuildGetCardEventsPayload(uint lastSeq)
    {
        var p = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(p, lastSeq);
        return p;
    }

    /// <summary>CardEventsResponse payload (spec v0.4): u32 bootId | u8 count | events.
    /// bootId identifies the device's event-sequence generation; a reboot must change it
    /// so the monitor knows to reset its lastSeq cursor.</summary>
    public static CardEventsPage DecodeCardEventsPage(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 5)
            throw new FormatException($"CardEventsResponse payload too short: {payload.Length}");
        uint bootId = BinaryPrimitives.ReadUInt32BigEndian(payload);
        var events = new List<CardEvent>();
        int count = payload[4];
        int pos = 5;
        for (int i = 0; i < count; i++)
        {
            if (pos + 9 > payload.Length)
                throw new FormatException("CardEventsResponse truncated");
            uint seq = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(pos));
            uint ts = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(pos + 4));
            int idLen = payload[pos + 8];
            if (pos + 10 + idLen > payload.Length)
                throw new FormatException("CardEventsResponse truncated");
            string cardId = Encoding.UTF8.GetString(payload.Slice(pos + 9, idLen));
            bool accepted = payload[pos + 9 + idLen] == 1;
            events.Add(new CardEvent(seq, DateTimeOffset.FromUnixTimeSeconds(ts), cardId, accepted));
            pos += 10 + idLen;
        }
        return new CardEventsPage(bootId, events);
    }
}
