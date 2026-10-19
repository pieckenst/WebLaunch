using El_Garnan_Plugin_Loader.Interfaces;
using XIVLauncher.Common.Support;

namespace WMConsole;

/// <summary>
/// Routes untrusted plugin log messages through a privacy-preserving structured summary.
/// Plugin-supplied text and exception messages are deliberately not persisted verbatim.
/// </summary>
internal sealed class ConsoleLogger : ILogger
{
    private static void Write(Serilog.Events.LogEventLevel level, string? message, Exception? exception = null)
    {
        var exceptionType = exception?.GetType().Name ?? "none";
        var exceptionCategory = exception switch
        {
            null => "none",
            OperationCanceledException => "cancelled",
            UnauthorizedAccessException => "access-denied",
            IOException => "io-failure",
            TimeoutException => "timeout",
            System.Net.Http.HttpRequestException => "network-failure",
            System.Text.Json.JsonException => "invalid-data",
            _ => "plugin-failure"
        };

        // Do not rely on regex redaction for arbitrary third-party plugin messages: a plugin can
        // place credentials or payloads in any free-form text. Keep a useful length signal and
        // controlled exception type/category, but no plugin-controlled message or exception text.
        Serilog.Log.Write(
            level,
            "[Plugin] MessageLength={MessageLength} ExceptionType={ExceptionType} ExceptionCategory={ExceptionCategory}",
            message?.Length ?? 0,
            exceptionType,
            exceptionCategory);
    }

    public void Debug(string message) => Write(Serilog.Events.LogEventLevel.Verbose, message);
    public void Information(string message) => Write(Serilog.Events.LogEventLevel.Information, message);
    public void Warning(string message) => Write(Serilog.Events.LogEventLevel.Warning, message);
    public void Error(string message) => Write(Serilog.Events.LogEventLevel.Error, message);
    public void Error(string message, Exception ex) => Write(Serilog.Events.LogEventLevel.Error, message, ex);
}
