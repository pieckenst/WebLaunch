using El_Garnan_Plugin_Loader.Base;
using El_Garnan_Plugin_Loader.Interfaces;
using El_Garnan_Plugin_Loader.Models;
namespace WebLaunch.TestPlugin;
// Only the original plugin API is used. This fixture deliberately does not implement new interfaces.
public class LegacyPlugin(ILogger logger) : GamePluginBase(logger)
{
    public override string PluginId => "legacy-fixture";
    public override string Name => "Legacy fixture";
    public override string Description => "Compatibility fixture";
    public override string TargetApplication => "test";
    public override Version Version => new(1, 0);
    public override bool SupportsHotReload => true;
    public override IReadOnlyCollection<PluginDependency> Dependencies => [];
    protected override Task InitializeInternalAsync() { Logger.Information("fixture-initialize"); return Task.CompletedTask; }
    protected override Task ShutdownInternalAsync() { Logger.Information("fixture-shutdown"); return Task.CompletedTask; }
    protected override Task<bool> LaunchGameInternalAsync(GameLaunchParameters parameters)
    {
        var resources = Path.Combine(Path.GetDirectoryName(GetType().Assembly.Location)!, "resources");
        if (!Directory.Exists(resources)) return Task.FromResult(true);

        var icon = Path.Combine(resources, "ui", "icon.txt");
        return Task.FromResult(File.Exists(icon) && File.ReadAllText(icon) == "private resource");
    }
}

#if !SINGLE_PLUGIN_FIXTURE
public sealed class SecondLegacyPlugin(ILogger logger) : LegacyPlugin(logger)
{
    public override string PluginId => "second-legacy-fixture";
    protected override Task ShutdownInternalAsync() => throw new InvalidOperationException("Synthetic shutdown failure");
}
#endif
