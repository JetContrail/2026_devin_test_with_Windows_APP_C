using Monitoring.Core;
using Monitoring.Net;
using Monitoring.Protocol;
using Xunit;

namespace Monitoring.UnitTests;

public class DeviceMonitorTests
{
    private sealed class FakeClient : IDeviceClient
    {
        public Queue<Exception?> PollResults { get; } = new();
        public int PollCount;
        public DeviceStatus Next = new(LockState.Locked, DoorState.Closed, 0,
            CardReaderStatus.Online, 0, false, false);
        public List<CardEvent> Events { get; } = new();
        public (LockCommandKind Cmd, uint Dur)? LastCommand;
        public event EventHandler<TransportLinkState>? LinkChanged;
        public void EmitLink(TransportLinkState s) => LinkChanged?.Invoke(this, s);

        public Task<DeviceStatus> PollStatusAsync(CancellationToken ct)
        {
            PollCount++;
            if (PollResults.Count > 0 && PollResults.Dequeue() is { } ex)
                return Task.FromException<DeviceStatus>(ex);
            return Task.FromResult(Next);
        }

        public Task<IReadOnlyList<CardEvent>> GetCardEventsAsync(uint lastSeq, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<CardEvent>>(Events.Where(e => e.EventSeq > lastSeq).ToList());

        public Task<CommandResponse> SendLockCommandAsync(LockCommandKind c, uint d, CancellationToken ct)
        {
            LastCommand = (c, d);
            return Task.FromResult(new CommandResponse(CommandResult.Ok, Next.Lock));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task WaitForAsync(Func<bool> cond)
    {
        for (int i = 0; i < 200 && !cond(); i++)
            await Task.Delay(10);
        Assert.True(cond(), "timed out waiting for condition");
    }

    [Fact]
    public async Task PollLoop_ReportsConnectedAndStatus()
    {
        var client = new FakeClient();
        var monitor = new DeviceMonitor(client, new MonitorOptions { PollInterval = TimeSpan.FromMilliseconds(20) });
        var statuses = new List<DeviceStatus>();
        monitor.StatusReceived += (_, s) => statuses.Add(s);
        var conn = new List<ConnectionState>();
        monitor.ConnectionChanged += (_, s) => conn.Add(s);

        monitor.Start();
        await WaitForAsync(() => statuses.Count >= 2);
        await monitor.StopAsync();

        Assert.Equal(ConnectionState.Connected, conn[0]);
        Assert.Equal(LockState.Locked, statuses[0].Lock);
    }

    [Fact]
    public async Task ConsecutiveFailures_TransitionToLost()
    {
        var client = new FakeClient();
        for (int i = 0; i < 50; i++) client.PollResults.Enqueue(new DeviceTimeoutException("t"));
        var monitor = new DeviceMonitor(client, new MonitorOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
            LostAfterFailures = 3,
        });
        var states = new List<ConnectionState>();
        monitor.ConnectionChanged += (_, s) => states.Add(s);

        monitor.Start();
        await WaitForAsync(() => states.Contains(ConnectionState.Lost));
        await monitor.StopAsync();
    }

    [Fact]
    public async Task RecoveryAfterLost_ReturnsToConnected()
    {
        var client = new FakeClient();
        for (int i = 0; i < 3; i++) client.PollResults.Enqueue(new DeviceTimeoutException("t"));
        var monitor = new DeviceMonitor(client, new MonitorOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
            LostAfterFailures = 3,
        });
        var states = new List<ConnectionState>();
        monitor.ConnectionChanged += (_, s) => states.Add(s);

        monitor.Start();
        await WaitForAsync(() => states.Contains(ConnectionState.Connected) &&
                                states.IndexOf(ConnectionState.Connected) > states.IndexOf(ConnectionState.Lost));
        await monitor.StopAsync();
    }

    [Fact]
    public async Task DoorLeftOpen_AlertFromDeviceFlagAndDuration()
    {
        var client = new FakeClient
        {
            Next = new DeviceStatus(LockState.Locked, DoorState.Open, 70,
                CardReaderStatus.Online, 0, true, false),
        };
        var monitor = new DeviceMonitor(client, new MonitorOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
            DoorOpenAlertThreshold = TimeSpan.FromSeconds(60),
        });
        bool? alert = null;
        monitor.DoorLeftOpenChanged += (_, v) => alert = v;
        monitor.Start();
        await WaitForAsync(() => alert == true);
        await monitor.StopAsync();
    }

    [Fact]
    public async Task PendingCardEvents_AreFetchedAndEmitted()
    {
        var client = new FakeClient
        {
            Next = new DeviceStatus(LockState.Locked, DoorState.Closed, 0,
                CardReaderStatus.Online, PendingCardEvents: 1, false, false),
        };
        client.Events.Add(new CardEvent(7, DateTimeOffset.UtcNow, "CARD-1", true));
        var monitor = new DeviceMonitor(client, new MonitorOptions { PollInterval = TimeSpan.FromMilliseconds(10) });
        var received = new List<CardEvent>();
        monitor.CardEventReceived += (_, e) => received.Add(e);
        monitor.Start();
        await WaitForAsync(() => received.Count == 1);
        await monitor.StopAsync();
        Assert.Equal("CARD-1", received[0].CardId);
    }

    [Fact]
    public async Task SetLock_SendsUnlockWithDuration()
    {
        var client = new FakeClient();
        var monitor = new DeviceMonitor(client, new MonitorOptions { UnlockDurationSec = 30 });
        var res = await monitor.SetLockAsync(unlock: true, CancellationToken.None);
        Assert.Equal(CommandResult.Ok, res.Result);
        Assert.Equal((LockCommandKind.Unlock, 30u), client.LastCommand);
    }
}
