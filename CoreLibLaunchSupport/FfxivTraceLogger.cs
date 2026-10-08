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
///   - Full HTTP request/response details, including bodies.
///   - Process arguments and process lifecycle information.
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

    /// <summary>True when FFXIV_TRACE=1 is set in the environment.</summary>
    public static bool TraceEnabled =>
        _traceEnabled ??= string.Equals(
            Environment.GetEnvironmentVariable("FFXIV_TRACE"),
            "1",
            StringComparison.Ordinal);

    /// <summary>
    /// The correlation ID associated with the current asynchronous launch flow.
    /// </summary>
    public static string? CurrentCorrelationId => CorrelationSlot.Value;

    /// <summary>
    /// Creates a short correlation ID (6 hex chars) for one FFXIV launch attempt.
    /// </summary>
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
    public static string CorrelationOrNone => CurrentCorrelationId ?? "NOID";

    // ---- Normal-mode stage logging -------------------------------------

    public static void Stage(string correlationId, string stage, string? detail = null)
    {
        var msg = detail is null
            ? $"[FFXIV:{correlationId}] {stage}"
            : $"[FFXIV:{correlationId}] {stage} — {detail}";

        Log.Information(msg);
        TraceWrite(LogEventLevel.Information, msg);
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

        TraceWrite(
            LogEventLevel.Error,
            ex is null
                ? $"[FFXIV:{correlationId}] {message}"
                : $"[FFXIV:{correlationId}] {message}{Environment.NewLine}{ex}");
    }

    // ---- Trace-only logging -------------------------------------------

    public static void Trace(string correlationId, string message)
    {
        if (!TraceEnabled)
            return;

        TraceWrite(LogEventLevel.Verbose, $"[FFXIV:{correlationId}] TRACE {message}");
    }

    public static void TraceHttp(
        string correlationId,
        string label,
        string method,
        string url,
        int? statusCode = null,
        string? requestBody = null,
        string? responseBody = null,
        string? requestHeaders = null,
        string? responseHeaders = null)
    {
        if (!TraceEnabled)
            return;

        var sb = new StringBuilder();
        sb.AppendLine($"[FFXIV:{correlationId}] TRACE HTTP {label}");
        sb.AppendLine($"  {method} {url}");

        if (statusCode.HasValue)
            sb.AppendLine($"  Status: {statusCode}");

        if (requestHeaders is not null)
            sb.AppendLine($"  Request Headers:{Environment.NewLine}{Indent(requestHeaders)}");

        if (requestBody is not null)
            sb.AppendLine($"  Request Body:{Environment.NewLine}{Indent(requestBody)}");

        if (responseHeaders is not null)
            sb.AppendLine($"  Response Headers:{Environment.NewLine}{Indent(responseHeaders)}");

        if (responseBody is not null)
            sb.AppendLine($"  Response Body:{Environment.NewLine}{Indent(responseBody)}");

        TraceWrite(LogEventLevel.Verbose, sb.ToString());
    }

    public static void TraceProcess(
        string correlationId,
        string label,
        string? executable = null,
        string? workingDir = null,
        string? arguments = null,
        int? pid = null,
        int? exitCode = null,
        string? stdout = null,
        string? stderr = null)
    {
        if (!TraceEnabled)
            return;

        var sb = new StringBuilder();
        sb.AppendLine($"[FFXIV:{correlationId}] TRACE PROCESS {label}");

        if (executable is not null)
            sb.AppendLine($"  Executable: {executable}");

        if (workingDir is not null)
            sb.AppendLine($"  Working Dir: {workingDir}");

        if (arguments is not null)
            sb.AppendLine($"  Arguments: {arguments}");

        if (pid.HasValue)
            sb.AppendLine($"  PID: {pid}");

        if (exitCode.HasValue)
            sb.AppendLine($"  Exit Code: {exitCode}");

        if (stdout is not null)
            sb.AppendLine($"  stdout:{Environment.NewLine}{Indent(stdout)}");

        if (stderr is not null)
            sb.AppendLine($"  stderr:{Environment.NewLine}{Indent(stderr)}");

        TraceWrite(LogEventLevel.Verbose, sb.ToString());
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
                var logsDir = Path.Combine(
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
                _traceFileLogger.Information("WARNING: This file contains FULL HTTP payloads and may contain plaintext credentials.");
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
    /// Flushes and closes the trace file. Safe to call at process shutdown.
    /// </summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            if (_traceFileLogger is null)
                return;

            _traceFileLogger.Information("=== FFXIV trace log ended ===");
            (_traceFileLogger as IDisposable)?.Dispose();
            _traceFileLogger = null;
            _traceFilePath = null;
        }
    }

    private static string Indent(string text, int spaces = 4)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var pad = new string(' ', spaces);
        using var reader = new StringReader(text);
        var sb = new StringBuilder();
        string? line;

        while ((line = reader.ReadLine()) is not null)
            sb.Append(pad).AppendLine(line);

        return sb.ToString();
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
    }
}
