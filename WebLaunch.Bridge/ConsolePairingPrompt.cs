namespace WebLaunch.Bridge;

/// <summary>Requires the browser's matching code to be entered at the local console.</summary>
public sealed class ConsolePairingPrompt(TextReader input, TextWriter output, bool quiet = false) : IPairingPrompt
{
    private Task<string?>? pendingRead;
    private readonly object gate = new();
    public async Task<bool> ConfirmAsync(string origin, string code, CancellationToken cancellationToken)
    {
        if (quiet) return false; // Unattended mode only accepts browsers paired beforehand.
        Task<string?> read;
        lock (gate)
        {
            if (pendingRead is { IsCompleted: false }) return false;
            output.WriteLine($"Pair {origin}? Browser and desktop code: {code}");
            output.WriteLine("Compare the browser code, then type the matching six digits here to approve (anything else declines):");
            // Console readers can block synchronously and ignore cancellation. Keep at most one
            // reader outstanding; timeouts cannot leave competing readers consuming future codes.
            read = pendingRead = Task.Run(() => input.ReadLine(), CancellationToken.None);
        }
        try { return await read.WaitAsync(cancellationToken) == code; }
        catch (OperationCanceledException) { return false; }
    }
}
