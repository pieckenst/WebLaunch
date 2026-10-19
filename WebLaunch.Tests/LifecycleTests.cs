using System.Collections.Concurrent;
using System.Diagnostics;
using CoreLibLaunchSupport;
using El_Garnan_Plugin_Loader;
using El_Garnan_Plugin_Loader.Interfaces;
using XIVLauncher.Common.Addon;
using XIVLauncher.Common.Game.Exceptions;
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
    [Fact] public async Task LegacyShadowCopyPreservesPrivateResourcesWithoutCopyingNestedPlugins()
    {
        using var root = new TemporaryDirectory();
        CopyPlugin(root.Path, "");
        Directory.CreateDirectory(Path.Combine(root.Path, "resources", "ui"));
        File.WriteAllText(Path.Combine(root.Path, "resources", "ui", "icon.txt"), "private resource");
        Directory.CreateDirectory(Path.Combine(root.Path, "lib", "managed"));
        File.WriteAllText(Path.Combine(root.Path, "lib", "managed", "PrivateDependency.dll"), "private dependency payload");
        Directory.CreateDirectory(Path.Combine(root.Path, "resources", "nested-plugin"));
        File.WriteAllText(Path.Combine(root.Path, "resources", "nested-plugin", "plugin.json"), "{\"version\":1,\"id\":\"nested-other\",\"assembly\":\"Other.dll\"}");
        File.WriteAllText(Path.Combine(root.Path, "resources", "nested-plugin", "Other.dll"), "not a plugin dependency");
        Directory.CreateDirectory(Path.Combine(root.Path, "nested-plugin"));
        File.WriteAllText(Path.Combine(root.Path, "nested-plugin", "Unrelated.dll"), "not a plugin dependency");
        File.WriteAllText(Path.Combine(root.Path, "dependency.txt"), "dependency");
        await using var host = new CoreFunctions(root.Path, new Logger(), false, false);
        await host.InitializeAsync();
        var shadow = Path.GetDirectoryName(host.GetPlugin("legacy-fixture").GetType().Assembly.Location)!;
        Assert.True(File.Exists(Path.Combine(shadow, "dependency.txt")));
        Assert.Equal("private resource", File.ReadAllText(Path.Combine(shadow, "resources", "ui", "icon.txt")));
        Assert.Equal("private dependency payload", File.ReadAllText(Path.Combine(shadow, "lib", "managed", "PrivateDependency.dll")));
        Assert.False(Directory.Exists(Path.Combine(shadow, "nested-plugin")));
        Assert.False(Directory.Exists(Path.Combine(shadow, "resources", "nested-plugin")));
        Assert.True(await host.GetPlugin("legacy-fixture").LaunchGameAsync(new()));
    }

    private static string CreateManifestBundle(string root, string id = "legacy-fixture", bool multiplePlugins = false)
    {
        var bundle = Directory.CreateDirectory(Path.Combine(root, "bundle")).FullName;
        var nested = Directory.CreateDirectory(Path.Combine(bundle, "lib", "managed")).FullName;
        var fixture = multiplePlugins ? "WebLaunch.TestPlugin.dll" : "WebLaunch.TestManifestPlugin.dll";
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", fixture), Path.Combine(nested, fixture));
        WriteManifest(bundle, id, "lib/managed/" + fixture);
        File.WriteAllText(Path.Combine(bundle, "asset.txt"), "bundle asset");
        return bundle;
    }
    private static void WriteManifest(string bundle, string id, string assembly) =>
        File.WriteAllText(Path.Combine(bundle, "plugin.json"),
            System.Text.Json.JsonSerializer.Serialize(new { version = 1, id, assembly }));

    [Fact] public async Task ManifestNestedAssemblyPreservesBundleRootAndAssets()
    {
        using var root = new TemporaryDirectory();
        CreateManifestBundle(root.Path);
        await using var host = new CoreFunctions(root.Path, new Logger(), false, false);
        await host.InitializeAsync();
        var plugin = Assert.Single(host.GetLoadedPlugins());
        var shadowAssembly = plugin.GetType().Assembly.Location;
        var shadowRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(shadowAssembly)!, "../.."));
        Assert.Equal(Path.Combine(shadowRoot, "lib", "managed", "WebLaunch.TestManifestPlugin.dll"), shadowAssembly);
        Assert.Equal("bundle asset", File.ReadAllText(Path.Combine(shadowRoot, "asset.txt")));
        Assert.True(File.Exists(Path.Combine(shadowRoot, "plugin.json")));
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task InvalidAssemblyReportsFailureOnlyForManifestBundles(bool manifest)
    {
        using var root = new TemporaryDirectory();
        var assembly = manifest
            ? Path.Combine(CreateManifestBundle(root.Path), "lib", "managed", "WebLaunch.TestManifestPlugin.dll")
            : Path.Combine(root.Path, "NativeDependency.dll");
        File.WriteAllText(assembly, "not a managed assembly");
        var logger = new Logger();
        await using var host = new CoreFunctions(root.Path, logger, false, false);
        var errors = new List<PluginErrorEventArgs>();
        host.PluginError += (_, error) => errors.Add(error);

        await host.InitializeAsync();

        Assert.Empty(host.GetLoadedPlugins());
        if (manifest)
        {
            Assert.IsType<BadImageFormatException>(Assert.Single(errors).Error);
            Assert.Contains(logger.Messages, message => message.Contains("BadImageFormatException"));
        }
        else
        {
            Assert.Empty(errors);
            Assert.DoesNotContain(logger.Messages, message => message.Contains("BadImageFormatException"));
        }
    }

    [Fact]
    public async Task ThrowingEventObserversCannotAbortPluginDiscoveryOrLifecycleNotifications()
    {
        using var root = new TemporaryDirectory();
        CopyPlugin(root.Path, "valid");
        var invalid = Directory.CreateDirectory(Path.Combine(root.Path, "invalid-bundle")).FullName;
        File.WriteAllText(Path.Combine(invalid, "plugin.json"), "{ malformed json");
        var logger = new Logger();
        await using var host = new CoreFunctions(root.Path, logger, false, false);
        var reportedErrors = 0;
        var loadedNotifications = 0;
        host.PluginError += (_, _) => throw new InvalidOperationException("faulty error observer");
        host.PluginError += (_, _) => Interlocked.Increment(ref reportedErrors);
        host.PluginLoaded += (_, _) => throw new InvalidOperationException("faulty load observer");
        host.PluginLoaded += (_, _) => Interlocked.Increment(ref loadedNotifications);

        await host.InitializeAsync();

        Assert.Equal(2, host.GetLoadedPlugins().Count());
        Assert.Equal(1, reportedErrors);
        Assert.Equal(2, loadedNotifications);
        Assert.Equal(3, logger.Messages.Count(message => message.Contains("Plugin lifecycle observer failed")));
    }

    [Theory]
    [InlineData("wrong-id", false)]
    [InlineData("legacy-fixture", true)]
    public async Task NestedManifestEnforcesPluginIdentityAndSinglePluginRule(string id, bool multiplePlugins)
    {
        using var root = new TemporaryDirectory();
        CreateManifestBundle(root.Path, id, multiplePlugins);
        await using var host = new CoreFunctions(root.Path, new Logger(), false, false);
        var errors = new List<PluginErrorEventArgs>();
        host.PluginError += (_, error) => errors.Add(error);
        await host.InitializeAsync();
        Assert.Empty(host.GetLoadedPlugins());
        Assert.IsType<InvalidDataException>(Assert.Single(errors).Error);
    }

    [Theory]
    [InlineData("../outside.dll")]
    [InlineData("lib/../../../outside.dll")]
    [InlineData("/outside.dll")]
    public async Task ManifestAssemblyCannotEscapeBundle(string assembly)
    {
        using var root = new TemporaryDirectory();
        var bundle = CreateManifestBundle(root.Path);
        WriteManifest(bundle, "legacy-fixture", assembly);
        await using var host = new CoreFunctions(root.Path, new Logger(), false, false);
        var errors = new List<PluginErrorEventArgs>();
        host.PluginError += (_, error) => errors.Add(error);
        await host.InitializeAsync();
        Assert.Empty(host.GetLoadedPlugins());
        Assert.IsType<InvalidDataException>(Assert.Single(errors).Error);
    }

    [Theory]
    [InlineData("assembly")]
    [InlineData("manifest")]
    [InlineData("asset-directory")]
    [InlineData("asset-file")]
    public async Task ManifestBundlesRejectLinks(string linkedItem)
    {
        using var root = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        var bundle = CreateManifestBundle(root.Path);
        try
        {
            if (linkedItem == "asset-directory")
                Directory.CreateSymbolicLink(Path.Combine(bundle, "assets"), outside.Path);
            else
            {
                var path = linkedItem switch
                {
                    "manifest" => Path.Combine(bundle, "plugin.json"),
                    "asset-file" => Path.Combine(bundle, "asset.txt"),
                    _ => Path.Combine(bundle, "lib", "managed", "WebLaunch.TestManifestPlugin.dll")
                };
                var target = Path.Combine(outside.Path, Path.GetFileName(path));
                File.Move(path, target);
                File.CreateSymbolicLink(path, target);
            }
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows()) { return; }
        await using var host = new CoreFunctions(root.Path, new Logger(), false, false);
        var errors = new List<PluginErrorEventArgs>();
        host.PluginError += (_, error) => errors.Add(error);
        await host.InitializeAsync();
        Assert.Empty(host.GetLoadedPlugins());
        Assert.IsType<InvalidDataException>(Assert.Single(errors).Error);
    }

    [Theory]
    [InlineData("plugin.json")]
    [InlineData("asset.txt")]
    public async Task BundleRootChangesReloadNestedManifestPlugin(string changedFile)
    {
        using var root = new TemporaryDirectory();
        var bundle = CreateManifestBundle(root.Path);
        await using var host = new CoreFunctions(root.Path, new Logger(), true, false);
        await host.InitializeAsync();
        var original = host.GetPlugin("legacy-fixture");
        var reloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.PluginReloaded += (_, _) => reloaded.TrySetResult();
        File.AppendAllText(Path.Combine(bundle, changedFile), " ");
        await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var replacement = Assert.Single(host.GetLoadedPlugins());
        Assert.NotSame(original, replacement);
        var shadowRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(replacement.GetType().Assembly.Location)!, "../.."));
        Assert.Equal(File.ReadAllText(Path.Combine(bundle, changedFile)), File.ReadAllText(Path.Combine(shadowRoot, changedFile)));

        // Reload must keep enforcing the originating manifest, even after a successful replacement.
        var rejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.PluginError += (_, error) =>
        {
            if (error.Error is InvalidDataException) rejected.TrySetResult();
        };
        WriteManifest(bundle, "wrong-id", "lib/managed/WebLaunch.TestManifestPlugin.dll");
        await rejected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(host.GetLoadedPlugins());
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
        // Unload is asynchronous for collectible contexts. A test-only collection lets Windows
        // release file handles deterministically; production cleanup continues retrying in the background.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        await Eventually(() => !Directory.Exists(secondRoot), TimeSpan.FromSeconds(20));
        Assert.Equal("lazy dependency", File.ReadAllText(Path.Combine(firstShadow, "dependency.txt")));
        await first.DisposeAsync();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        await Eventually(() => !Directory.Exists(firstRoot) && !File.Exists(firstRoot + ".lock"), TimeSpan.FromSeconds(20));
    }

    [Fact] public async Task SynchronousDisposeDoesNotCaptureTheCallerSynchronizationContext()
    {
        using var root = new TemporaryDirectory();
        CopyPlugin(root.Path, "one");
        var host = new CoreFunctions(root.Path, new Logger(), false, false);
        await host.InitializeAsync();

        var dispose = Task.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new NonPumpingSynchronizationContext());
            host.Dispose();
        });
        await dispose.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Empty(host.GetLoadedPlugins());
    }

    private sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            // Deliberately never run posted continuations, reproducing a blocked UI context.
        }
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

    [Theory]
    [InlineData("legacy-fixture")]
    [InlineData("second-legacy-fixture")]
    public async Task HotReloadDefersTheWholeGroupWhileLeasedWithoutBlockingNewActions(string leasedId)
    {
        using var root = new TemporaryDirectory(); CopyPlugin(root.Path, "one");
        var asset = Path.Combine(root.Path, "one", "asset.txt");
        File.WriteAllText(asset, "original");
        await using var host = new CoreFunctions(root.Path, new Logger(), true, false);
        await host.InitializeAsync();
        var originals = host.GetLoadedPlugins().ToArray();
        var reloaded = new ConcurrentBag<string>();
        host.PluginReloaded += (_, e) => reloaded.Add(e.PluginId);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var action = host.UsePluginAsync(leasedId, _ => release.Task);
        try
        {
            File.WriteAllText(asset, "updated");
            // Allow several watcher ticks while the lease remains active.
            await Task.Delay(1200);
            foreach (var original in originals)
            {
                var current = await host.UsePluginAsync(original.PluginId, plugin => Task.FromResult(plugin))
                    .WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Same(original, current);
            }
            Assert.Empty(reloaded);
        }
        finally { release.TrySetResult(true); await action; }

        // No additional filesystem event is sent: the deferred paths must trigger the retry.
        await Eventually(() => originals.All(original => reloaded.Contains(original.PluginId)));
        foreach (var original in originals)
        {
            var replacement = host.GetPlugin(original.PluginId);
            Assert.NotSame(original, replacement);
            Assert.Equal("updated", File.ReadAllText(Path.Combine(Path.GetDirectoryName(replacement.GetType().Assembly.Location)!, "asset.txt")));
        }
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
    [Fact] public async Task MovingPluginDirectoryOutsideSearchRootUnloadsPlugins()
    {
        using var root = new TemporaryDirectory(); using var outside = new TemporaryDirectory();
        CopyPlugin(root.Path, "one");
        await using var host = new CoreFunctions(root.Path, new Logger(), true, false);
        await host.InitializeAsync();
        Assert.NotEmpty(host.GetLoadedPlugins());
        Directory.Move(Path.Combine(root.Path, "one"), Path.Combine(outside.Path, "moved"));
        await Eventually(() => !host.GetLoadedPlugins().Any());
    }

    private static async Task Eventually(Func<bool> condition, TimeSpan? maximumWait = null)
    {
        using var timeout = new CancellationTokenSource(maximumWait ?? TimeSpan.FromSeconds(10));
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

    [Fact] public void GameWindowReadinessHasABoundInsteadOfPollingForever()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var game = LongRunningGame();
        var error = Assert.Throws<TimeoutException>(() =>
            NativeProcessLauncher.EnsureGameWindowReady(game, TimeSpan.FromMilliseconds(250)));
        Assert.Contains("window", error.Message.ToLowerInvariant());
    }

    [Fact] public void GameWindowReadinessObservesLaunchCancellation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var game = LongRunningGame();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        using (NativeProcessLauncher.UseCancellationToken(cancellation.Token))
        {
            Assert.ThrowsAny<OperationCanceledException>(() =>
                NativeProcessLauncher.EnsureGameWindowReady(game, TimeSpan.FromSeconds(5)));
        }
    }

    [Fact] public void GameWindowReadinessDetectsAnEarlyProcessExit()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var game = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false })!;
        Assert.True(game.WaitForExit(5000));
        Assert.Throws<CoreLibLaunchSupport.GameExitedException>(() =>
            NativeProcessLauncher.EnsureGameWindowReady(game, TimeSpan.FromSeconds(1)));
    }

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
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
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
        var cancellationCallbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var worker = new WorkerAddon(token =>
        {
            // Keep the registration alive until cancellation actually begins. CancelAsync invokes
            // callbacks asynchronously, so releasing the worker immediately would race disposal
            // of this registration and make the expected callback failure nondeterministic.
            using var registration = token.Register(() =>
            {
                cancellationCallbackEntered.TrySetResult();
                throw new IOException("synthetic cancellation failure");
            });
            entered.SetResult(); release.Wait(TimeSpan.FromSeconds(10));
        });
        var other = new ControlledAddon();
        try
        {
            var session = DalamudGameSession.Start(game, [worker, other]);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stop = session.StopAsync();
            Assert.False(stop.IsCompleted);
            await cancellationCallbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
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
