using Microsoft.AspNetCore.Components.Web;

namespace LaunchApp.Shared;

// Do not pass exception messages, render arguments or stacks to the browser console.
public sealed class SafeErrorBoundary : ErrorBoundary
{
    protected override Task OnErrorAsync(Exception exception)
    {
        BrowserDiagnostics.Report("render", exception);
        return Task.CompletedTask;
    }
}
// Blazor treats .NET stderr as fatal and opens its crash banner, even for caught errors.
public static class BrowserDiagnostics
{
    public static void Report(string operation, Exception exception) =>
        Console.WriteLine($"[WebLaunch] {operation} failed ({exception.GetType().Name}). Retry the action or reload the page.");
}

// Framework render/interop logging must not format arbitrary state or exceptions.
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
