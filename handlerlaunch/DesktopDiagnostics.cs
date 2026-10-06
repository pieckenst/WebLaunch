using System.IO;
using WebLaunch.Core;

namespace handlerlaunch;
internal static class DesktopDiagnostics
{
    private static SafeDiagnosticLog? log;
    public static void Configure(bool debug) => log = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WebLaunch", "Logs"), debug);
    public static void Write(DiagnosticEvent code, string? requestId = null, LaunchStatus? status = null, string? pluginId = null) => log?.Write(code, requestId, status, pluginId);
}
