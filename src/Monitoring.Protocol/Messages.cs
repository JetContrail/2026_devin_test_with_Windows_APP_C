namespace Monitoring.Protocol;

public enum MessageType : byte
{
    PollStatus = 0x01,
    GetCardEvents = 0x02,
    LockCommand = 0x10,
    ErrorResponse = 0x7F,
    StatusResponse = 0x81,
    CardEventsResponse = 0x82,
    CommandResponse = 0x90,
}

public enum LockState : byte { Unknown = 0, Locked = 1, Unlocked = 2, Jammed = 3 }
public enum DoorState : byte { Unknown = 0, Closed = 1, Open = 2 }
public enum CardReaderStatus : byte { Offline = 0, Online = 1, Error = 2 }
public enum LockCommandKind : byte { Lock = 1, Unlock = 2 }
public enum CommandResult : byte { Ok = 0, Rejected = 1, Timeout = 2, Jammed = 3 }

public sealed record DeviceStatus(
    LockState Lock,
    DoorState Door,
    uint DoorOpenSeconds,
    CardReaderStatus CardReader,
    byte PendingCardEvents,
    bool DoorLeftOpenAlert,
    bool DeviceWarning);

public sealed record CardEvent(
    uint EventSeq,
    DateTimeOffset Timestamp,
    string CardId,
    bool Accepted);

public sealed record CommandResponse(CommandResult Result, LockState Lock);
