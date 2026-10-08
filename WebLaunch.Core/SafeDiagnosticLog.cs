using System.Text;

namespace WebLaunch.Core;

public enum DiagnosticEvent
{
    HostStarting, HostReady, HostStopped, HostFailed, LaunchState,
    PluginLoaded, PluginUnloaded, PluginReloaded, PluginWarning, PluginError, PluginDebug, PluginInformation,
    NotificationShown, NotificationUnavailable, NotificationFailed, RegistrationComplete
}
/// <summary>Bounded diagnostic files containing only controlled event fields.</summary>
public sealed class SafeDiagnosticLog(string directory, bool debug)
{
    private readonly object gate = new();
    public bool DebugEnabled { get; } = debug;
    public void Write(DiagnosticEvent code, string? requestId = null, LaunchStatus? status = null, string? pluginId = null)
    {
        if (!DebugEnabled && code is DiagnosticEvent.PluginDebug or DiagnosticEvent.PluginInformation or DiagnosticEvent.NotificationShown) return;
        var id = Guid.TryParseExact(requestId, "N", out _) ? requestId : "-";
        var phase = status is null ? "-" : LaunchPresentation.Normalize(status).State;
        var plugin = pluginId switch { "ffxiv-launcher" => "ffxiv", "spellborn-launcher" => "spellborn", null => "-", _ => "external" };
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(directory);
                var file = Path.Combine(directory, "desktop.log");
                if (File.Exists(file) && new FileInfo(file).Length >= 1_048_576)
                    File.Move(file, file + ".1", overwrite: true);
                File.AppendAllText(file, $"{DateTimeOffset.UtcNow:O} {code} request={id} state={phase} plugin={plugin} debug={DebugEnabled}\n", Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Diagnostics cannot fail the launch. */ }
    }
}
