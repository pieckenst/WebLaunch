using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Serilog;
using Serilog.Events;
using Serilog.Formatting;

namespace XIVLauncher.Common.Support;

/// <summary>
/// Initializes the global Serilog logger used throughout CoreLibLaunchSupport and LibDalamud.
/// The ordinary log is always sanitized; full protocol/process details stay in FFXIV_TRACE files.
/// </summary>
public static class LogInit
{
    private static bool _initialized;
    private static readonly object Gate = new();

    private static readonly Regex UrlPattern = new(
        @"(?i)\bhttps?://[^\s""'<>]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex QuotedWindowsPathPattern = new(
        @"(?i)""(?:[a-z]:\\|\\\\)[^""]*""|'(?:[a-z]:\\|\\\\)[^']*'",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WindowsPathPattern = new(
        @"(?i)(?:[a-z]:\\|\\\\)[^\s""'<>|]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SensitiveAssignmentPattern = new(
        @"(?i)(?<key>""?(?:password|passwd|otp|token|access[_ -]?token|refresh[_ -]?token|sid|session(?:id)?|authorization|cookie|secret|username|credential(?:s)?|api[_ -]?key)""?)\s*[:=]\s*(?:""[^""]*""|'[^']*'|[^,\s;&}]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CommandLineSecretPattern = new(
        @"(?i)(?<prefix>(?:^|\s)--?(?:password|passwd|otp|username|token|sid|session|auth)(?:=|\s+))(?:""[^""]*""|'[^']*'|[^\s,;]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PayloadPattern = new(
        @"(?i)(?<label>\b(?:request|response)\s+(?:body|payload|content|headers?)\s*[:=]).*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OpaqueTokenPattern = new(
        @"(?<![A-Za-z0-9+/=_-])[A-Za-z0-9+/=_-]{96,}(?![A-Za-z0-9+/=_-])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RepeatedWhitespacePattern = new(
        @"\s{2,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static void Initialize(bool debug = false)
    {
        lock (Gate)
        {
            if (_initialized) return;

            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WebLaunch",
                "Logs");
            Directory.CreateDirectory(logDir);

            var logPath = Path.Combine(logDir, "core.log");
            var traceEnabled = string.Equals(Environment.GetEnvironmentVariable("FFXIV_TRACE"), "1", StringComparison.Ordinal);
            var minLevel = debug || traceEnabled ? LogEventLevel.Verbose : LogEventLevel.Information;

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Is(minLevel)
                .Enrich.FromLogContext()
                .WriteTo.File(
                    new SafeCoreLogFormatter(),
                    logPath,
                    fileSizeLimitBytes: 4_194_304,
                    rollOnFileSizeLimit: true,
                    retainedFileCountLimit: 3,
                    shared: false,
                    encoding: Encoding.UTF8,
                    flushToDiskInterval: TimeSpan.FromSeconds(1))
                .CreateLogger();

            Log.Information(
                "[{Component}] Serilog initialized. MinimumLevel={MinimumLevel}; Trace={TraceEnabled}; LogFile=core.log",
                nameof(LogInit), minLevel, traceEnabled);
            _initialized = true;
        }
    }

    /// <summary>
    /// Removes secrets, local paths, full URLs and raw request/response bodies from ordinary logs.
    /// Original raw FFXIV protocol detail remains opt-in through FFXIV_TRACE.
    /// </summary>
    public static string SanitizeDiagnosticText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var bounded = text.Length > 12_000 ? text[..12_000] + " [input-truncated]" : text;
        var cleaned = new string(bounded.Select(character => char.IsControl(character) ? ' ' : character).ToArray());

        cleaned = UrlPattern.Replace(cleaned, match =>
        {
            if (!Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)) return "[url-redacted]";
            return uri.Scheme + "://" + uri.Authority + "/[path-redacted]";
        });
        cleaned = QuotedWindowsPathPattern.Replace(cleaned, "[path-redacted]");
        cleaned = WindowsPathPattern.Replace(cleaned, "[path-redacted]");
        cleaned = SensitiveAssignmentPattern.Replace(cleaned, match => match.Groups["key"].Value + "=[redacted]");
        cleaned = CommandLineSecretPattern.Replace(cleaned, match => match.Groups["prefix"].Value + "[redacted]");
        cleaned = PayloadPattern.Replace(cleaned, match => match.Groups["label"].Value + " [details-redacted]");
        cleaned = OpaqueTokenPattern.Replace(cleaned, "[opaque-data-redacted]");
        cleaned = RepeatedWhitespacePattern.Replace(cleaned, " ").Trim();

        return cleaned.Length <= 1200 ? cleaned : cleaned[..1200] + "…[truncated]";
    }

    private sealed class SafeCoreLogFormatter : ITextFormatter
    {
        public void Format(LogEvent logEvent, TextWriter output)
        {
            var level = logEvent.Level switch
            {
                LogEventLevel.Verbose => "VRB",
                LogEventLevel.Debug => "DBG",
                LogEventLevel.Information => "INF",
                LogEventLevel.Warning => "WRN",
                LogEventLevel.Error => "ERR",
                LogEventLevel.Fatal => "FTL",
                _ => "LOG"
            };
            var correlation = logEvent.Properties.TryGetValue("FFXIVCorrelationId", out var correlationValue)
                && correlationValue is ScalarValue { Value: string id }
                && Regex.IsMatch(id, @"^[A-Fa-f0-9]{6,64}$")
                    ? id
                    : "NOID";
            var source = logEvent.Properties.TryGetValue("SourceContext", out var sourceValue)
                && sourceValue is ScalarValue { Value: string sourceName }
                    ? sourceName[(sourceName.LastIndexOf('.') + 1)..]
                    : "Host";
            source = SanitizeDiagnosticText(source);
            var message = SanitizeDiagnosticText(logEvent.RenderMessage(CultureInfo.InvariantCulture));

            output.Write(logEvent.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
            output.Write(" ["); output.Write(level); output.Write("] [FFXIV:");
            output.Write(correlation); output.Write("] ["); output.Write(source); output.Write("] ");
            output.Write(message);

            // Full exception messages/stacks may contain paths, launch parameters or provider
            // payloads. Keep a useful type marker here; raw detail stays in the explicit trace log.
            if (logEvent.Exception is not null)
            {
                output.Write(" [Exception=");
                output.Write(SanitizeDiagnosticText(logEvent.Exception.GetType().Name));
                output.Write(']');
            }
            output.WriteLine();
        }
    }

    public static void Shutdown()
    {
        lock (Gate)
        {
            if (!_initialized) return;
            Log.Information("[{Component}] Shutting down Serilog.", nameof(LogInit));
            Log.CloseAndFlush();
            _initialized = false;
        }
    }
}
