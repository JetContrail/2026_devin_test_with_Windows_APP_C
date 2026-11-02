using Monitoring.Core;

namespace Monitoring.Gui.Contract;

public sealed record GuiContext(string UserName, string DeviceDisplayName);

/// <summary>
/// GUI plugin contract. Each user's GUI is an independent assembly that
/// implements this interface; the AppHost loads the DLL mapped to the
/// current user (appsettings.json "GuiPlugins") and calls <see cref="Run"/>.
/// The plugin owns its UI toolkit (the stock implementation uses WPF) and
/// must return a process exit code when its UI closes.
/// </summary>
public interface IMonitorGui
{
    int Run(DeviceMonitor monitor, GuiContext context);
}
