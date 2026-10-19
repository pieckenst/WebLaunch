using El_Garnan_Plugin_Loader.Interfaces;
using XIVLauncher.Common.Support;

namespace WMConsole;

/// <summary>
/// Routes plugin messages through the ordinary, privacy-filtered core log. Raw FFXIV
/// protocol/process details remain in FfxivTraceLogger's separate FFXIV_TRACE-only output.
/// </summary>
internal sealed class ConsoleLogger : ILogger
{
    private static void Write(Serilog.Events.LogEventLevel level, string? message, Exception? exception = null)
    {
        var safeMessage = LogInit.SanitizeDiagnosticText(message);
        var exceptionSummary = exception is null
            ? ""
            : $" [Exception={exception.GetType().Name}: {LogInit.SanitizeDiagnosticText(exception.Message)}]";
        Serilog.Log.Write(level, "[Plugin] {PluginMessage}{ExceptionSummary}", safeMessage, exceptionSummary);
    }

    public void Debug(string message) => Write(Serilog.Events.LogEventLevel.Verbose, message);
    public void Information(string message) => Write(Serilog.Events.LogEventLevel.Information, message);
    public void Warning(string message) => Write(Serilog.Events.LogEventLevel.Warning, message);
    public void Error(string message) => Write(Serilog.Events.LogEventLevel.Error, message);
    public void Error(string message, Exception ex) => Write(Serilog.Events.LogEventLevel.Error, message, ex);
}
