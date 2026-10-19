using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace LaunchApp.Shared;

// Do not pass exception messages, render arguments or stacks to the browser console.
// JS bridge errors are mapped to short, static, user-safe summaries to identify transport failures
// without leaking credentials, local paths, or request/response payloads.
public sealed class SafeErrorBoundary : ErrorBoundary
{
    protected override Task OnErrorAsync(Exception exception)
    {
        BrowserDiagnostics.Report("render", exception);
        return Task.CompletedTask;
    }
}

public static class BrowserDiagnostics
{
    private static readonly Regex SensitiveAssignment = new(
        @"(?i)(?<key>""?(?:password|passwd|otp|token|sid|session(?:id)?|authorization|cookie|secret|username|credential(?:s)?|api[_ -]?key)""?)\s*[:=]\s*(?:""[^""]*""|'[^']*'|[^,\s;&}]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Url = new(
        @"(?i)\bhttps?://[^\s""'<>]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WindowsPath = new(
        @"(?i)(?:[a-z]:\\|\\\\)[^\s""'<>|]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Payload = new(
        @"(?i)\b(?:request|response)\s+(?:body|payload|content|headers?)\s*[:=].*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static void Report(string operation, Exception exception)
    {
        var summary = exception is JSException ? SafeInteropSummary(exception.Message) : null;
        var details = string.IsNullOrWhiteSpace(summary) ? "" : $": {summary}";
        Console.WriteLine($"[WebLaunch] {operation} failed ({exception.GetType().Name}){details}. Retry the action or reload the page.");
    }

    private static string? SafeInteropSummary(string message)
    {
        var firstLine = message.Replace('\r', '\n').Split('\n', 2)[0].Trim();
        firstLine = Regex.Replace(firstLine, @"^(?:JavaScript interop error|JSException|Error):\s*", "", RegexOptions.IgnoreCase);
        firstLine = Payload.Replace(firstLine, "request/response payload=[redacted]");
        firstLine = SensitiveAssignment.Replace(firstLine, match => match.Groups["key"].Value + "=[redacted]");
        firstLine = Url.Replace(firstLine, "[url redacted]");
        firstLine = WindowsPath.Replace(firstLine, "[path redacted]");
        firstLine = Regex.Replace(firstLine, @"\s{2,}", " ").Trim();
        if (firstLine.Length > 180) firstLine = firstLine[..180] + "…";
        return firstLine.Length == 0 ? null : firstLine;
    }
}

public sealed class SafeBrowserLoggerProvider : Microsoft.Extensions.Logging.ILoggerProvider
{
    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new SafeLogger();
    public void Dispose() { }
    private sealed class SafeLogger : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => level >= Microsoft.Extensions.Logging.LogLevel.Warning;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(level)) Console.WriteLine($"[WebLaunch] managed-{level} event {id.Id} ({exception?.GetType().Name ?? "no exception"}).");
        }
    }
}
