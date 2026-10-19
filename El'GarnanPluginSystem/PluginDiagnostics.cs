using System.Text;

namespace El_Garnan_Plugin_Loader;

/// <summary>Severity for the plugin host's privacy-preserving diagnostics.</summary>
public enum PluginDiagnosticLevel
{
    Trace,
    Information,
    Warning,
    Error
}

/// <summary>A safe, structured snapshot of one plugin-host diagnostic event.</summary>
/// <remarks>Entries deliberately omit paths, exception messages, stack traces, credentials and plugin payloads.</remarks>
public sealed record PluginDiagnosticEntry(
    DateTimeOffset TimestampUtc,
    string CorrelationId,
    PluginDiagnosticLevel Level,
    string Stage,
    string PluginId,
    string Outcome,
    string? ExceptionType,
    string? HResult);

/// <summary>
/// Keeps a bounded in-memory history and a rotating local log for plugin discovery, loading,
/// dependency resolution, lifecycle and cleanup failures. Diagnostic failures never interrupt
/// plugin loading or shutdown.
/// </summary>
public sealed class PluginDiagnostics
{
    private const int MaximumFileBytes = 1_048_576;
    private const int MaximumHistoryEntries = 256;
    private readonly object gate = new();
    private readonly Queue<PluginDiagnosticEntry> recent = new();
    private readonly string logPath;

    public bool DebugEnabled { get; }
    public string LogPath => logPath;

    /// <param name="directory">Optional log directory, primarily for isolated tests.</param>
    /// <param name="debugEnabled">Whether high-volume trace events should be recorded.</param>
    public PluginDiagnostics(string? directory = null, bool debugEnabled = false)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            directory = string.IsNullOrWhiteSpace(local)
                ? Path.Combine(Path.GetTempPath(), "WebLaunch", "Logs")
                : Path.Combine(local, "WebLaunch", "Logs");
        }

        logPath = Path.Combine(Path.GetFullPath(directory), "plugins.log");
        DebugEnabled = debugEnabled;
    }

    /// <summary>Records a controlled event. Exception text and paths are never persisted.</summary>
    public void Record(
        PluginDiagnosticLevel level,
        string stage,
        string? pluginId,
        string outcome,
        Exception? exception = null,
        string? correlationId = null)
    {
        if (level == PluginDiagnosticLevel.Trace && !DebugEnabled) return;

        var entry = new PluginDiagnosticEntry(
            DateTimeOffset.UtcNow,
            Sanitize(correlationId ?? Guid.NewGuid().ToString("N"), "unknown"),
            level,
            Sanitize(stage, "unknown"),
            Sanitize(pluginId, "host"),
            Sanitize(outcome, "unknown"),
            exception is null ? null : Sanitize(exception.GetType().Name, "Exception"),
            exception is null ? null : "0x" + unchecked((uint)exception.HResult)
                .ToString("X8", System.Globalization.CultureInfo.InvariantCulture));

        lock (gate)
        {
            recent.Enqueue(entry);
            while (recent.Count > MaximumHistoryEntries) recent.Dequeue();

            try
            {
                var directory = Path.GetDirectoryName(logPath)!;
                Directory.CreateDirectory(directory);
                if (File.Exists(logPath) && new FileInfo(logPath).Length >= MaximumFileBytes)
                    File.Move(logPath, logPath + ".1", overwrite: true);

                var line = $"{entry.TimestampUtc:O} correlation={entry.CorrelationId} level={entry.Level} stage={entry.Stage} " +
                    $"plugin={entry.PluginId} outcome={entry.Outcome} error={entry.ExceptionType ?? "-"} " +
                    $"hr={entry.HResult ?? "-"}{Environment.NewLine}";
                File.AppendAllText(logPath, line, Encoding.UTF8);
            }
            catch (Exception)
            {
                // Diagnostics are best-effort and cannot become a launch or shutdown failure.
            }
        }
    }

    /// <summary>Returns the newest diagnostic entries without exposing mutable internal state.</summary>
    public IReadOnlyList<PluginDiagnosticEntry> GetRecent(int limit = 100)
    {
        if (limit is < 1 or > MaximumHistoryEntries)
            throw new ArgumentOutOfRangeException(nameof(limit), $"Limit must be between 1 and {MaximumHistoryEntries}.");

        lock (gate) return recent.Reverse().Take(limit).ToArray();
    }

    private static string Sanitize(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var chars = value
            .Where(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '.' or ':')
            .Take(64)
            .ToArray();
        return chars.Length == 0 ? fallback : new string(chars);
    }
}
