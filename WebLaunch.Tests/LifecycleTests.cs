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
    [Fact] public async Task LegacyShadowCopyExcludesNestedFiles()
    {
        using var root = new TemporaryDirectory();
        CopyPlugin(root.Path, "");
        Directory.CreateDirectory(Path.Combine(root.Path, "nested"));
        File.WriteAllText(Path.Combine(root.Path, "nested", "unrelated.txt"), "unrelated");
        File.WriteAllText(Path.Combine(root.Path, "dependency.txt"), "dependency");
        await using var host = new CoreFunctions(root.Path, new Logger(), false, false);
        await host.InitializeAsync();
        var shadow = Path.GetDirectoryName(host.GetPlugin("legacy-fixture").GetType().Assembly.Location)!;
        Assert.True(File.Exists(Path.Combine(shadow, "dependency.txt")));
        Assert.False(Directory.Exists(Path.Combine(shadow, "nested")));
    }

    [Fact] public async Task InitializationRemovesStaleShadowDirectoriesBeforeLoading()
    {
        var stale = Path.Combine(Path.GetTempPath(), "WebLaunch-plugins", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "stale.txt"), "stale");
        using var root = new TemporaryDirectory(); CopyPlugin(root.Path, "one");
        await using var host = new CoreFunctions(root.Path, new Logger(), false, false);
        await host.InitializeAsync();
        Assert.False(Directory.Exists(stale));
        Assert.True(File.Exists(host.GetPlugin("legacy-fixture").GetType().Assembly.Location));
    }

    [Fact] public async Task AnotherManagerCannotRemoveLiveShadowFiles()
    {
        using var root = new TemporaryDirectory(); CopyPlugin(root.Path, "one");
        File.WriteAllText(Path.Combine(root.Path, "one", "dependency.txt"), "lazy dependency");
        await using var first = new CoreFunctions(root.Path, new Logger(), false, false);
        await first.InitializeAsync();
        var firstShadow = Path.GetDirectoryName(first.GetPlugin("legacy-fixture").GetType().Assembly.Location)!;
        var firstRoot = Path.GetDirectoryName(firstShadow)!;
        var second = new CoreFunctions(root.Path, new Logger(), false, false);
        string secondRoot;
        await using (second)
        {
            await second.InitializeAsync();
            var secondShadow = Path.GetDirectoryName(second.GetPlugin("legacy-fixture").GetType().Assembly.Location)!;
            secondRoot = Path.GetDirectoryName(secondShadow)!;
            Assert.NotEqual(firstRoot, secondRoot);
            Assert.Equal("lazy dependency", File.ReadAllText(Path.Combine(firstShadow, "dependency.txt")));
            Assert.Throws<IOException>(() => new FileStream(firstRoot + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None));
        }
        Assert.False(Directory.Exists(secondRoot));
        Assert.Equal("lazy dependency", File.ReadAllText(Path.Combine(firstShadow, "dependency.txt")));
        await first.DisposeAsync();
        Assert.False(Directory.Exists(firstRoot));
        Assert.False(File.Exists(firstRoot + ".lock"));
    }

    [Fact] public async Task PluginActionsRunConcurrentlyAndUnloadWaitsForEveryLease()
    {
        using var root = new TemporaryDirectory(); CopyPlugin(root.Path, "one");
        var logger = new Logger();
        await using var host = new CoreFunctions(root.Path, logger, false, false);
        await host.InitializeAsync();
        var firstRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = host.UsePluginAsync("legacy-fixture", _ => firstRelease.Task);
        var second = host.UsePluginAsync("legacy-fixture", _ => { secondStarted.SetResult(); return secondRelease.Task; });
        try
        {
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await host.UsePluginAsync("second-legacy-fixture", _ => Task.FromResult(true)).WaitAsync(TimeSpan.FromSeconds(5)));
            var unload = host.UnloadAllPluginsAsync();
            Assert.False(unload.IsCompleted);
            firstRelease.SetResult(true); await first;
            Assert.False(unload.IsCompleted);
            Assert.DoesNotContain("fixture-shutdown", logger.Messages);
            secondRelease.SetResult(true); await second;
            await unload.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(host.GetLoadedPlugins());
            Assert.Contains("fixture-shutdown", logger.Messages);
        }
        finally { firstRelease.TrySetResult(true); secondRelease.TrySetResult(true); }
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task PluginActionReceivesCallerAndHostCancellation(bool disposeHost)
    {
        using var root = new TemporaryDirectory(); CopyPlugin(root.Path, "one");
        await using var host = new CoreFunctions(root.Path, new Logger(), false, false);
        await host.InitializeAsync();
        using var caller = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var action = host.UsePluginAsync("legacy-fixture", async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return true;
        }, caller.Token);
        await started.Task;
        if (disposeHost) await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        else caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action.WaitAsync(TimeSpan.FromSeconds(5)));
        if (!disposeHost)
        {
            await Assert.ThrowsAsync<IOException>(() => host.UsePluginAsync<bool>("legacy-fixture", _ => throw new IOException("synthetic")));
            await host.UnloadAllPluginsAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
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

    [Theory]
    [InlineData("{")]
    [InlineData("{\"version\":2,\"assembly\":\"bad.dll\"}")]
    [InlineData("{\"version\":1,\"assembly\":\"../escape.dll\"}")]
    [InlineData("{\"version\":1}")]
    public async Task InvalidManifestDoesNotPreventOtherPluginsLoading(string manifest)
    {
        using var root = new TemporaryDirectory();
        CopyPlugin(root.Path, "valid");
        var invalid = Directory.CreateDirectory(Path.Combine(root.Path, "invalid")).FullName;
        File.WriteAllText(Path.Combine(invalid, "plugin.json"), manifest);
        await using var host = new CoreFunctions(root.Path, new Logger(), false, false);
        var errors = new List<PluginErrorEventArgs>();
        host.PluginError += (_, error) => errors.Add(error);
        await host.InitializeAsync();
        Assert.Equal(2, host.GetLoadedPlugins().Count());
        Assert.Equal(Path.Combine(invalid, "plugin.json"), Assert.Single(errors).PluginPath);
    }

    [Fact] public async Task LinkedDirectoryDoesNotPreventOtherPluginsLoading()
    {
        using var root = new TemporaryDirectory(); using var outside = new TemporaryDirectory();
        CopyPlugin(root.Path, "valid");
        try { Directory.CreateSymbolicLink(Path.Combine(root.Path, "invalid"), outside.Path); }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows()) { return; }
        await using var host = new CoreFunctions(root.Path, new Logger(), false, false);
        var errors = new List<PluginErrorEventArgs>();
        host.PluginError += (_, error) => errors.Add(error);
        await host.InitializeAsync();
        Assert.Equal(2, host.GetLoadedPlugins().Count());
        Assert.Equal(Path.Combine(root.Path, "invalid"), Assert.Single(errors).PluginPath);
    }

    private sealed class ControlledAddon : IRunnableAddon, INotifyAddonAfterClose
    {
        public string Name => "controlled";
        public Action OnSetup = () => { }, OnClose = () => { };
        public bool Ran, Closed;
        public void Setup(int pid) => OnSetup();
        public void Run() => Ran = true;
        public void GameClosed() { Closed = true; OnClose(); }
    }
    private sealed class WorkerAddon(Action<CancellationToken> work) : IPersistentAddon, INotifyAddonAfterClose
    {
        public string Name => "worker";
        public int Closed;
        public void Setup(int pid) { }
        public void DoWork(object state) => work((CancellationToken)state);
        public void GameClosed() => Closed++;
    }
    private static Process LongRunningGame() => Process.Start(OperatingSystem.IsWindows()
        ? new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 > nul") { UseShellExecute = false }
        : new ProcessStartInfo("/bin/sleep", "30") { UseShellExecute = false })!;

    [Fact] public async Task StopDuringStartupWaitsForRollbackAndSkipsFurtherAddonWork()
    {
        using var game = LongRunningGame();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new ControlledAddon { OnSetup = () => { entered.SetResult(); release.Wait(TimeSpan.FromSeconds(10)); } };
        var second = new ControlledAddon();
        var start = Task.Run(() => DalamudGameSession.Start(game, [first, second]));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stop = DalamudGameSession.StopAllAsync();
            Assert.False(stop.IsCompleted);
            release.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await start);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop);
            Assert.True(first.Closed);
            Assert.False(first.Ran || second.Ran || second.Closed);
        }
        finally { release.Set(); game.Kill(true); await game.WaitForExitAsync(); }
    }

    [Fact] public async Task BackgroundAddonFailureTriggersCleanupBeforeGameExit()
    {
        using var game = LongRunningGame();
        using var fail = new ManualResetEventSlim();
        var worker = new WorkerAddon(_ => { fail.Wait(TimeSpan.FromSeconds(10)); throw new IOException("synthetic worker failure"); });
        var other = new ControlledAddon();
        try
        {
            var session = DalamudGameSession.Start(game, [worker, other]);
            fail.Set();
            await Assert.ThrowsAsync<IOException>(() => session.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(game.HasExited);
            Assert.Equal(1, worker.Closed); Assert.True(other.Closed);
        }
        finally { fail.Set(); game.Kill(true); await game.WaitForExitAsync(); }
    }

    [Fact] public async Task StopWaitsForWorkerAndContinuesAfterCancellationCallbackFailure()
    {
        using var game = LongRunningGame();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var worker = new WorkerAddon(token =>
        {
            using var registration = token.Register(() => throw new IOException("synthetic cancellation failure"));
            entered.SetResult(); release.Wait(TimeSpan.FromSeconds(10));
        });
        var other = new ControlledAddon();
        try
        {
            var session = DalamudGameSession.Start(game, [worker, other]);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stop = session.StopAsync();
            Assert.False(stop.IsCompleted);
            release.Set();
            await Assert.ThrowsAsync<AggregateException>(() => stop.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, worker.Closed); Assert.True(other.Closed);
        }
        finally { release.Set(); game.Kill(true); await game.WaitForExitAsync(); }
    }

    [Fact] public void FailedLaunchSessionTerminatesAndDisposesGame()
    {
        using var game = LongRunningGame();
        using var observer = Process.GetProcessById(game.Id);
        _ = observer.Handle;
        var failure = new InvalidOperationException("synthetic startup failure");
        var addon = new ControlledAddon { OnSetup = () => throw failure };
        try
        {
            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => networklogic.StartGameSession(game, [addon])));
            Assert.True(observer.WaitForExit(5000));
            Assert.Throws<InvalidOperationException>(() => game.Id);
            Assert.True(addon.Closed);
        }
        finally { if (!observer.HasExited) { observer.Kill(true); observer.WaitForExit(); } }
    }

    [Fact] public void StartupRollbackCleansEveryAddonDespiteCloseFailures()
    {
        using var game = LongRunningGame();
        var first = new ControlledAddon { OnClose = () => throw new IOException("first cleanup") };
        var second = new ControlledAddon { OnClose = () => throw new IOException("second cleanup") };
        var third = new ControlledAddon { OnSetup = () => throw new InvalidOperationException("setup") };
        try
        {
            var error = Assert.Throws<AggregateException>(() => DalamudGameSession.Start(game, [first, second, third]));
            Assert.Equal(3, error.Flatten().InnerExceptions.Count);
            Assert.True(first.Closed && second.Closed && third.Closed);
        }
        finally { game.Kill(true); game.WaitForExit(); }
    }

}
