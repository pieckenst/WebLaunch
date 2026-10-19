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

    public void Write(
        DiagnosticEvent code,
        string? requestId = null,
        LaunchStatus? status = null,
        string? pluginId = null,
        string? detail = null)
    {
        if (!DebugEnabled && code is DiagnosticEvent.PluginDebug or DiagnosticEvent.PluginInformation or DiagnosticEvent.NotificationShown) return;

        var id = Guid.TryParseExact(requestId, "N", out _) ? requestId : "-";
        var phase = status is null ? "-" : LaunchPresentation.Normalize(status).State;
        var plugin = pluginId switch
        {
            "ffxiv-launcher" => "ffxiv",
            "spellborn-launcher" => "spellborn",
            null => "-",
            _ => "external"
        };
        // Callers supply reason codes, enum values and exception type names, never arbitrary
        // exception text, paths, request bodies, or credentials.
        var safeDetail = DebugEnabled ? NormalizeDetail(detail) : null;

        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(directory);
                var file = Path.Combine(directory, "desktop.log");
                if (File.Exists(file) && new FileInfo(file).Length >= 1_048_576)
                    File.Move(file, file + ".1", overwrite: true);

                var suffix = safeDetail is null ? "" : $" detail={safeDetail}";
                File.AppendAllText(
                    file,
                    $"{DateTimeOffset.UtcNow:O} {code} request={id} state={phase} plugin={plugin} debug={DebugEnabled}{suffix}\n",
                    Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // Logging is deliberately non-fatal: filesystem diagnostics must never interrupt a launch.
        }
    }

    private static string? NormalizeDetail(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return null;
        var safe = new string(detail.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or ':').Take(96).ToArray());
        return safe.Length == 0 ? null : safe;
    }
}
