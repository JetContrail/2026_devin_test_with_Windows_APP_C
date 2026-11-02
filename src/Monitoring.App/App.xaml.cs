using System.IO;
using System.Net;
using System.Text.Json;
using System.Windows;
using Monitoring.Core;
using Monitoring.Net;

namespace Monitoring.App;

public partial class App : Application
{
    private readonly List<DeviceMonitor> _monitors = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var baseDir = AppContext.BaseDirectory;
        var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(baseDir, "appsettings.json"))).RootElement;
        var mon = settings.GetProperty("Monitor");

        var options = new MonitorOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(mon.GetProperty("PollIntervalMs").GetInt32()),
            LostAfterFailures = mon.GetProperty("LostAfterFailures").GetInt32(),
            DoorOpenAlertThreshold = TimeSpan.FromSeconds(mon.GetProperty("DoorOpenAlertThresholdSec").GetInt32()),
            UnlockDurationSec = mon.GetProperty("UnlockDurationSec").GetUInt32(),
        };

        // write to LocalApplicationData — the install dir may be read-only
        CommunicationLog? commLog;
        try
        {
            commLog = new CommunicationLog(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DoorMonitor", "logs"));
        }
        catch (IOException)
        {
            commLog = null; // logging is best-effort; never block startup
        }
        var devices = new List<DeviceMonitor>();
        foreach (var dev in EnumerateDevices(settings))
        {
            var endpoint = new IPEndPoint(IPAddress.Parse(dev.GetProperty("Host").GetString()!),
                dev.GetProperty("Port").GetInt32());
            var security = new DeviceSecurityOptions
            {
                UseTls = dev.TryGetProperty("UseTls", out var t) && t.GetBoolean(),
                TargetHost = dev.TryGetProperty("TargetHost", out var th) ? th.GetString() : null,
                ExpectedCertThumbprint = dev.TryGetProperty("CertThumbprint", out var ct) ? ct.GetString() : null,
                Psk = dev.TryGetProperty("PskHex", out var p) && p.GetString() is { Length: > 0 } ph
                    ? TryParseHex(ph) : null,
            };
            var monitor = new DeviceMonitor(
                new TcpDeviceClient(endpoint, log: commLog, security: security),
                options)
            {
                Name = dev.TryGetProperty("Name", out var n) ? n.GetString() ?? endpoint.ToString()
                    : dev.TryGetProperty("DisplayName", out var dn) ? dn.GetString() ?? endpoint.ToString()
                    : endpoint.ToString(),
            };
            monitor.Start();
            devices.Add(monitor);
            _monitors.Add(monitor);
        }

        if (devices.Count == 0)
        {
            MessageBox.Show(
                "appsettings.json has no devices. Add at least one entry to \"Devices\" (or a legacy \"Device\" object).",
                "Door Monitor — configuration error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        var theme = ResolveTheme(baseDir, Environment.UserName);
        var window = new MainWindow(devices, theme);
        window.Show();
    }

    private static byte[]? TryParseHex(string hex)
    {
        try { return Convert.FromHexString(hex); }
        catch (FormatException) { return null; } // placeholder/unset — treat as no PSK
    }

    private static IEnumerable<JsonElement> EnumerateDevices(JsonElement settings)
    {
        if (settings.TryGetProperty("Devices", out var list))
            foreach (var d in list.EnumerateArray())
                yield return d;
        else if (settings.TryGetProperty("Device", out var single)) // legacy single-device config
            yield return single;
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        foreach (var m in _monitors)
            await m.DisposeAsync();
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
