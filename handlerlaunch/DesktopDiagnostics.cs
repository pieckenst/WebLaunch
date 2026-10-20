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
        // Diagnostics are best-effort. An unwritable log directory must not prevent the
        // desktop bridge from starting; the type-only fallback is visible in Visual Studio.
        try { LogInit.Initialize(debug); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[WebLaunch] Core log initialization failed ({ex.GetType().Name})."); }
    }
    public static void Write(DiagnosticEvent code, string? requestId = null, LaunchStatus? status = null, string? pluginId = null, string? detail = null) =>
        log?.Write(code, requestId, status, pluginId, detail);
}
