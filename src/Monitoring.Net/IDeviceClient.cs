using Monitoring.Protocol;

namespace Monitoring.Net;

/// <summary>Request/response transport to the door-control device. UI and polling logic
/// depend only on this abstraction so they can be unit-tested without a socket.</summary>
public interface IDeviceClient : IAsyncDisposable
{
    Task<DeviceStatus> PollStatusAsync(CancellationToken ct);
    Task<IReadOnlyList<CardEvent>> GetCardEventsAsync(uint lastSeq, CancellationToken ct);
    Task<CommandResponse> SendLockCommandAsync(LockCommandKind command, uint unlockDurationSec, CancellationToken ct);
}

public sealed class DeviceTimeoutException : Exception
{
    public DeviceTimeoutException(string message) : base(message) { }
}

public sealed class DeviceProtocolException : Exception
{
    public DeviceProtocolException(string message) : base(message) { }
}
