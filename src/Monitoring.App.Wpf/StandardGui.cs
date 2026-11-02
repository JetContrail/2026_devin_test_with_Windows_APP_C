using System.Windows;
using Monitoring.Core;
using Monitoring.Gui.Contract;

namespace Monitoring.App.Wpf;

/// <summary>Default WPF GUI plugin. One plugin = one user-specific GUI assembly.</summary>
public sealed class StandardGui : IMonitorGui
{
    public int Run(DeviceMonitor monitor, GuiContext context)
    {
        // The plugin owns its WPF thread so the host stays toolkit-agnostic.
        int exitCode = 0;
        var ui = new Thread(() =>
        {
            var app = new Application();
            var window = new MainWindow(monitor, context);
            app.Run(window);
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.IsBackground = false;
        ui.Start();
        ui.Join();
        return exitCode;
    }
}
