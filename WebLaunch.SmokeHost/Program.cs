using System.Security.Cryptography;
using WebLaunch.Bridge;
using WebLaunch.Core;

// Test-only host: no native launcher references, provider APIs, or real account support.
using var trust = new SyntheticTrust();
await using var bridge = new BridgeHost(trust, new SyntheticPrompt(), new SyntheticLauncher(), ["http://localhost:5148", "http://127.0.0.1:5148"]);
await bridge.StartAsync();
Console.WriteLine("Synthetic bridge ready at 127.0.0.1:47832");
await Task.Delay(Timeout.Infinite);

sealed class SyntheticTrust : IBridgeTrustStore, IDisposable
{
    public ECDsa Identity { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly HashSet<string> keys = [];
    public bool IsTrusted(string origin, string fingerprint) => keys.Contains(origin + fingerprint);
    public void Trust(string origin, string fingerprint) => keys.Add(origin + fingerprint);
    public void ForgetAll() => keys.Clear();
    public void Dispose() => Identity.Dispose();
}
sealed class SyntheticPrompt : IPairingPrompt
{
    public Task<bool> ConfirmAsync(string origin, string code, CancellationToken cancellationToken) => Task.FromResult(true);
}
sealed class SyntheticLauncher : ILaunchService
{
    public async Task LaunchAsync(LaunchRequest request, IProgress<LaunchStatus> progress, CancellationToken token)
    {
        if (request.Game != "spellborn" && !request.Username.StartsWith("synthetic-", StringComparison.Ordinal))
            throw new InvalidOperationException("Only synthetic accounts are accepted by this test host.");
        foreach (var state in new[] { "authenticating", "updating", "launching" })
        {
            progress.Report(new(state, "Synthetic " + state));
            await Task.Delay(700, token);
        }
        if (request.Username == "synthetic-failure") throw new InvalidOperationException("Synthetic failure");
    }
}
