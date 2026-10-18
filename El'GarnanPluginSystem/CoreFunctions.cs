using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using El_Garnan_Plugin_Loader.Interfaces;
using WebLaunch.Core;

namespace El_Garnan_Plugin_Loader
{
    public class CoreFunctions : IDisposable, IAsyncDisposable
    {
        private sealed record Entry(string Path, string ShadowDirectory, IGamePlugin Plugin, PluginLoadContext Context)
        {
            private readonly object leaseLock = new();
            private int activeLeases;
            private TaskCompletionSource? drained;
            public void AcquireLease() { lock (leaseLock) activeLeases++; }
            public void ReleaseLease()
            {
                lock (leaseLock)
                    if (--activeLeases == 0) drained?.TrySetResult();
            }
            public Task WaitForLeasesAsync()
            {
                lock (leaseLock)
                    return activeLeases == 0 ? Task.CompletedTask
                        : (drained ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
        }
        private static readonly string ShadowRoot = Path.Combine(Path.GetTempPath(), "WebLaunch-plugins");
        private static readonly HashSet<string> LegacyDependencyDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "assets", "content", "fonts", "native", "resource", "resources", "runtimes",
            "shaders", "x64", "x86"
        };
        private readonly string managerShadowRoot = Path.Combine(ShadowRoot, Guid.NewGuid().ToString("N"));
        private FileStream? shadowRootLock;
        private readonly string pluginsPath;
        private readonly ILogger logger;
        private readonly bool hotReload;
        private readonly Func<IPluginRenderer> rendererFactory;
        private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim lifecycle = new(1, 1);
        private readonly object renderLock = new();
        private readonly object disposeGate = new();
        private Task? disposeTask;
        private readonly CancellationTokenSource lifetime = new();
        private readonly ConcurrentDictionary<string, byte> pendingChanges = new(StringComparer.OrdinalIgnoreCase);
        private FileSystemWatcher? watcher;
        private Task? watchTask;
        private IPluginRenderer? renderer;
        private Thread? renderThread;
        private TaskCompletionSource? renderStopped;
        private bool standalone, initialized, disposed;
        /// <summary>Raised after a plugin has completed validation and initialization and is registered for use.</summary>
        public event EventHandler<PluginLoadEventArgs>? PluginLoaded;
        /// <summary>Raised after a plugin has been removed from the active registry and its shutdown begins.</summary>
        public event EventHandler<PluginUnloadEventArgs>? PluginUnloaded;
        /// <summary>Raised after a file-watcher reload has replaced the plugin associated with the reported ID.</summary>
        public event EventHandler<PluginReloadEventArgs>? PluginReloaded;
        /// <summary>Raised when discovery, validation, initialization or reload fails for an individual plugin.</summary>
        public event EventHandler<PluginErrorEventArgs>? PluginError;
        /// <summary>Creates a plugin host using the built-in ImGui renderer.</summary>
        /// <param name="pluginsPath">Directory scanned for manifest-based and legacy plugin assemblies.</param>
        /// <param name="logger">Logger shared with plugin instances and renderer initialization.</param>
        /// <param name="enableHotReload">Whether filesystem changes trigger serialized unload/reload operations.</param>
        /// <param name="useStandaloneWindow">Whether the renderer starts with a standalone window.</param>
        public CoreFunctions(string pluginsPath, ILogger logger, bool enableHotReload = false, bool useStandaloneWindow = true)
            : this(pluginsPath, logger, enableHotReload, useStandaloneWindow, () => new ImGuiPluginRenderer(logger)) { }
        /// <summary>Creates a plugin host with an injected renderer factory for native or embedded rendering.</summary>
        /// <param name="pluginsPath">Directory scanned for plugin assemblies.</param>
        /// <param name="logger">Logger shared with plugin instances.</param>
        /// <param name="enableHotReload">Whether filesystem changes trigger reloads.</param>
        /// <param name="useStandaloneWindow">Whether standalone rendering is initially enabled.</param>
        /// <param name="rendererFactory">Factory invoked when a renderer is first required.</param>
        public CoreFunctions(string pluginsPath, ILogger logger, bool enableHotReload, bool useStandaloneWindow, Func<IPluginRenderer> rendererFactory)
        {
            this.pluginsPath = Path.GetFullPath(pluginsPath);
            this.logger = logger; hotReload = enableHotReload; standalone = useStandaloneWindow;
            this.rendererFactory = rendererFactory ?? throw new ArgumentNullException(nameof(rendererFactory));
        }
        /// <summary>Discovers plugins, retries dependencies deferred by directory order, and optionally starts hot reload.</summary>
        /// <remarks>Individual plugin failures are reported through <see cref="PluginError"/> and do not abort discovery of other candidates.</remarks>
        public async Task InitializeAsync()
        {
            await lifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (initialized) return;
                Directory.CreateDirectory(pluginsPath);
                Directory.CreateDirectory(ShadowRoot);
                // Lock before publishing the directory so concurrent cleanup cannot claim it.
                shadowRootLock ??= LockShadowRoot(managerShadowRoot);
                Directory.CreateDirectory(managerShadowRoot);
                try
                {
                    foreach (var directory in Directory.EnumerateDirectories(ShadowRoot))
                    {
                        try
                        {
                            using (LockShadowRoot(directory)) TryDeleteShadow(directory);
                            if (!Directory.Exists(directory)) File.Delete(directory + ".lock");
                        }
                        catch (IOException) { } // A live manager or another cleaner owns this root.
                        catch (UnauthorizedAccessException) { }
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                var candidates = DiscoverPlugins().ToList();
                // Dependencies may appear later in directory order. Retry deferred candidates once other plugins load.
                while (candidates.Count > 0)
                {
                    var before = entries.Count;
                    foreach (var path in candidates.ToArray())
                        if (await LoadAsync(path).ConfigureAwait(false)) candidates.Remove(path);
                    if (entries.Count == before) break;
                }
                initialized = true;
                if (hotReload)
                {
                    watcher = new FileSystemWatcher(pluginsPath) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName };
                    watcher.Changed += Changed; watcher.Created += Changed; watcher.Deleted += Changed;
                    watcher.Renamed += (_, e) => { pendingChanges[e.OldFullPath] = 0; pendingChanges[e.FullPath] = 0; };
                    watcher.EnableRaisingEvents = true;
                    watchTask = WatchAsync();
                }
            }
            finally { lifecycle.Release(); }
        }
        private IEnumerable<string> DiscoverPlugins()
        {
            var directories = new Stack<string>();
            directories.Push(pluginsPath);
            while (directories.TryPop(out var directory))
            {
                var affectedPath = directory;
                string[] assemblies;
                try
                {
                    SafePath.RejectLinks(directory);
                    var manifest = Path.Combine(directory, "plugin.json");
                    if (File.Exists(manifest))
                    {
                        affectedPath = manifest;
                        SafePath.RejectLinks(manifest);
                        using var json = JsonDocument.Parse(File.ReadAllText(manifest));
                        if (json.RootElement.GetProperty("version").GetInt32() != 1) throw new InvalidDataException("Unsupported plugin manifest version.");
                        var assembly = json.RootElement.GetProperty("assembly").GetString() ?? throw new InvalidDataException("Invalid plugin manifest.");
                        assemblies = [SafePath.Resolve(directory, assembly)];
                    }
                    else
                    {
                        assemblies = Directory.EnumerateFiles(directory, "*.dll").Order(StringComparer.Ordinal).ToArray();
                        foreach (var child in Directory.EnumerateDirectories(directory))
                            directories.Push(child); // Each directory is checked when popped, isolating link failures.
                    }
                }
                catch (Exception ex)
                {
                    PluginError?.Invoke(this, new PluginErrorEventArgs(affectedPath, ex));
                    continue;
                }
                foreach (var assembly in assemblies) yield return assembly;
            }
        }
        private async Task<bool> LoadAsync(string path)
        {
            if (!File.Exists(path) || entries.Values.Any(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase))) return true;
            PluginLoadContext? context = null;
            var candidates = new List<IGamePlugin>();
            string? shadow = null;
            try
            {
                SafePath.RejectLinks(path);
                // Shadow copies let Windows replace plugin files while an old context finishes unloading.
                shadow = Path.Combine(managerShadowRoot, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(shadow);
                var sourceDirectory = Path.GetDirectoryName(path)!;
                var manifest = Path.Combine(sourceDirectory, "plugin.json");
                if (File.Exists(manifest))
                {
                    foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
                    {
                        var relative = Path.GetRelativePath(sourceDirectory, file);
                        var source = SafePath.Resolve(sourceDirectory, relative);
                        var target = SafePath.Resolve(shadow, relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(source, target);
                    }
                }
                else
                {
                    CopyLegacyBundleDirectory(sourceDirectory, sourceDirectory, shadow, isRoot: true);
                }
                context = new PluginLoadContext(Path.Combine(shadow, Path.GetFileName(path)));
                var assembly = context.LoadFromAssemblyPath(Path.Combine(shadow, Path.GetFileName(path)));
                var types = assembly.GetTypes().Where(t => typeof(IGamePlugin).IsAssignableFrom(t) && !t.IsAbstract).ToArray();
                if (types.Length == 0) return true;
                string? manifestId = null;
                if (File.Exists(manifest))
                {
                    SafePath.RejectLinks(manifest);
                    using var json = JsonDocument.Parse(File.ReadAllText(manifest));
                    if (json.RootElement.GetProperty("version").GetInt32() != 1) throw new InvalidDataException("Unsupported plugin manifest version.");
                    manifestId = json.RootElement.GetProperty("id").GetString();
                    if (types.Length != 1) throw new InvalidDataException("Manifest bundles must identify one plugin.");
                }
                foreach (var type in types)
                {
                    var candidate = (IGamePlugin?)Activator.CreateInstance(type, logger) ?? throw new InvalidDataException("Invalid plugin constructor.");
                    candidates.Add(candidate);
                    if (string.IsNullOrWhiteSpace(candidate.PluginId) || entries.ContainsKey(candidate.PluginId) || candidates.Count(p => p.PluginId == candidate.PluginId) != 1)
                        throw new InvalidDataException("Duplicate or empty plugin ID.");
                    if (manifestId is not null && manifestId != candidate.PluginId) throw new InvalidDataException("Plugin ID differs from manifest.");
                }
                foreach (var candidate in candidates)
                {
                    foreach (var dependency in candidate.Dependencies)
                    {
                        var pluginDependency = candidates.FirstOrDefault(p => p.PluginId == dependency.Name)
                            ?? (entries.TryGetValue(dependency.Name, out var entry) ? entry.Plugin : null);
                        var version = pluginDependency?.Version ?? context.LoadFromAssemblyName(new AssemblyName(dependency.Name)).GetName().Version;
                        if (version < dependency.MinVersion) throw new PluginValidationException("Dependency version is too old.");
                    }
                    if (!await candidate.ValidateConfigurationAsync().ConfigureAwait(false)) throw new PluginValidationException("Plugin configuration is invalid.");
                    await candidate.InitializeAsync().ConfigureAwait(false);
                }
                var loaded = candidates.ToArray();
                lock (renderLock)
                    foreach (var candidate in loaded) entries[candidate.PluginId] = new(path, shadow, candidate, context);
                candidates.Clear(); context = null; shadow = null;
                foreach (var candidate in loaded) PluginLoaded?.Invoke(this, new PluginLoadEventArgs(candidate));
                return true;
            }
            catch (BadImageFormatException) { return true; } // Native dependencies in legacy bundles.
            catch (Exception ex)
            {
                logger.Warning($"Plugin could not load ({ex.GetType().Name}).");
                PluginError?.Invoke(this, new PluginErrorEventArgs(path, ex));
                return false;
            }
            finally
            {
                foreach (var candidate in candidates)
                {
                    try { await candidate.ShutdownAsync().ConfigureAwait(false); } catch { }
                    try { if (candidate is IDisposable d) d.Dispose(); } catch { }
                }
                context?.Unload();
                if (shadow is not null) ScheduleShadowCleanup(shadow);
            }
        }
        private static void CopyLegacyBundleDirectory(
            string directory, string bundleRoot, string shadowRoot, bool isRoot = false, bool privateTree = false)
        {
            SafePath.RejectLinks(directory);
            var keepPrivateTree = isRoot || privateTree || LegacyDependencyDirectoryNames.Contains(Path.GetFileName(directory));
            if (!isRoot && !keepPrivateTree)
            {
                var hasManifest = File.Exists(Path.Combine(directory, "plugin.json"));
                var hasAssemblies = Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly).Any();
                if (hasManifest || hasAssemblies)
                    return; // A separately discoverable plugin must retain its own shadow-copy boundary.
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                SafePath.RejectLinks(file);
                var relative = Path.GetRelativePath(bundleRoot, file);
                var source = SafePath.Resolve(bundleRoot, relative);
                var target = SafePath.Resolve(shadowRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, overwrite: true);
            }

            foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
            {
                SafePath.RejectLinks(child);
                CopyLegacyBundleDirectory(child, bundleRoot, shadowRoot, privateTree: privateTree || LegacyDependencyDirectoryNames.Contains(Path.GetFileName(child)));
            }
        }

        private static void ScheduleShadowCleanup(string path)
        {
            // Keep the task independent of plugin/context objects so collectible unload can finish.
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
                await TryDeleteShadowTreeAsync(path, 20).ConfigureAwait(false);
            });
        }

        private static void ScheduleManagerShadowCleanup(string path)
        {
            _ = Task.Run(async () =>
            {
                for (var attempt = 0; attempt < 20; attempt++)
                {
                    try
                    {
                        using (LockShadowRoot(path))
                            await TryDeleteShadowTreeAsync(path, 3).ConfigureAwait(false);
                        if (!Directory.Exists(path))
                        {
                            try { File.Delete(path + ".lock"); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                            return;
                        }
                    }
                    catch (IOException) { } // A live manager owns the lock.
                    catch (UnauthorizedAccessException) { }
                    await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
                }
            });
        }

        private static async Task TryDeleteShadowTreeAsync(string path, int attempts)
        {
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                try
                {
                    if (!Directory.Exists(path)) return;
                    Directory.Delete(path, recursive: true);
                    return;
                }
                catch (IOException)
                {
                    if (attempt + 1 < attempts) await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException)
                {
                    if (attempt + 1 < attempts) await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
                }
            }
        }

        private void Changed(object sender, FileSystemEventArgs e) => pendingChanges[e.FullPath] = 0;
        private async Task WatchAsync()
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(400));
                while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
                {
                    if (pendingChanges.IsEmpty) continue;
                    var changed = pendingChanges.Keys.ToArray();
                    foreach (var path in changed) pendingChanges.TryRemove(path, out _);
                    await lifecycle.WaitAsync(lifetime.Token).ConfigureAwait(false);
                    try
                    {
                        foreach (var group in entries.Values.ToArray().GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
                        {
                            if (!changed.Any(p => p.StartsWith(Path.GetDirectoryName(group.Key)! + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) continue;
                            if (group.Any(e => !e.Plugin.SupportsHotReload)) continue;
                            var ids = group.Select(e => e.Plugin.PluginId).ToArray();
                            foreach (var id in ids) await UnloadAsync(id).ConfigureAwait(false);
                            if (await LoadAsync(group.Key).ConfigureAwait(false))
                                foreach (var id in ids.Where(entries.ContainsKey)) PluginReloaded?.Invoke(this, new PluginReloadEventArgs(id));
                        }
                        foreach (var path in DiscoverPlugins()) await LoadAsync(path).ConfigureAwait(false);
                    }
                    catch (Exception ex) { logger.Warning($"Plugin reload failed ({ex.GetType().Name})."); }
                    finally { lifecycle.Release(); }
                }
            }
            catch (OperationCanceledException) { }
        }
        private async Task UnloadAsync(string id)
        {
            Entry? entry;
            lock (renderLock) entries.TryRemove(id, out entry);
            if (entry is null) return;
            await entry.WaitForLeasesAsync().ConfigureAwait(false);
            try { await entry.Plugin.ShutdownAsync().ConfigureAwait(false); }
            catch (Exception ex) { logger.Warning($"Plugin shutdown failed ({ex.GetType().Name})."); }
            finally
            {
                try { if (entry.Plugin is IDisposable d) d.Dispose(); }
                catch (Exception ex) { logger.Warning($"Plugin disposal failed ({ex.GetType().Name})."); }
                finally
                {
                    if (!entries.Values.Any(e => ReferenceEquals(e.Context, entry.Context)))
                    { entry.Context.Unload(); ScheduleShadowCleanup(entry.ShadowDirectory); }
                }
                PluginUnloaded?.Invoke(this, new PluginUnloadEventArgs(id));
            }
        }
        private static FileStream LockShadowRoot(string path) =>
            new(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        private static void TryDeleteShadow(string path)
        {
            try { Directory.Delete(path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        /// <summary>Returns the active plugin for an exact plugin ID.</summary>
        /// <param name="pluginId">Stable ID declared by the plugin.</param>
        /// <exception cref="KeyNotFoundException">No active plugin has the requested ID.</exception>
        public IGamePlugin GetPlugin(string pluginId) => entries.TryGetValue(pluginId, out var entry) ? entry.Plugin : throw new KeyNotFoundException("Requested game plugin is not installed.");
        /// <summary>Returns a snapshot of currently registered plugin instances.</summary>
        public IEnumerable<IGamePlugin> GetLoadedPlugins() => entries.Values.Select(e => e.Plugin).ToArray();
        /// <summary>Runs an asynchronous action while holding a lease that prevents the selected plugin from unloading.</summary>
        /// <typeparam name="T">Type returned by the plugin action.</typeparam>
        /// <param name="id">Stable ID of the active plugin.</param>
        /// <param name="action">Operation to invoke while the plugin lease is active.</param>
        /// <param name="token">Caller cancellation token.</param>
        public Task<T> UsePluginAsync<T>(string id, Func<IGamePlugin, Task<T>> action, CancellationToken token = default) =>
            UsePluginAsync(id, (plugin, _) => action(plugin), token);
        /// <summary>Runs a cancellable plugin operation under a lease linked to caller and host shutdown.</summary>
        /// <typeparam name="T">Type returned by the plugin operation.</typeparam>
        /// <param name="id">Stable ID of the active plugin.</param>
        /// <param name="action">Operation receiving the plugin and linked cancellation token.</param>
        /// <param name="token">Caller cancellation token.</param>
        /// <exception cref="KeyNotFoundException">The plugin is not active when the lease is requested.</exception>
        public async Task<T> UsePluginAsync<T>(string id, Func<IGamePlugin, CancellationToken, Task<T>> action, CancellationToken token = default)
        {
            Entry entry;
            CancellationTokenSource cancellation;
            await lifecycle.WaitAsync(token).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                entry = entries.TryGetValue(id, out var found) ? found : throw new KeyNotFoundException("Requested game plugin is not installed.");
                cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
                entry.AcquireLease();
            }
            finally { lifecycle.Release(); }
            using (cancellation)
            {
                try
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    return await action(entry.Plugin, cancellation.Token).ConfigureAwait(false);
                }
                finally { entry.ReleaseLease(); }
            }
        }
        /// <summary>Stops new leases, waits for existing actions, shuts down active plugins, and releases their load contexts.</summary>
        public async Task UnloadAllPluginsAsync()
        {
            await lifecycle.WaitAsync().ConfigureAwait(false);
            try { foreach (var id in entries.Keys.ToArray()) await UnloadAsync(id).ConfigureAwait(false); }
            finally { lifecycle.Release(); }
        }
        /// <summary>Requests rendering of active plugin interfaces on the renderer's owned thread.</summary>
        public void RenderPluginInterfaces()
        {
            lock (renderLock)
                foreach (var plugin in GetLoadedPlugins().Where(p => p.SupportsImGui)) plugin.RenderImGui();
        }
        /// <summary>Enables the renderer's standalone-window mode for the next render lifecycle.</summary>
        public void EnableStandaloneRenderer() => standalone = true;
        /// <summary>Starts the renderer thread once; repeated calls do not create duplicate render workers.</summary>
        public void StartRendering()
        {
            lock (renderLock)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (!standalone || renderThread is { IsAlive: true }) return;
                renderStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
                renderThread = new Thread(() =>
                {
                    try
                    {
                        renderer = rendererFactory();
                        renderer.Initialize();
                        while (!lifetime.IsCancellationRequested && renderer.IsInitialized)
                        {
                            lock (renderLock) { renderer.SetPlugins(GetLoadedPlugins()); renderer.Render(); }
                            Thread.Sleep(16);
                        }
                    }
                    catch (Exception ex) { logger.Warning($"Renderer stopped ({ex.GetType().Name})."); }
                    finally
                    {
                        try { renderer?.Dispose(); }
                        catch (Exception ex) { logger.Warning($"Renderer disposal failed ({ex.GetType().Name})."); }
                        finally { renderer = null; renderStopped.TrySetResult(); }
                    }
                }) { IsBackground = true, Name = "WebLaunch renderer" };
                renderThread.Start();
            }
        }
        /// <summary>Synchronously disposes the host while keeping asynchronous lifecycle continuations context-independent.</summary>
        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

        /// <summary>Begins or joins the single shared asynchronous shutdown operation.</summary>
        /// <remarks>Concurrent callers await the same task so plugin callbacks and file cleanup are not duplicated.</remarks>
        public ValueTask DisposeAsync()
        {
            lock (disposeGate)
                return new ValueTask(disposeTask ??= DisposeCoreAsync());
        }

        private async Task DisposeCoreAsync()
        {
            if (disposed) return;
            disposed = true;
            watcher?.Dispose();
            await lifetime.CancelAsync().ConfigureAwait(false);
            if (watchTask is not null) await watchTask.ConfigureAwait(false);
            if (renderStopped is not null) await renderStopped.Task.ConfigureAwait(false);
            try { await UnloadAllPluginsAsync().ConfigureAwait(false); }
            finally
            {
                await TryDeleteShadowTreeAsync(managerShadowRoot, 20).ConfigureAwait(false);
                shadowRootLock?.Dispose();
                shadowRootLock = null;
                try
                {
                    if (!Directory.Exists(managerShadowRoot)) File.Delete(managerShadowRoot + ".lock");
                    else ScheduleManagerShadowCleanup(managerShadowRoot);
                }
                catch (IOException) { ScheduleManagerShadowCleanup(managerShadowRoot); }
                catch (UnauthorizedAccessException) { ScheduleManagerShadowCleanup(managerShadowRoot); }
                lifetime.Dispose();
            }
        }
    }
    public class PluginValidationException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="PluginValidationException"/> class.
        /// </summary>
        /// <param name="message">The exception message.</param>
        public PluginValidationException(string message) : base(message) { }
    }

    /// <summary>
    /// Event arguments for plugin error events.
    /// </summary>
    public class PluginErrorEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the path to the plugin that encountered an error.
        /// </summary>
        public string PluginPath { get; }

        /// <summary>
        /// Gets the exception that occurred.
        /// </summary>
        public Exception Error { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="PluginErrorEventArgs"/> class.
        /// </summary>
        /// <param name="pluginPath">The path to the plugin that encountered an error.</param>
        /// <param name="error">The exception that occurred.</param>
        public PluginErrorEventArgs(string pluginPath, Exception error)
        {
            PluginPath = pluginPath;
            Error = error;
        }
    }

    /// <summary>
    /// Event arguments for plugin load events.
    /// </summary>
    public class PluginLoadEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the loaded plugin.
        /// </summary>
        public IGamePlugin Plugin { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="PluginLoadEventArgs"/> class.
        /// </summary>
        /// <param name="plugin">The loaded plugin.</param>
        public PluginLoadEventArgs(IGamePlugin plugin) => Plugin = plugin;
    }

    /// <summary>
    /// Event arguments for plugin unload events.
    /// </summary>
    public class PluginUnloadEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the ID of the unloaded plugin.
        /// </summary>
        public string PluginId { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="PluginUnloadEventArgs"/> class.
        /// </summary>
        /// <param name="pluginId">The ID of the unloaded plugin.</param>
        public PluginUnloadEventArgs(string pluginId) => PluginId = pluginId;
    }

    /// <summary>
    /// Event arguments for plugin reload events.
    /// </summary>
    public class PluginReloadEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the ID of the reloaded plugin.
        /// </summary>
        public string PluginId { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="PluginReloadEventArgs"/> class.
        /// </summary>
        /// <param name="pluginId">The ID of the reloaded plugin.</param>
        public PluginReloadEventArgs(string pluginId) => PluginId = pluginId;
    }
}
