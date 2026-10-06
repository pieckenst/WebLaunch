using System.Collections.Concurrent;
using System.Diagnostics;
using CoreLibLaunchSupport;
using El_Garnan_Plugin_Loader;
using El_Garnan_Plugin_Loader.Interfaces;
using XIVLauncher.Common.Addon;
using Xunit;
namespace WebLaunch.Tests;

public sealed class LifecycleTests
{
    private sealed class Logger : ILogger
    {
        public ConcurrentBag<string> Messages { get; } = [];
        public void Debug(string m) { }
        public void Information(string m) => Messages.Add(m);
        public void Warning(string m) => Messages.Add(m);
        public void Error(string m) => Messages.Add(m);
        public void Error(string m, Exception e) => Messages.Add(m);
    }
    private static void CopyPlugin(string root, string directory)
    {
        Directory.CreateDirectory(Path.Combine(root, directory));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "WebLaunch.TestPlugin.dll"), Path.Combine(root, directory, "WebLaunch.TestPlugin.dll"), true);
    }
    [Fact] public async Task LegacyPluginLoadsOnceDuplicateIsDisposedAndHostShutsDown()
    {
        using var root = new TemporaryDirectory(); CopyPlugin(root.Path, "nested/one"); CopyPlugin(root.Path, "two");
        var logger = new Logger(); var host = new CoreFunctions(root.Path, logger, false, false);
        await host.InitializeAsync(); await host.InitializeAsync();
        Assert.Equal(2, host.GetLoadedPlugins().Count());
        Assert.True(await host.GetPlugin("legacy-fixture").LaunchGameAsync(new()));
        Assert.Throws<KeyNotFoundException>(() => host.GetPlugin("missing"));
        await host.DisposeAsync();
        Assert.Empty(host.GetLoadedPlugins());
        Assert.Contains(logger.Messages, message => message.StartsWith("Plugin shutdown failed"));
        Assert.Contains("fixture-initialize", logger.Messages); Assert.Contains("fixture-shutdown", logger.Messages);
    }
    [Fact] public async Task ReloadAndDeleteTrackPluginIdRatherThanFilename()
    {
        using var root = new TemporaryDirectory(); CopyPlugin(root.Path, "one");
        var logger = new Logger(); await using var host = new CoreFunctions(root.Path, logger, true, false);
        await host.InitializeAsync();
        var original = host.GetPlugin("legacy-fixture");
        CopyPlugin(root.Path, "one");
        await Eventually(() => host.GetLoadedPlugins().FirstOrDefault(p => p.PluginId == "legacy-fixture") is { } plugin && !ReferenceEquals(plugin, original));
        File.Delete(Path.Combine(root.Path, "one", "WebLaunch.TestPlugin.dll"));
        await Eventually(() => !host.GetLoadedPlugins().Any());
    }
    private static async Task Eventually(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(50, timeout.Token);
    }
    private sealed class Addon : IAddon, INotifyAddonAfterClose
    {
        public string Name => "test addon";
        public bool Started, Stopped;
        public void Setup(int gamePid) => Started = true;
        public void GameClosed() => Stopped = true;
    }
    [Fact] public async Task DalamudAddonsFollowGameLifetimeAfterLaunchHandleIsDisposed()
    {
        var info = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c ping -n 2 127.0.0.1 > nul")
            : new ProcessStartInfo("/bin/sleep", "0.3");
        info.UseShellExecute = false;
        var game = Process.Start(info)!;
        var addon = new Addon();
        var session = DalamudGameSession.Start(game, [addon]);
        Assert.True(addon.Started); game.Dispose();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10)); Assert.True(addon.Stopped);
    }
    private sealed class Renderer(bool throwOnDispose) : IPluginRenderer
    {
        public bool IsInitialized { get; private set; }
        public bool Disposed;
        public TaskCompletionSource Rendered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Initialize() => IsInitialized = true;
        public void SetPlugins(IEnumerable<IGamePlugin> plugins) { }
        public void Render() { IsInitialized = false; Rendered.TrySetResult(); }
        public void Dispose() { Disposed = true; if (throwOnDispose) throw new InvalidOperationException("Synthetic graphics disposal failure"); }
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task RendererClosureAndDisposalFailureCannotHangHost(bool throws)
    {
        using var root = new TemporaryDirectory(); var renderer = new Renderer(throws);
        var host = new CoreFunctions(root.Path, new Logger(), false, true, () => renderer);
        await host.InitializeAsync(); host.StartRendering();
        await renderer.Rendered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(renderer.Disposed);
    }

}
