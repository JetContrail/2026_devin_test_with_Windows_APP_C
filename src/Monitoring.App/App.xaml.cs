using System.Net;
using System.Text.Json;
using System.Windows;
using Monitoring.Core;
using Monitoring.Net;

namespace Monitoring.App;

public partial class App : Application
{
    private DeviceMonitor? _monitor;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var baseDir = AppContext.BaseDirectory;
        var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(baseDir, "appsettings.json"))).RootElement;
        var dev = settings.GetProperty("Device");
        var mon = settings.GetProperty("Monitor");

        var endpoint = new IPEndPoint(IPAddress.Parse(dev.GetProperty("Host").GetString()!),
            dev.GetProperty("Port").GetInt32());
        var options = new MonitorOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(mon.GetProperty("PollIntervalMs").GetInt32()),
            LostAfterFailures = mon.GetProperty("LostAfterFailures").GetInt32(),
            DoorOpenAlertThreshold = TimeSpan.FromSeconds(mon.GetProperty("DoorOpenAlertThresholdSec").GetInt32()),
            UnlockDurationSec = mon.GetProperty("UnlockDurationSec").GetUInt32(),
        };

        _monitor = new DeviceMonitor(new UdpDeviceClient(endpoint), options);
        _monitor.Start();

        var theme = ResolveTheme(baseDir, Environment.UserName);
        var window = new MainWindow(_monitor, dev.GetProperty("DisplayName").GetString() ?? "", theme);
        window.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_monitor is not null)
            await _monitor.DisposeAsync();
        base.OnExit(e);
    }

    internal static UserTheme ResolveTheme(string baseDir, string userName)
    {
        var path = Path.Combine(baseDir, "user_themes.json");
        if (!File.Exists(path))
            return new UserTheme("#2E6DA4", null, "");
        var root = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        var entry = root.TryGetProperty(userName, out var u) ? u
            : root.TryGetProperty("*", out var w) ? w : default;
        if (entry.ValueKind != JsonValueKind.Object)
            return new UserTheme("#2E6DA4", null, "");
        string accent = entry.TryGetProperty("AccentColor", out var a) ? a.GetString() ?? "#2E6DA4" : "#2E6DA4";
        string? logo = entry.TryGetProperty("LogoImage", out var l) ? l.GetString() : null;
        string suffix = entry.TryGetProperty("TitleSuffix", out var s) ? s.GetString() ?? "" : "";
        return new UserTheme(accent, logo, suffix);
    }
}
