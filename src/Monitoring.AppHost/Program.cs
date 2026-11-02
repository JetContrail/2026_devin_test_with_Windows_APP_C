// MonitoringApp host: owns the device connection and the polling engine, then
// hands them to the per-user GUI plugin resolved from gui_plugins.json.
// GUI is deliberately independent of the app: a user gets a custom UI by
// dropping in a DLL implementing Monitoring.Gui.Contract.IMonitorGui.
using System.Net;
using System.Reflection;
using System.Text.Json;
using Monitoring.Core;
using Monitoring.Gui.Contract;
using Monitoring.Net;

namespace Monitoring.AppHost;

public static class Program
{
    [STAThread]
    public static int Main()
    {
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

        string user = Environment.UserName;
        var plugins = JsonDocument.Parse(File.ReadAllText(Path.Combine(baseDir, "gui_plugins.json"))).RootElement;
        string? dll = plugins.TryGetProperty(user, out var p) ? p.GetString()
            : plugins.TryGetProperty("*", out var w) ? w.GetString() : null;
        if (dll is null)
        {
            Console.Error.WriteLine($"No GUI plugin mapped for user '{user}' and no '*' fallback in gui_plugins.json");
            return 2;
        }

        var gui = LoadGui(Path.Combine(baseDir, dll));
        var client = new UdpDeviceClient(endpoint);
        var monitor = new DeviceMonitor(client, options);
        try
        {
            monitor.Start();
            return gui.Run(monitor, new GuiContext(user, dev.GetProperty("DisplayName").GetString() ?? ""));
        }
        finally
        {
            monitor.DisposeAsync().AsTask().Wait();
        }
    }

    private static IMonitorGui LoadGui(string dllPath)
    {
        var asm = Assembly.LoadFrom(dllPath);
        var type = asm.GetTypes().FirstOrDefault(t =>
            typeof(IMonitorGui).IsAssignableFrom(t) && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) is not null)
            ?? throw new InvalidOperationException($"No IMonitorGui implementation in {dllPath}");
        return (IMonitorGui)Activator.CreateInstance(type)!;
    }
}
