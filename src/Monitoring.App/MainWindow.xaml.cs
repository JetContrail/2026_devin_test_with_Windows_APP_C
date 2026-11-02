using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Monitoring.Core;
using Monitoring.Protocol;

namespace Monitoring.App;

public partial class MainWindow : Window
{
    private readonly IReadOnlyList<DeviceMonitor> _monitors;
    private DeviceMonitor? _current;

    private EventHandler<ConnectionState>? _onConn;
    private EventHandler<DeviceStatus>? _onStatus;
    private EventHandler<bool>? _onDoorLeft;
    private EventHandler<CardEvent>? _onCard;
    private EventHandler<Exception>? _onPollErr;

    public MainWindow(IReadOnlyList<DeviceMonitor> monitors, UserTheme theme)
    {
        _monitors = monitors;
        InitializeComponent();
        Title = $"Door Monitor{theme.TitleSuffix}";
        HeaderText.Foreground = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(theme.AccentColor));
        var logo = theme.LogoImage;
        if (logo is not null && File.Exists(logo))
        {
            LogoImage.Source = new BitmapImage(new Uri(logo, UriKind.Absolute));
            LogoImage.Visibility = Visibility.Visible;
        }

        DeviceSelector.ItemsSource = _monitors;
        DeviceSelector.DisplayMemberPath = nameof(DeviceMonitor.Name);
        DeviceSelector.SelectedIndex = 0;
        DeviceSelector.Visibility = _monitors.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        Attach(_monitors[0]);
    }

    private void OnDeviceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceSelector.SelectedItem is DeviceMonitor m)
            Attach(m);
    }

    private void Attach(DeviceMonitor monitor)
    {
        if (_current is not null)
        {
            _current.ConnectionChanged -= _onConn;
            _current.StatusReceived -= _onStatus;
            _current.DoorLeftOpenChanged -= _onDoorLeft;
            _current.CardEventReceived -= _onCard;
            _current.PollError -= _onPollErr;
        }
        _current = monitor;
        HeaderText.Text = $"{monitor.Name} — user: {Environment.UserName}";

        _onConn = (_, s) => Post(() =>
        {
            ConnText.Text = s.ToString();
            bool connected = s == ConnectionState.Connected;
            LockButton.IsEnabled = connected;
            UnlockButton.IsEnabled = connected;
        });
        _onStatus = (_, st) => Post(() =>
        {
            LockText.Text = st.Lock.ToString();
            DoorText.Text = st.Door.ToString();
            ReaderText.Text = st.CardReader.ToString();
            DoorOpenText.Text = st.Door == DoorState.Open ? $"{st.DoorOpenSeconds}s" : "-";
        });
        _onDoorLeft = (_, leftOpen) => Post(() =>
            AlertText.Text = leftOpen ? "DOOR LEFT OPEN" : "");
        _onCard = (_, ev) => Post(() =>
            Log($"Card {ev.CardId} {(ev.Accepted ? "accepted" : "DENIED")} @ {ev.Timestamp:HH:mm:ss}"));
        _onPollErr = (_, ex) => Post(() => Log($"Poll error: {ex.Message}"));

        monitor.ConnectionChanged += _onConn;
        monitor.StatusReceived += _onStatus;
        monitor.DoorLeftOpenChanged += _onDoorLeft;
        monitor.CardEventReceived += _onCard;
        monitor.PollError += _onPollErr;

        // reflect the current state immediately on switch
        ConnText.Text = monitor.Connection.ToString();
        LockText.Text = monitor.LastStatus?.Lock.ToString() ?? "-";
        DoorText.Text = monitor.LastStatus?.Door.ToString() ?? "-";
        ReaderText.Text = monitor.LastStatus?.CardReader.ToString() ?? "-";
        AlertText.Text = monitor.DoorLeftOpen ? "DOOR LEFT OPEN" : "";
        LockButton.IsEnabled = UnlockButton.IsEnabled =
            monitor.Connection == ConnectionState.Connected;
        Log($"Watching {monitor.Name}");
    }

    private void OnLock(object sender, RoutedEventArgs e) => _ = SendLockAsync(unlock: false);
    private void OnUnlock(object sender, RoutedEventArgs e) => _ = SendLockAsync(unlock: true);

    private async Task SendLockAsync(bool unlock)
    {
        if (_current is null) return;
        try
        {
            var res = await _current.SetLockAsync(unlock, CancellationToken.None);
            Log($"{(unlock ? "Unlock" : "Lock")} -> {res.Result} (lock={res.Lock})");
        }
        catch (Exception ex)
        {
            Log($"{(unlock ? "Unlock" : "Lock")} failed: {ex.Message}");
        }
    }

    private void Post(Action a)
    {
        if (Dispatcher.CheckAccess()) a(); else Dispatcher.Invoke(a);
    }

    private const int MaxLogLines = 500; // history lives in logs/comm-*.log

    private void Log(string message)
    {
        while (LogBox.Items.Count >= MaxLogLines)
            LogBox.Items.RemoveAt(0);
        LogBox.Items.Add($"{DateTime.Now:HH:mm:ss.fff}  {message}");
        LogBox.ScrollIntoView(LogBox.Items[^1]);
    }
}
