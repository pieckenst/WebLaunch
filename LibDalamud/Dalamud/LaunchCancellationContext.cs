using System;
using System.Threading;

namespace LibDalamud.Common.Dalamud;

/// <summary>
/// Carries the current launch cancellation token and trace-only diagnostic sink through legacy
/// synchronous runner APIs without adding cancellation plumbing to every runner interface.
/// </summary>
public static class LaunchCancellationContext
{
    private sealed record Context(CancellationToken CancellationToken, Action<string>? TraceSink, Func<bool>? TraceEnabled);
    private static readonly AsyncLocal<Context?> Active = new();

    public static CancellationToken CurrentCancellationToken =>
        Active.Value?.CancellationToken ?? CancellationToken.None;

    public static void WriteTrace(string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        WriteTrace(() => diagnostic);
    }

    /// <summary>
    /// Defers formatting of potentially sensitive exception details until the active opt-in trace
    /// policy confirms that a trace sink is enabled.
    /// </summary>
    public static void WriteTrace(Func<string> diagnosticFactory)
    {
        ArgumentNullException.ThrowIfNull(diagnosticFactory);
        var context = Active.Value;
        if (context?.TraceSink is null || context.TraceEnabled is not null && !context.TraceEnabled())
            return;

        context.TraceSink(diagnosticFactory());
    }

    public static IDisposable Enter(
        CancellationToken cancellationToken,
        Action<string>? traceSink = null,
        Func<bool>? traceEnabled = null)
    {
        var previous = Active.Value;
        Active.Value = new Context(
            cancellationToken,
            traceSink ?? previous?.TraceSink,
            traceEnabled ?? previous?.TraceEnabled);
        return new Scope(previous);
    }

    private sealed class Scope(Context? previous) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;

            Active.Value = previous;
        }
    }
}
