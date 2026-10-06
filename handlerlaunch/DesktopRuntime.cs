using System.IO;
using El_Garnan_Plugin_Loader;
using WebLaunch.Bridge;
using WebLaunch.Core;
using WMConsole;

namespace handlerlaunch;

/// <summary>The shared host lifetime for graphical and console entry points.</summary>
internal sealed class DesktopRuntime : IAsyncDisposable
{
    public DesktopTrustStore Trust { get; }
    public CoreFunctions Plugins { get; }
    public DesktopLaunchService Launcher { get; }
    public BridgeHost Bridge { get; }
    private DesktopRuntime(IPairingPrompt prompt, Action<LaunchStatus> report)
    {
        Trust = new DesktopTrustStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WebLaunch"));
        Plugins = new CoreFunctions(Path.Combine(AppContext.BaseDirectory, "Plugins"), new ConsoleLogger(), false, false);
        Launcher = new DesktopLaunchService(Plugins, report);
        var development = (Environment.GetEnvironmentVariable("WEBLAUNCH_DEVELOPMENT_ORIGINS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
        try { Bridge = new BridgeHost(Trust, prompt, Launcher, development); }
        catch { Plugins.Dispose(); Trust.Dispose(); throw; }
    }
    public static async Task<DesktopRuntime> StartAsync(IPairingPrompt prompt, Action<LaunchStatus> report, CancellationToken token)
    {
        var runtime = new DesktopRuntime(prompt, report);
        try
        {
            await runtime.Plugins.InitializeAsync();
            await runtime.Bridge.StartAsync(token);
            return runtime;
        }
        catch { await runtime.DisposeAsync(); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        try { await Bridge.DisposeAsync(); }
        finally
        {
            try { await Plugins.DisposeAsync(); }
            finally
            {
                try { await CoreLibLaunchSupport.DalamudGameSession.StopAllAsync(); }
                finally { Trust.Dispose(); }
            }
        }
    }
}
