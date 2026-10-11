using System.IO;
using CommandLine;
using Serilog;
using Serilog.Events;

namespace XIVLauncher.Common.Support;

/// <summary>
/// Initializes the Serilog global logger used throughout CoreLibLaunchSupport and LibDalamud.
/// </summary>
public static class LogInit
{
    private static bool _initialized;
    private static readonly object Gate = new();

    // ReSharper disable once ClassNeverInstantiated.Local
    private sealed class LogOptions
    {
        [Option('v', "verbose", Required = false, HelpText = "Set output to verbose messages.")]
        public bool Verbose { get; set; }

        [Option("log-file-path", Required = false, HelpText = "Set path for log file.")]
        public string? LogPath { get; set; }
    }

    /// <summary>
    /// Initializes the global Serilog logger if not already initialized.
    ///
    /// Normal mode writes Information and above. Debug mode or FFXIV_TRACE=1
    /// enables Verbose level logging.
    /// </summary>
    public static void Initialize(bool debug = false)
    {
        lock (Gate)
        {
            if (_initialized)
                return;

            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WebLaunch",
                "Logs");
            Directory.CreateDirectory(logDir);

            var logPath = Path.Combine(logDir, "core.log");
            var traceEnabled = string.Equals(
                Environment.GetEnvironmentVariable("FFXIV_TRACE"),
                "1",
                StringComparison.Ordinal);
            var minLevel = debug || traceEnabled
                ? LogEventLevel.Verbose
                : LogEventLevel.Information;

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Is(minLevel)
                .Enrich.FromLogContext()
                .WriteTo.File(
                    logPath,
                    outputTemplate:
                        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] " +
                        "[FFXIV:{FFXIVCorrelationId}] {Message:lj}{NewLine}{Exception}",
                    shared: false,
                    flushToDiskInterval: TimeSpan.FromSeconds(1))
                .CreateLogger();

            Log.Information(
                "[{Component}] Serilog initialized. MinimumLevel={MinimumLevel}; Trace={TraceEnabled}; LogFile={LogFile}",
                nameof(LogInit),
                minLevel,
                traceEnabled,
                logPath);

            _initialized = true;
        }
    }

    /// <summary>Flushes and closes the global logger.</summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            if (!_initialized)
                return;

            Log.Information("[{Component}] Shutting down Serilog.", nameof(LogInit));
            Log.CloseAndFlush();
            _initialized = false;
        }
    }
}
