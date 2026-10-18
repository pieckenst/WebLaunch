using El_Garnan_Plugin_Loader.Interfaces;

namespace WMConsole;

/// <summary>
/// Writes plugin log events without persisting plugin-controlled message text or exception payloads.
/// </summary>
internal sealed class ConsoleLogger : ILogger
{
    private static void Write(Serilog.Events.LogEventLevel level, string? message, Exception? exception = null) =>
        Serilog.Log.Write(
            level,
            "[Plugin] Diagnostic text redacted; MessageLength={MessageLength}; ExceptionType={ExceptionType}",
            message?.Length ?? 0,
            exception?.GetType().Name ?? "none");

    public void Debug(string message) => Write(Serilog.Events.LogEventLevel.Verbose, message);
    public void Information(string message) => Write(Serilog.Events.LogEventLevel.Information, message);
    public void Warning(string message) => Write(Serilog.Events.LogEventLevel.Warning, message);
    public void Error(string message) => Write(Serilog.Events.LogEventLevel.Error, message);
    public void Error(string message, Exception ex) => Write(Serilog.Events.LogEventLevel.Error, message, ex);
}
