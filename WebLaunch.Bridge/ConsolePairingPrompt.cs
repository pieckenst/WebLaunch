using System.Diagnostics;
using System.Threading.Channels;

namespace WebLaunch.Bridge;

/// <summary>Requires the browser's matching code to be entered at the local console.</summary>
public sealed class ConsolePairingPrompt : IPairingPrompt
{
    private readonly TextReader input;
    private readonly TextWriter output;
    private readonly bool quiet;
    private readonly Channel<ConsoleLine> lines = Channel.CreateUnbounded<ConsoleLine>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly SemaphoreSlim confirmationGate = new(1, 1);
    private readonly record struct ConsoleLine(string? Value, long ReadTimestamp);

    public ConsolePairingPrompt(TextReader input, TextWriter output, bool quiet = false)
    {
        this.input = input ?? throw new ArgumentNullException(nameof(input));
        this.output = output ?? throw new ArgumentNullException(nameof(output));
        this.quiet = quiet;

        // Console.ReadLine cannot be cancelled per pairing request. Keep exactly one reader alive
        // for the prompt's lifetime and deliver input lines to cancellable per-request readers.
        _ = Task.Run(ReadLoop);
    }

    public async Task<bool> ConfirmAsync(string origin, string code, CancellationToken cancellationToken)
    {
        if (quiet) return false; // Unattended mode only accepts browsers paired beforehand.

        try { await confirmationGate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return false; }

        try
        {
            var promptStarted = Stopwatch.GetTimestamp();
            output.WriteLine($"Pair {origin}? Browser and desktop code: {code}");
            output.WriteLine("Compare the browser code, then type the matching six digits here to approve (anything else declines):");

            while (true)
            {
                var line = await lines.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                // Discard lines already read before this pairing request began. A late line from an
                // expired prompt must not be interpreted as approval for a newer browser session.
                if (line.ReadTimestamp < promptStarted) continue;
                return string.Equals(line.Value, code, StringComparison.Ordinal);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return false; }
        catch (ChannelClosedException) { return false; }
        finally { confirmationGate.Release(); }
    }

    private void ReadLoop()
    {
        try
        {
            while (true)
            {
                var value = input.ReadLine();
                if (!lines.Writer.TryWrite(new ConsoleLine(value, Stopwatch.GetTimestamp()))) break;
                if (value is null) break;
            }
            lines.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            // Input failure closes the channel and makes all future approvals fail closed.
            lines.Writer.TryComplete(ex);
        }
    }
}
