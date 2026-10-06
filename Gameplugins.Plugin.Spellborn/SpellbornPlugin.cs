using System.Diagnostics;
using El_Garnan_Plugin_Loader.Base;
using El_Garnan_Plugin_Loader.Interfaces;
using El_Garnan_Plugin_Loader.Models;
using WebLaunch.Core;

namespace GamePlugins.Spellborn;

public sealed class SpellbornPlugin : GamePluginBase, ICancellableGamePlugin
{
    private readonly HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromHours(2) };
    public SpellbornPlugin(ILogger logger) : base(logger) { }
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
        var updater = new SpellbornUpdater(client, new SpellbornVersionStore(), new ArchiveInstaller());
        await updater.EnsureUpdatedAsync(parameters.GamePath, progress, token);
        var exe = SafePath.Resolve(parameters.GamePath, "bin/client/Sb_client.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("Spellborn executable is missing.");
        token.ThrowIfCancellationRequested();
        progress.Report(new("launching", "Starting Chronicles of Spellborn…"));
        using var process = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) });
        return process is not null && !process.HasExited;
    }
}

public sealed class SpellbornVersionStore : IInstalledVersionStore
{
    public string? Read(string root)
    {
        var file = SafePath.Resolve(root, ".weblaunch-version");
        if (File.Exists(file)) return File.ReadAllText(file).Trim();
        if (OperatingSystem.IsWindows())
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\The Chronicles of Spellborn");
            if (key?.GetValue("installPath") is string previous && string.Equals(Path.GetFullPath(previous), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                return key.GetValue("installedVersion") as string;
        }
        return null;
    }
    public void Write(string root, string version)
    {
        var file = SafePath.Resolve(root, ".weblaunch-version");
        File.WriteAllText(file + ".tmp", version);
        File.Move(file + ".tmp", file, true);
        if (OperatingSystem.IsWindows())
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\The Chronicles of Spellborn");
            key.SetValue("installPath", root); key.SetValue("installedVersion", version);
        }
    }
}
