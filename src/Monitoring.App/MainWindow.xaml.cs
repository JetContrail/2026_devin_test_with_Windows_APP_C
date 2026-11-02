using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Monitoring.Core;
using Monitoring.Protocol;

namespace Monitoring.App;

public partial class MainWindow : Window
{
    private readonly DeviceMonitor _monitor;

    public MainWindow(DeviceMonitor monitor, string deviceDisplayName, UserTheme theme)
    {
        _monitor = monitor;
        InitializeComponent();
        Title = $"Door Monitor{theme.TitleSuffix}";
        HeaderText.Text = $"{deviceDisplayName} — user: {Environment.UserName}";
        HeaderText.Foreground = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(theme.AccentColor));
        if (theme.LogoImage is { } logo && File.Exists(logo))
        {
            LogoImage.Source = new BitmapImage(new Uri(logo, UriKind.Absolute));
            LogoImage.Visibility = Visibility.Visible;
        }

        _monitor.ConnectionChanged += (_, s) => Post(() =>
        {
            ConnText.Text = s.ToString();
            bool connected = s == ConnectionState.Connected;
            LockButton.IsEnabled = connected;
            UnlockButton.IsEnabled = connected;
        });
        _monitor.StatusReceived += (_, st) => Post(() =>
        {
            LockText.Text = st.Lock.ToString();
            DoorText.Text = st.Door.ToString();
            ReaderText.Text = st.CardReader.ToString();
            DoorOpenText.Text = st.Door == DoorState.Open ? $"{st.DoorOpenSeconds}s" : "-";
        });
        _monitor.DoorLeftOpenChanged += (_, leftOpen) => Post(() =>
            AlertText.Text = leftOpen ? "DOOR LEFT OPEN" : "");
        _monitor.CardEventReceived += (_, ev) => Post(() =>
            Log($"Card {ev.CardId} {(ev.Accepted ? "accepted" : "DENIED")} @ {ev.Timestamp:HH:mm:ss}"));
        _monitor.PollError += (_, ex) => Post(() => Log($"Poll error: {ex.Message}"));
        Log("Monitor started");
    }

    private void OnLock(object sender, RoutedEventArgs e) => _ = SendLockAsync(unlock: false);
    private void OnUnlock(object sender, RoutedEventArgs e) => _ = SendLockAsync(unlock: true);

    private async Task SendLockAsync(bool unlock)
    {
        try
        {
            var res = await _monitor.SetLockAsync(unlock, CancellationToken.None);
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

    private void Log(string message)
    {
        LogBox.Items.Add($"{DateTime.Now:HH:mm:ss.fff}  {message}");
        LogBox.ScrollIntoView(LogBox.Items[^1]);
    }
}
