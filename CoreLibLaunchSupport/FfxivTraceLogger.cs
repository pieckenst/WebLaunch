using System.IO;
using System.Security.Cryptography;
using System.Text;
using Serilog;
using Serilog.Context;
using Serilog.Events;
using Serilog.Sinks.File;

namespace CoreLibLaunchSupport;

/// <summary>
/// Two-mode FFXIV launch pipeline logger with per-launch correlation IDs.
///
/// Normal mode:
///   - INFO / WARN / ERROR through the global Serilog logger.
///   - No credential payloads or request/response bodies are emitted by this class.
///
/// Trace mode (FFXIV_TRACE=1):
///   - Full launch arguments, local paths and process-launch details via Trace/Stage detail.
///   - Full exception text for failures.
///   - Detailed Dalamud launch stages.
///   - Trace data is written to a separate local forensic log.
///
/// WARNING: Trace mode can contain plaintext credentials and session material.
/// Never commit, upload, or share a trace file without reviewing it first.
/// </summary>
public static class FfxivTraceLogger
{
    private static readonly object Gate = new();
    private static readonly AsyncLocal<string?> CorrelationSlot = new();
    private static Serilog.ILogger? _traceFileLogger;
    private static string? _traceFilePath;
    private static bool? _traceEnabled;
    // Tests redirect the trace file location; null in production.
    internal static string? TraceDirectoryOverride;

    /// <summary>True when FFXIV_TRACE=1 is set in the environment.</summary>
    public static bool TraceEnabled =>
        _traceEnabled ??= string.Equals(
            Environment.GetEnvironmentVariable("FFXIV_TRACE"),
            "1",
            StringComparison.Ordinal);

    /// <summary>
    /// The correlation ID associated with the current asynchronous launch flow.
    /// </summary>
    /// <summary>Gets the active launch correlation ID, if a correlation scope is active.</summary>
    public static string? CurrentCorrelationId => CorrelationSlot.Value;

    /// <summary>
    /// Creates a short correlation ID (6 hex chars) for one FFXIV launch attempt.
    /// </summary>
    /// <summary>Generates a short, random, uppercase hexadecimal ID for a launch attempt.</summary>
    public static string NewCorrelationId()
    {
        Span<byte> bytes = stackalloc byte[3];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes);
    }

    /// <summary>
    /// Starts a correlation scope. Nested calls reuse the active ID so the same
    /// launch remains correlated across the entire in-process pipeline.
    /// </summary>
    /// <summary>Creates a correlation scope, reusing an existing asynchronous scope when nested.</summary>
    /// <param name="requestedId">Optional caller-supplied ID used only when no scope is active.</param>
    public static CorrelationScope BeginCorrelation(string? requestedId = null)
    {
        var existing = CorrelationSlot.Value;
        if (!string.IsNullOrWhiteSpace(existing))
            return new CorrelationScope(existing, null, false);

        var id = string.IsNullOrWhiteSpace(requestedId) ? NewCorrelationId() : requestedId;
        CorrelationSlot.Value = id;

        var logContext = LogContext.PushProperty("FFXIVCorrelationId", id);
        return new CorrelationScope(id, logContext, true);
    }

    /// <summary>
    /// Returns the active correlation ID, or a stable marker when code is running
    /// outside a launch scope.
    /// </summary>
    /// <summary>Returns the active correlation ID, or NOID outside a launch scope.</summary>
    public static string CorrelationOrNone => CurrentCorrelationId ?? "NOID";

    // ---- Normal-mode stage logging -------------------------------------

    /// <summary>Logs a sanitized stage label normally and sends potentially sensitive detail only to opt-in trace mode.</summary>
    /// <param name="correlationId">ID associated with this launch attempt.</param>
    /// <param name="stage">Non-sensitive stage name.</param>
    /// <param name="detail">Optional diagnostic detail, emitted only when trace mode is enabled.</param>
    public static void Stage(string correlationId, string stage, string? detail = null)
    {
        var summary = $"[FFXIV:{correlationId}] {stage}";
        Log.Information(summary);

        // Stage detail may contain installation paths or provider-specific diagnostics.
        // Keep it available in the local forensic trace without leaking it to ordinary sinks.
        if (TraceEnabled)
            TraceWrite(LogEventLevel.Information, detail is null ? summary : $"{summary} — {detail}");
    }

    /// <summary>Logs a sanitized stage normally and constructs potentially sensitive detail only in opt-in trace mode.</summary>
    /// <param name="correlationId">ID associated with this launch attempt.</param>
    /// <param name="stage">Non-sensitive stage name.</param>
    /// <param name="detailFactory">Lazy trace detail builder; it may contain local paths or launch arguments.</param>
    public static void StageLazy(string correlationId, string stage, Func<string> detailFactory)
    {
        ArgumentNullException.ThrowIfNull(detailFactory);
        var summary = $"[FFXIV:{correlationId}] {stage}";
        Log.Information(summary);

        if (TraceEnabled)
            TraceWrite(LogEventLevel.Information, $"{summary} — {detailFactory()}");
    }

    public static void Info(string correlationId, string message)
    {
        var msg = $"[FFXIV:{correlationId}] {message}";
        Log.Information(msg);
        TraceWrite(LogEventLevel.Information, msg);
    }

    public static void Warn(string correlationId, string message)
    {
        var msg = $"[FFXIV:{correlationId}] {message}";
        Log.Warning(msg);
        TraceWrite(LogEventLevel.Warning, msg);
    }

    /// <summary>Emits a controlled failure category to ordinary sinks and a full exception only to opt-in trace output.</summary>
    /// <param name="correlationId">ID associated with this launch attempt.</param>
    /// <param name="message">Sanitized user-facing failure description.</param>
    /// <param name="ex">Optional underlying exception, never rendered to an ordinary sink.</param>
    public static void Error(string correlationId, string message, Exception? ex = null)
    {
        if (ex is not null)
            Log.Error("[FFXIV:{CorrelationId}] {Message}; Reason={Reason}", correlationId, message, ex switch
            {
                OperationCanceledException => "cancelled",
                UnauthorizedAccessException => "access denied",
                IOException => "I/O failure",
                _ => "launch failure"
            });
        else
            Log.Error("[FFXIV:{CorrelationId}] {Message}", correlationId, message);

        // Exception.ToString() can contain sensitive paths, arguments and remote payloads.
        // Do not even format it unless the operator explicitly opted into local trace mode.
        if (TraceEnabled)
        {
            var traceMessage = ex is null
                ? $"[FFXIV:{correlationId}] {message}"
                : $"[FFXIV:{correlationId}] {message}{Environment.NewLine}{ex}";
            TraceWrite(LogEventLevel.Error, traceMessage);
        }
    }

    // ---- Trace-only logging -------------------------------------------

    public static void Trace(string correlationId, string message)
    {
        if (!TraceEnabled)
            return;

        TraceWrite(LogEventLevel.Verbose, $"[FFXIV:{correlationId}] TRACE {message}");
    }

    /// <summary>
    /// Writes trace-only detail without evaluating sensitive diagnostic formatting when trace mode is disabled.
    /// </summary>
    /// <summary>Evaluates and writes a trace message factory only while explicit local tracing is enabled.</summary>
    /// <param name="correlationId">ID associated with this launch attempt.</param>
    /// <param name="messageFactory">Lazy detail builder, which may include sensitive diagnostics.</param>
    public static void Trace(string correlationId, Func<string> messageFactory)
    {
        ArgumentNullException.ThrowIfNull(messageFactory);
        if (!TraceEnabled)
            return;

        TraceWrite(LogEventLevel.Verbose, $"[FFXIV:{correlationId}] TRACE {messageFactory()}");
    }

    // ---- Internal trace-file management -------------------------------

    private static void TraceWrite(LogEventLevel level, string message)
    {
        if (!TraceEnabled)
            return;

        EnsureTraceLogger();
        _traceFileLogger?.Write(level, message);
    }

    private static void EnsureTraceLogger()
    {
        if (_traceFileLogger is not null)
            return;

        lock (Gate)
        {
            if (_traceFileLogger is not null)
                return;

            if (!TraceEnabled) return;
            try
            {
                var logsDir = TraceDirectoryOverride ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WebLaunch",
                    "Logs");

                Directory.CreateDirectory(logsDir);
                CleanupTraceFiles(logsDir);

                var fileName = $"ffxiv-trace-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}-{Environment.ProcessId}-{Guid.NewGuid():N}.log";
                var fullPath = Path.Combine(logsDir, fileName);
                _traceFilePath = fullPath;

                _traceFileLogger = CreateTraceFileLogger(fullPath);

                _traceFileLogger.Information("=== FFXIV trace log started ===");
                _traceFileLogger.Information("WARNING: This file may contain full launch arguments, local paths, exception details and plaintext session material.");
                _traceFileLogger.Information("WARNING: Do NOT commit or share this file without reviewing and redacting it.");
                _traceFileLogger.Information("File: {Path}", fullPath);
                _traceFileLogger.Information("");
            }
            catch (Exception)
            {
                _traceEnabled = false;
                (_traceFileLogger as IDisposable)?.Dispose();
                _traceFileLogger = null;
                _traceFilePath = null;
                Log.Warning("FFXIV file tracing disabled: trace file initialization failed.");
            }
        }
    }

    private sealed class TraceFileHooks : FileLifecycleHooks
    {
        public bool Opened { get; private set; }
        public override Stream OnFileOpened(string path, Stream underlyingStream, Encoding encoding)
        {
            Opened = true;
            return underlyingStream;
        }
    }

    internal static Serilog.Core.Logger CreateTraceFileLogger(string fullPath)
    {
        var hooks = new TraceFileHooks();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.File(
                fullPath,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                shared: false,
                encoding: Encoding.UTF8,
                flushToDiskInterval: TimeSpan.FromSeconds(1),
                hooks: hooks)
            .CreateLogger();
        // Serilog can swallow file-open errors and return a logger without a working sink.
        if (!hooks.Opened)
        {
            logger.Dispose();
            throw new IOException("Trace file did not open.");
        }
        return logger;
    }

    // Timestamped files do not roll, so the sink's rolling retention cannot expire them.
    internal static void CleanupTraceFiles(string logsDir)
    {
        var cutoff = DateTime.UtcNow.AddDays(-7);
        try
        {
            foreach (var path in Directory.EnumerateFiles(logsDir, "ffxiv-trace-*.log").Take(1000))
            {
                try
                {
                    var file = new FileInfo(path);
                    if ((file.Attributes & FileAttributes.ReparsePoint) == 0 && file.LastWriteTimeUtc < cutoff)
                        file.Delete();
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Writes the end marker, flushes and closes the trace file during desktop host shutdown.
    /// Safe to call when tracing was never started or more than once. A later trace write
    /// while FFXIV_TRACE=1 starts a new file.
    /// </summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            if (_traceFileLogger is null)
                return;

            var logger = _traceFileLogger;
            _traceFileLogger = null;
            _traceFilePath = null;
            try { logger.Information("=== FFXIV trace log ended ==="); }
            finally { (logger as IDisposable)?.Dispose(); }
        }
    }

    /// <summary>Scope returned by BeginCorrelation.</summary>
    public sealed class CorrelationScope : IDisposable
    {
        private readonly IDisposable? logContext;
        private readonly bool ownsCorrelation;
        private bool disposed;

        internal CorrelationScope(string id, IDisposable? logContext, bool ownsCorrelation)
        {
            Id = id;
            this.logContext = logContext;
            this.ownsCorrelation = ownsCorrelation;
        }

        public string Id { get; }

        /// <summary>Restores the prior correlation scope and its associated logging context.</summary>
        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            logContext?.Dispose();

            if (ownsCorrelation)
                CorrelationSlot.Value = null;
        }
    }

    /// <summary>For testing: clears the cached environment switch.</summary>
    internal static void ResetForTest()
    {
        _traceEnabled = null;
        TraceDirectoryOverride = null;
    }
}
