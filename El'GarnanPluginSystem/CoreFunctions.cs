using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using El_Garnan_Plugin_Loader.Interfaces;
using WebLaunch.Core;

namespace El_Garnan_Plugin_Loader
{
    public class CoreFunctions : IDisposable, IAsyncDisposable
    {
        private sealed record Entry(string Path, string ShadowDirectory, IGamePlugin Plugin, PluginLoadContext Context);
        private readonly string pluginsPath;
        private readonly ILogger logger;
        private readonly bool hotReload;
        private readonly Func<IPluginRenderer> rendererFactory;
        private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim lifecycle = new(1, 1);
        private readonly object renderLock = new();
        private readonly CancellationTokenSource lifetime = new();
        private readonly ConcurrentDictionary<string, byte> pendingChanges = new(StringComparer.OrdinalIgnoreCase);
        private FileSystemWatcher? watcher;
        private Task? watchTask;
        private IPluginRenderer? renderer;
        private Thread? renderThread;
        private TaskCompletionSource? renderStopped;
        private bool standalone, initialized, disposed;
        public event EventHandler<PluginLoadEventArgs>? PluginLoaded;
        public event EventHandler<PluginUnloadEventArgs>? PluginUnloaded;
        public event EventHandler<PluginReloadEventArgs>? PluginReloaded;
        public event EventHandler<PluginErrorEventArgs>? PluginError;
        public CoreFunctions(string pluginsPath, ILogger logger, bool enableHotReload = false, bool useStandaloneWindow = true)
            : this(pluginsPath, logger, enableHotReload, useStandaloneWindow, () => new ImGuiPluginRenderer(logger)) { }
        public CoreFunctions(string pluginsPath, ILogger logger, bool enableHotReload, bool useStandaloneWindow, Func<IPluginRenderer> rendererFactory)
        {
            this.pluginsPath = Path.GetFullPath(pluginsPath);
            this.logger = logger; hotReload = enableHotReload; standalone = useStandaloneWindow;
            this.rendererFactory = rendererFactory ?? throw new ArgumentNullException(nameof(rendererFactory));
        }
        public async Task InitializeAsync()
        {
            await lifecycle.WaitAsync();
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (initialized) return;
                Directory.CreateDirectory(pluginsPath);
                var candidates = DiscoverPlugins().ToList();
                // Dependencies may appear later in directory order. Retry deferred candidates once other plugins load.
                while (candidates.Count > 0)
                {
                    var before = entries.Count;
                    foreach (var path in candidates.ToArray())
                        if (await LoadAsync(path)) candidates.Remove(path);
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
                shadow = Path.Combine(Path.GetTempPath(), "WebLaunch-plugins", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(shadow);
                var sourceDirectory = Path.GetDirectoryName(path)!;
                foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(sourceDirectory, file);
                    var source = SafePath.Resolve(sourceDirectory, relative);
                    var target = SafePath.Resolve(shadow, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(source, target);
                }
                context = new PluginLoadContext(Path.Combine(shadow, Path.GetFileName(path)));
                var assembly = context.LoadFromAssemblyPath(Path.Combine(shadow, Path.GetFileName(path)));
                var types = assembly.GetTypes().Where(t => typeof(IGamePlugin).IsAssignableFrom(t) && !t.IsAbstract).ToArray();
                if (types.Length == 0) return true;
                var manifest = Path.Combine(sourceDirectory, "plugin.json");
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
                    if (!await candidate.ValidateConfigurationAsync()) throw new PluginValidationException("Plugin configuration is invalid.");
                    await candidate.InitializeAsync();
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
                    try { await candidate.ShutdownAsync(); } catch { }
                    try { if (candidate is IDisposable d) d.Dispose(); } catch { }
                }
                context?.Unload();
                if (shadow is not null) TryDeleteShadow(shadow);
            }
        }
        private void Changed(object sender, FileSystemEventArgs e) => pendingChanges[e.FullPath] = 0;
        private async Task WatchAsync()
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(400));
                while (await timer.WaitForNextTickAsync(lifetime.Token))
                {
                    if (pendingChanges.IsEmpty) continue;
                    var changed = pendingChanges.Keys.ToArray();
                    foreach (var path in changed) pendingChanges.TryRemove(path, out _);
                    await lifecycle.WaitAsync(lifetime.Token);
                    try
                    {
                        foreach (var group in entries.Values.ToArray().GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
                        {
                            if (!changed.Any(p => p.StartsWith(Path.GetDirectoryName(group.Key)! + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) continue;
                            if (group.Any(e => !e.Plugin.SupportsHotReload)) continue;
                            var ids = group.Select(e => e.Plugin.PluginId).ToArray();
                            foreach (var id in ids) await UnloadAsync(id);
                            if (await LoadAsync(group.Key))
                                foreach (var id in ids.Where(entries.ContainsKey)) PluginReloaded?.Invoke(this, new PluginReloadEventArgs(id));
                        }
                        foreach (var path in DiscoverPlugins()) await LoadAsync(path);
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
            try { await entry.Plugin.ShutdownAsync(); }
            catch (Exception ex) { logger.Warning($"Plugin shutdown failed ({ex.GetType().Name})."); }
            finally
            {
                try { if (entry.Plugin is IDisposable d) d.Dispose(); }
                catch (Exception ex) { logger.Warning($"Plugin disposal failed ({ex.GetType().Name})."); }
                finally
                {
                    if (!entries.Values.Any(e => ReferenceEquals(e.Context, entry.Context)))
                    { entry.Context.Unload(); TryDeleteShadow(entry.ShadowDirectory); }
                }
                PluginUnloaded?.Invoke(this, new PluginUnloadEventArgs(id));
            }
        }
        private static void TryDeleteShadow(string path)
        {
            try { Directory.Delete(path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        public IGamePlugin GetPlugin(string pluginId) => entries.TryGetValue(pluginId, out var entry) ? entry.Plugin : throw new KeyNotFoundException("Requested game plugin is not installed.");
        public IEnumerable<IGamePlugin> GetLoadedPlugins() => entries.Values.Select(e => e.Plugin).ToArray();
        public async Task<T> UsePluginAsync<T>(string id, Func<IGamePlugin, Task<T>> action, CancellationToken token = default)
        {
            await lifecycle.WaitAsync(token);
            try { ObjectDisposedException.ThrowIf(disposed, this); return await action(GetPlugin(id)); }
            finally { lifecycle.Release(); }
        }
        public async Task UnloadAllPluginsAsync()
        {
            await lifecycle.WaitAsync();
            try { foreach (var id in entries.Keys.ToArray()) await UnloadAsync(id); }
            finally { lifecycle.Release(); }
        }
        public void RenderPluginInterfaces()
        {
            lock (renderLock)
                foreach (var plugin in GetLoadedPlugins().Where(p => p.SupportsImGui)) plugin.RenderImGui();
        }
        public void EnableStandaloneRenderer() => standalone = true;
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
        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true; watcher?.Dispose(); await lifetime.CancelAsync();
            if (watchTask is not null) await watchTask;
            if (renderStopped is not null) await renderStopped.Task;
            await UnloadAllPluginsAsync();
            lifetime.Dispose();
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
