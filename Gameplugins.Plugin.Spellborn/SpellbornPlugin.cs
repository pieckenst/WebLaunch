using System.Diagnostics;
using El_Garnan_Plugin_Loader.Base;
using El_Garnan_Plugin_Loader.Interfaces;
using El_Garnan_Plugin_Loader.Models;
using WebLaunch.Core;

namespace GamePlugins.Spellborn;

public sealed class SpellbornPlugin : GamePluginBase, ICancellableGamePlugin
{
    private readonly HttpClient client;
    public SpellbornPlugin(ILogger logger) : this(logger, new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromHours(2) }) { }
    internal SpellbornPlugin(ILogger logger, HttpClient client) : base(logger) => this.client = client;
    public override string PluginId => "spellborn-launcher";
    public override string Name => "Chronicles of Spellborn";
    public override string Description => "Installs, updates and launches Chronicles of Spellborn";
    public override string TargetApplication => "Sb_client.exe";
    public override Version Version => new(1, 0, 0);
    public override IReadOnlyCollection<PluginDependency> Dependencies => [];
    protected override Task InitializeInternalAsync() => Task.CompletedTask;
    protected override Task ShutdownInternalAsync() { client.Dispose(); return Task.CompletedTask; }
    protected override Task<bool> LaunchGameInternalAsync(GameLaunchParameters parameters) =>
        LaunchCoreAsync(parameters, new Progress<LaunchStatus>(), CancellationToken.None);
    public async Task<bool> LaunchAsync(GameLaunchParameters parameters, IProgress<LaunchStatus> progress, CancellationToken cancellationToken)
    {
        await LaunchLock.WaitAsync(cancellationToken);
        try { return await LaunchCoreAsync(parameters, progress, cancellationToken); }
        finally { LaunchLock.Release(); }
    }
    private async Task<bool> LaunchCoreAsync(GameLaunchParameters parameters, IProgress<LaunchStatus> progress, CancellationToken token)
    {
        var versions = new SpellbornVersionStore();
        var updater = new SpellbornUpdater(client, versions, new ArchiveInstaller());
        try { await updater.EnsureUpdatedAsync(parameters.GamePath, progress, token); }
        catch (Exception) when (!token.IsCancellationRequested && !updater.InstallationStarted)
        {
            token.ThrowIfCancellationRequested();
            var installed = versions.Read(parameters.GamePath);
            if (string.IsNullOrWhiteSpace(installed) || installed == "false") throw;
            Logger.Warning("Spellborn update failed; launching the installed version.");
        }
        var exe = SafePath.Resolve(parameters.GamePath, "bin/client/Sb_client.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("Spellborn executable is missing.");
        token.ThrowIfCancellationRequested();
        progress.Report(new("launching", "Starting Chronicles of Spellborn…"));
        using var process = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) });
        return process is not null && !process.HasExited;
    }
}

/// <summary>
/// Compatibility adapter for older callers of the original Spellborn plugin assembly.
/// Version persistence is implemented in WebLaunch.Core so the desktop host does not need
/// a static reference to this dynamically loaded plugin.
/// </summary>
public sealed class SpellbornVersionStore : IInstalledVersionStore
{
    private readonly WebLaunch.Core.SpellbornVersionStore inner = new();

    public string? Read(string root) => inner.Read(root);

    public void Write(string root, string version) => inner.Write(root, version);
}
