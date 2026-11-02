using Monitoring.Protocol;

namespace Monitoring.Net;

/// <summary>Request/response transport to the door-control device. UI and polling logic
/// depend only on this abstraction so they can be unit-tested without a socket.</summary>
public interface IDeviceClient : IAsyncDisposable
{
    /// <summary>Transport link state changes (Up on connect, Down on loss,
    /// Reconnecting while the auto-reconnect loop runs).</summary>
    event EventHandler<TransportLinkState>? LinkChanged;

    Task<DeviceStatus> PollStatusAsync(CancellationToken ct);
    /// <summary>Returns events newer than lastSeq plus the device's BootId so the
    /// caller can detect a rebooted device (spec v0.4).</summary>
    Task<CardEventsPage> GetCardEventsAsync(uint lastSeq, CancellationToken ct);
    Task<CommandResponse> SendLockCommandAsync(LockCommandKind command, uint unlockDurationSec, CancellationToken ct);
}

public enum TransportLinkState { Up, Down, Reconnecting }

public sealed class DeviceTimeoutException : Exception
{
    public DeviceTimeoutException(string message) : base(message) { }
}

public sealed class DeviceProtocolException : Exception
{
    public DeviceProtocolException(string message) : base(message) { }
}
