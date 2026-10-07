using System.IO;
using WebLaunch.Core;
using XIVLauncher.Common.Support;

namespace handlerlaunch;
internal static class DesktopDiagnostics
{
    public static bool DebugEnabled { get; private set; }
    private static SafeDiagnosticLog? log;
    public static void Configure(bool debug)
    {
        DebugEnabled = debug;
        log = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WebLaunch", "Logs"), debug);
        // Without LogInit, every Serilog Log.* call in CoreLibLaunchSupport and LibDalamud is a no-op.
        LogInit.Initialize(debug);
    }
    public static void Write(DiagnosticEvent code, string? requestId = null, LaunchStatus? status = null, string? pluginId = null) => log?.Write(code, requestId, status, pluginId);
}
