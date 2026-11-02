using Monitoring.Net;
using Monitoring.Protocol;

namespace Monitoring.Core;

public enum ConnectionState { Disconnected, Connecting, Connected, Reconnecting, Lost }

public sealed record MonitorOptions
{
    /// <summary>Poll period (protocol spec §5: default 1000ms, min 200ms).</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>Consecutive poll failures before ConnectionState becomes Lost.</summary>
    public int LostAfterFailures { get; init; } = 3;
    /// <summary>Door-open duration that raises a left-open alert.</summary>
    public TimeSpan DoorOpenAlertThreshold { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>Automatic re-lock delay sent with Unlock commands (0 = stay unlocked).</summary>
    public uint UnlockDurationSec { get; init; }
}

public sealed record MonitorSnapshot(
    ConnectionState Connection,
    DeviceStatus? Status,
    bool DoorLeftOpen,
    DateTimeOffset? LastSuccessfulPollUtc);

/// <summary>
/// Polling engine: polls the device on a timer, tracks connection state,
/// fetches pending card events, and raises door-left-open alerts.
/// All notifications are raised as events off the caller's thread; the WPF
/// app marshals them to the UI thread via Dispatcher.
/// </summary>
public sealed class DeviceMonitor : IAsyncDisposable
{
    private readonly IDeviceClient _client;
    private readonly MonitorOptions _options;
    private readonly Func<DateTimeOffset> _clock;
    private CancellationTokenSource? _run;
    private Task? _loop;
    private uint _lastCardEventSeq;
    private int _consecutiveFailures;
    private bool _lastOpenDoor;
    private ConnectionState _connection = ConnectionState.Disconnected;

    public DeviceMonitor(IDeviceClient client, MonitorOptions? options = null, Func<DateTimeOffset>? clock = null)
    {
        _client = client;
        _options = options ?? new MonitorOptions();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _client.LinkChanged += (_, s) =>
        {
            switch (s)
            {
                case TransportLinkState.Up:
                    // transport up is not proof the device responds: only a
                    // successful poll may clear Lost/Connected (see OnPollSucceeded)
                    if (_connection != ConnectionState.Lost)
                        SetConnection(ConnectionState.Connecting);
                    break;
                case TransportLinkState.Down:
                    // Lost persists across failed reconnect attempts — only a
                    // successful poll proves the device recovered
                    if (_connection != ConnectionState.Lost)
                        SetConnection(ConnectionState.Disconnected);
                    break;
                case TransportLinkState.Reconnecting:
                    // transport keeps retrying; surface as Lost once polls keep failing
                    if (_connection != ConnectionState.Lost)
                        SetConnection(ConnectionState.Reconnecting);
                    break;
            }
        };
    }

    /// <summary>Display name for the device (multi-device setups).</summary>
    public string Name { get; set; } = "";

    public ConnectionState Connection => _connection;
    public DeviceStatus? LastStatus { get; private set; }
    public DateTimeOffset? LastSuccessfulPollUtc { get; private set; }
    public bool DoorLeftOpen { get; private set; }

    public event EventHandler<ConnectionState>? ConnectionChanged;
    public event EventHandler<DeviceStatus>? StatusReceived;
    public event EventHandler<CardEvent>? CardEventReceived;
    public event EventHandler<bool>? DoorLeftOpenChanged;
    public event EventHandler<Exception>? PollError;

    public void Start()
    {
        if (_run is not null) return;
        _run = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_run.Token));
    }

    public async Task StopAsync()
    {
        if (_run is null) return;
        await _run.CancelAsync().ConfigureAwait(false);
        try { await _loop!.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _run.Dispose();
        _run = null;
        _loop = null;
        SetConnection(ConnectionState.Disconnected);
    }

    /// <summary>Sends a lock/unlock command. Throws on transport failure; Result reflects the device.</summary>
    public Task<CommandResponse> SetLockAsync(bool unlock, CancellationToken ct)
        => _client.SendLockCommandAsync(
            unlock ? LockCommandKind.Unlock : LockCommandKind.Lock,
            unlock ? _options.UnlockDurationSec : 0, ct);

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var status = await _client.PollStatusAsync(ct).ConfigureAwait(false);
                OnPollSucceeded(status, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                OnPollFailed(ex);
            }
            try { await Task.Delay(_options.PollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void OnPollSucceeded(DeviceStatus status, CancellationToken ct)
    {
        _consecutiveFailures = 0;
        LastStatus = status;
        LastSuccessfulPollUtc = _clock();
        SetConnection(ConnectionState.Connected);
        StatusReceived?.Invoke(this, status);
        UpdateDoorLeftOpen(status);
        if (status.PendingCardEvents > 0)
            _ = FetchCardEventsAsync(ct);
    }

    private void OnPollFailed(Exception ex)
    {
        PollError?.Invoke(this, ex);
        if (Interlocked.Increment(ref _consecutiveFailures) >= _options.LostAfterFailures)
            SetConnection(ConnectionState.Lost);
    }

    private async Task FetchCardEventsAsync(CancellationToken ct)
    {
        try
        {
            var events = await _client.GetCardEventsAsync(_lastCardEventSeq, ct).ConfigureAwait(false);
            foreach (var e in events.OrderBy(e => e.EventSeq))
            {
                _lastCardEventSeq = Math.Max(_lastCardEventSeq, e.EventSeq);
                CardEventReceived?.Invoke(this, e);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PollError?.Invoke(this, ex);
        }
    }

    private void UpdateDoorLeftOpen(DeviceStatus status)
    {
        bool doorOpen = status.Door == DoorState.Open;
        bool leftOpen = doorOpen &&
            (status.DoorLeftOpenAlert ||
             TimeSpan.FromSeconds(status.DoorOpenSeconds) >= _options.DoorOpenAlertThreshold);
        if (leftOpen != DoorLeftOpen || doorOpen != _lastOpenDoor)
        {
            DoorLeftOpen = leftOpen;
            DoorLeftOpenChanged?.Invoke(this, leftOpen);
        }
        _lastOpenDoor = doorOpen;
    }

    private void SetConnection(ConnectionState next)
    {
        if (_connection == next) return;
        _connection = next;
        ConnectionChanged?.Invoke(this, next);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await _client.DisposeAsync().ConfigureAwait(false);
    }
}
