using System.IO;
using El_Garnan_Plugin_Loader;
using Serilog;
using WebLaunch.Bridge;
using WebLaunch.Core;
using WMConsole;

namespace handlerlaunch;

/// <summary>The shared host lifetime for graphical and console entry points.</summary>
internal sealed class DesktopRuntime : IAsyncDisposable
{
    public DesktopTrustStore Trust { get; }
    public CoreFunctions Plugins { get; }
    public ILaunchService Launcher { get; }
    public BridgeHost Bridge { get; }

    private DesktopRuntime(IPairingPrompt prompt, Action<LaunchStatus> report, string mode)
    {
        Trust = new DesktopTrustStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WebLaunch"));

        Plugins = new CoreFunctions(
            Path.Combine(AppContext.BaseDirectory, "Plugins"),
            new ConsoleLogger(),
            enableHotReload: false,
            useStandaloneWindow: false,
            diagnosticsDirectory: null,
            debugDiagnostics: DesktopDiagnostics.DebugEnabled);

        Plugins.PluginLoaded += (_, e) => DesktopDiagnostics.Write(DiagnosticEvent.PluginLoaded, pluginId: e.Plugin.PluginId);
        Plugins.PluginUnloaded += (_, e) => DesktopDiagnostics.Write(DiagnosticEvent.PluginUnloaded, pluginId: e.PluginId);
        Plugins.PluginReloaded += (_, e) => DesktopDiagnostics.Write(DiagnosticEvent.PluginReloaded, pluginId: e.PluginId);
        Plugins.PluginError += (_, e) =>
        {
            var hresult = unchecked((uint)e.Error.HResult).ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
            DesktopDiagnostics.Write(
                DiagnosticEvent.PluginError,
                detail: $"loader-exception-{e.Error.GetType().Name}-hr-{hresult}");
        };

        var notifications = new DesktopNotifications(mode);
        Launcher = new ObservedLaunchService(
            new DesktopLaunchService(Plugins, _ => { }),
            (id, status) =>
            {
                DesktopDiagnostics.Write(DiagnosticEvent.LaunchState, id, status);
                report(status);
                notifications.Report(id, status);
            },
            (id, game, exception) =>
            {
                var pluginId = game switch
                {
                    "ffxiv" => "ffxiv-launcher",
                    "spellborn" => "spellborn-launcher",
                    _ => null
                };
                var hresult = unchecked((uint)exception.HResult).ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
                DesktopDiagnostics.Write(
                    DiagnosticEvent.LaunchFailed,
                    id,
                    pluginId: pluginId,
                    detail: $"exception-{exception.GetType().Name}-hr-{hresult}");
            });

        var development = (Environment.GetEnvironmentVariable("WEBLAUNCH_DEVELOPMENT_ORIGINS") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);

        try
        {
            Bridge = new BridgeHost(
                Trust,
                prompt,
                Launcher,
                development,
                mode,
                new WindowsGameFolderPicker(),
                diagnosticSink: (code, requestId, detail) =>
                    DesktopDiagnostics.Write(code, requestId, detail: detail));
        }
        catch
        {
            Plugins.Dispose();
            Trust.Dispose();
            throw;
        }
    }

    public static async Task<DesktopRuntime> StartAsync(
        IPairingPrompt prompt,
        Action<LaunchStatus> report,
        CancellationToken token,
        string mode = "gui")
    {
        DesktopDiagnostics.Write(DiagnosticEvent.HostStarting);

        var runtime = new DesktopRuntime(prompt, report, mode);
        try
        {
            await runtime.Plugins.InitializeAsync();

            // Keep the bridge available so one broken/missing optional plugin cannot disable
            // the desktop host, but record the exact readiness gap before a launch is requested.
            var loadedPluginIds = runtime.Plugins.GetLoadedPlugins()
                .Select(plugin => plugin.PluginId)
                .ToHashSet(StringComparer.Ordinal);
            if (!loadedPluginIds.Contains("ffxiv-launcher"))
                DesktopDiagnostics.Write(DiagnosticEvent.PluginError, pluginId: "ffxiv-launcher", detail: "required-plugin-not-loaded-at-startup");
            if (!loadedPluginIds.Contains("spellborn-launcher"))
                DesktopDiagnostics.Write(DiagnosticEvent.PluginWarning, pluginId: "spellborn-launcher", detail: "optional-plugin-not-loaded-at-startup");

            await runtime.Bridge.StartAsync(token);
            DesktopDiagnostics.Write(DiagnosticEvent.HostReady);
            Log.Information("[{Component}] Desktop runtime ready.", nameof(DesktopRuntime));
            return runtime;
        }
        catch (Exception ex)
        {
            var hresult = unchecked((uint)ex.HResult).ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
            DesktopDiagnostics.Write(DiagnosticEvent.HostFailed, detail: $"exception-{ex.GetType().Name}-hr-{hresult}");
            await runtime.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Bridge.DisposeAsync();
        }
        finally
        {
            try
            {
                await Plugins.DisposeAsync();
            }
            finally
            {
                try
                {
                    await CoreLibLaunchSupport.DalamudGameSession.StopAllAsync();
                }
                finally
                {
                    try { CoreLibLaunchSupport.FfxivTraceLogger.Shutdown(); }
                    finally
                    {
                        Trust.Dispose();
                        DesktopDiagnostics.Write(DiagnosticEvent.HostStopped);
                    }
                }
            }
        }
    }
}
