namespace WebLaunch.Core;

/// <summary>Shared, nonsecret lifecycle reporting for all desktop modes.</summary>
public sealed class ObservedLaunchService(ILaunchService inner, Action<string, LaunchStatus> observe) : ILaunchService
{
    public async Task LaunchAsync(LaunchRequest request, IProgress<LaunchStatus> progress, CancellationToken token)
    {
        request.Validate();
        void Report(LaunchStatus status)
        {
            var safe = LaunchPresentation.Normalize(status);
            progress.Report(safe);
            // Desktop notifications or logging must never prevent a game launch.
            try { observe(request.RequestId, safe); } catch { }
        }
        Report(new("connecting", ""));
        try
        {
            await inner.LaunchAsync(request, new ForwardProgress(s => Report(s.State is "connecting" or "authenticating" or "updating" or "launching"
                ? s with { Completed = false, Success = false } : new("working", ""))), token);
            Report(new("launched", "", true, true));
        }
        catch (OperationCanceledException) { Report(new("cancelled", "", true)); throw; }
        catch { Report(new("failed", "", true)); throw; }
    }
    private sealed class ForwardProgress(Action<LaunchStatus> report) : IProgress<LaunchStatus>
    {
        public void Report(LaunchStatus value) => report(value);
    }
}
public static class LaunchPresentation
{
    public static LaunchStatus Normalize(LaunchStatus status) => status.State switch
    {
        "connecting" => new("connecting", "Preparing launch…"),
        "authenticating" => new("authenticating", "Signing in…"),
        "updating" => new("updating", "Checking and updating game files…"),
        "launching" => new("launching", "Starting the game…"),
        "launched" when status.Success && status.Completed => new("launched", "Game process started.", true, true),
        "failed" => new("failed", "Launch failed. Check the launcher and retry.", true),
        "cancelled" => new("cancelled", "Launch cancelled.", true),
        _ => new("working", "Working…")
    };
}
