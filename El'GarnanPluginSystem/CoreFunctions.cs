using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using El_Garnan_Plugin_Loader.Interfaces;
using WebLaunch.Core;

namespace El_Garnan_Plugin_Loader
{
    public class CoreFunctions : IDisposable, IAsyncDisposable
    {
        private sealed record PluginCandidate(string AssemblyPath, string BundleRoot, string? ManifestPath = null);
        private sealed record Entry(PluginCandidate Candidate, string ShadowDirectory, IGamePlugin Plugin, PluginLoadContext Context)
        {
            private readonly object leaseLock = new();
            private int activeLeases;
            private TaskCompletionSource? drained;
            public bool HasActiveLeases { get { lock (leaseLock) return activeLeases > 0; } }
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
            // Conventional private dependency/runtime/resource locations used by older bundles.
            // This is intentionally a bounded policy, not a recursive copy of every discovered plugin.
            "assets", "content", "dependencies", "dependency", "fonts", "lib", "libs", "managed",
            "native", "resource", "resources", "runtimes", "shaders", "x64", "x86"
        };
        private readonly string managerShadowRoot = Path.Combine(ShadowRoot, Guid.NewGuid().ToString("N"));
        private FileStream? shadowRootLock;
        private readonly string pluginsPath;
        private readonly ILogger logger;
        private readonly bool hotReload;
        private readonly Func<IPluginRenderer> rendererFactory;
        private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> renderFailures = new(StringComparer.Ordinal);
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
        private bool standalone, initialized;
        private volatile bool disposed;
        private readonly PluginDiagnostics diagnostics;

        /// <summary>Raised after a plugin has completed validation and initialization and is registered for use.</summary>
        public event EventHandler<PluginLoadEventArgs>? PluginLoaded;
        /// <summary>Raised after a plugin has been removed from the active registry and its shutdown begins.</summary>
        public event EventHandler<PluginUnloadEventArgs>? PluginUnloaded;
        /// <summary>Raised after a file-watcher reload has replaced the plugin associated with the reported ID.</summary>
        public event EventHandler<PluginReloadEventArgs>? PluginReloaded;
        /// <summary>Raised when discovery, validation, initialization or reload fails for an individual plugin.</summary>
        public event EventHandler<PluginErrorEventArgs>? PluginError;

        private void TryLogWarning(string message)
        {
            try { logger.Warning(message); }
            catch (Exception ex)
            {
                // Logger sinks are external integrations; a broken sink must not abort plugin loading or shutdown.
                diagnostics.Record(PluginDiagnosticLevel.Warning, "logger", "host", "warning-write-failed", ex);
            }
        }

        private void Notify<TEventArgs>(EventHandler<TEventArgs>? observers, TEventArgs args, string eventName)
            where TEventArgs : EventArgs
        {
            if (observers is null) return;
            foreach (EventHandler<TEventArgs> observer in observers.GetInvocationList())
            {
                try { observer(this, args); }
                catch (Exception ex)
                {
                    // Observers are diagnostics/UI integrations, not part of plugin ownership.
                    // One failing subscriber must not abort discovery, roll back registered plugins,
                    // or prevent remaining unload/cleanup notifications from running.
                    diagnostics.Record(PluginDiagnosticLevel.Warning, "event-observer", "host", $"failed-{eventName}", ex);
                    try { TryLogWarning($"Plugin lifecycle observer failed for {eventName} ({ex.GetType().Name})."); }
                    catch { /* Logging must not turn an observer failure into a host failure. */ }
                }
            }
        }
        /// <summary>Creates a plugin host using the built-in ImGui renderer.</summary>
        /// <param name="pluginsPath">Directory scanned for manifest-based and legacy plugin assemblies.</param>
        /// <param name="logger">Logger shared with plugin instances and renderer initialization.</param>
        /// <param name="enableHotReload">Whether filesystem changes trigger serialized unload/reload operations.</param>
        /// <param name="useStandaloneWindow">Whether the renderer starts with a standalone window.</param>
        public CoreFunctions(string pluginsPath, ILogger logger, bool enableHotReload = false, bool useStandaloneWindow = true)
            : this(pluginsPath, logger, enableHotReload, useStandaloneWindow,
                () => new ImGuiPluginRenderer(logger), null, false) { }

        /// <summary>Creates a plugin host with explicit diagnostics and the built-in renderer.</summary>
        public CoreFunctions(
            string pluginsPath,
            ILogger logger,
            bool enableHotReload,
            bool useStandaloneWindow,
            string? diagnosticsDirectory,
            bool debugDiagnostics)
            : this(pluginsPath, logger, enableHotReload, useStandaloneWindow,
                () => new ImGuiPluginRenderer(logger), diagnosticsDirectory, debugDiagnostics) { }

        /// <summary>Creates a plugin host with an injected renderer factory for native or embedded rendering.</summary>
        /// <param name="pluginsPath">Directory scanned for plugin assemblies.</param>
        /// <param name="logger">Logger shared with plugin instances.</param>
        /// <param name="enableHotReload">Whether filesystem changes trigger reloads.</param>
        /// <param name="useStandaloneWindow">Whether standalone rendering is initially enabled.</param>
        /// <param name="rendererFactory">Factory invoked when a renderer is first required.</param>
        public CoreFunctions(string pluginsPath, ILogger logger, bool enableHotReload, bool useStandaloneWindow, Func<IPluginRenderer> rendererFactory)
            : this(pluginsPath, logger, enableHotReload, useStandaloneWindow, rendererFactory, null, false) { }

        /// <summary>Creates a plugin host with explicit diagnostics and renderer settings.</summary>
        public CoreFunctions(
            string pluginsPath,
            ILogger logger,
            bool enableHotReload,
            bool useStandaloneWindow,
            Func<IPluginRenderer> rendererFactory,
            string? diagnosticsDirectory,
            bool debugDiagnostics)
        {
            this.pluginsPath = Path.GetFullPath(pluginsPath);
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            hotReload = enableHotReload;
            standalone = useStandaloneWindow;
            this.rendererFactory = rendererFactory ?? throw new ArgumentNullException(nameof(rendererFactory));
            diagnostics = new PluginDiagnostics(diagnosticsDirectory, debugDiagnostics);
            diagnostics.Record(PluginDiagnosticLevel.Information, "manager", "host", "created");
        }

        /// <summary>Returns recent safe plugin-loader diagnostics from this host instance.</summary>
        public IReadOnlyList<PluginDiagnosticEntry> GetDiagnosticSnapshot(int limit = 100) => diagnostics.GetRecent(limit);

        /// <summary>Gets the local rolling plugin diagnostic log path.</summary>
        public string DiagnosticLogPath => diagnostics.LogPath;
        /// <summary>Discovers plugins, retries dependencies deferred by directory order, and optionally starts hot reload.</summary>
        /// <remarks>Individual plugin failures are reported through <see cref="PluginError"/> and do not abort discovery of other candidates.</remarks>
        public async Task InitializeAsync()
        {
            await lifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (initialized)
                {
                    diagnostics.Record(PluginDiagnosticLevel.Trace, "initialize", "host", "already-initialized");
                    return;
                }

                diagnostics.Record(PluginDiagnosticLevel.Information, "initialize", "host", "started");
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
                diagnostics.Record(PluginDiagnosticLevel.Information, "discovery", "host", $"candidate-count-{candidates.Count}");
                if (candidates.Count == 0)
                    diagnostics.Record(PluginDiagnosticLevel.Warning, "discovery", "host", "no-plugin-candidates");

                // Dependencies may appear later in directory order. Retry deferred candidates once other plugins load.
                while (candidates.Count > 0)
                {
                    var before = entries.Count;
                    foreach (var candidate in candidates.ToArray())
                        if (await LoadAsync(candidate).ConfigureAwait(false)) candidates.Remove(candidate);
                    if (entries.Count == before) break;
                }

                if (candidates.Count > 0)
                    diagnostics.Record(PluginDiagnosticLevel.Warning, "resolution", "host", $"unresolved-candidates-{candidates.Count}");
                if (entries.IsEmpty && candidates.Count > 0)
                    diagnostics.Record(PluginDiagnosticLevel.Warning, "initialize", "host", "no-plugins-loaded");

                initialized = true;
                if (hotReload)
                {
                    watcher = new FileSystemWatcher(pluginsPath)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName
                    };
                    watcher.Changed += Changed;
                    watcher.Created += Changed;
                    watcher.Deleted += Changed;
                    watcher.Renamed += (_, e) =>
                    {
                        pendingChanges[e.OldFullPath] = 0;
                        pendingChanges[e.FullPath] = 0;
                    };
                    watcher.EnableRaisingEvents = true;
                    watchTask = WatchAsync();
                    diagnostics.Record(PluginDiagnosticLevel.Information, "hot-reload", "host", "watcher-started");
                }

                diagnostics.Record(PluginDiagnosticLevel.Information, "initialize", "host", $"loaded-count-{entries.Count}");
            }
            catch (Exception ex)
            {
                diagnostics.Record(PluginDiagnosticLevel.Error, "initialize", "host", "failed", ex);
                throw;
            }
            finally { lifecycle.Release(); }
        }
        private static JsonElement RequireManifestProperty(JsonElement root, string name)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var property))
                throw new InvalidDataException($"Plugin manifest is missing required field '{name}'.");
            return property;
        }

        private IEnumerable<PluginCandidate> DiscoverPlugins()
        {
            var directories = new Stack<string>();
            directories.Push(pluginsPath);
            while (directories.TryPop(out var directory))
            {
                var affectedPath = directory;
                PluginCandidate[] assemblies;
                try
                {
                    SafePath.RejectLinks(directory);
                    var manifest = Path.Combine(directory, "plugin.json");
                    if (File.Exists(manifest))
                    {
                        affectedPath = manifest;
                        assemblies = [ReadManifestCandidate(directory, manifest)];
                    }
                    else
                    {
                        assemblies = Directory.EnumerateFiles(directory, "*.dll").Order(StringComparer.Ordinal)
                            .Select(path => new PluginCandidate(path, directory)).ToArray();
                        foreach (var child in Directory.EnumerateDirectories(directory))
                            directories.Push(child); // Each directory is checked when popped, isolating link failures.
                    }
                }
                catch (Exception ex)
                {
                    var failedPluginId = affectedPath.EndsWith("plugin.json", StringComparison.OrdinalIgnoreCase)
                        ? GetDiagnosticPluginId(affectedPath)
                        : "legacy";
                    diagnostics.Record(PluginDiagnosticLevel.Error, "discovery", failedPluginId, "candidate-rejected", ex);
                    Notify(PluginError, new PluginErrorEventArgs(affectedPath, ex), nameof(PluginError));
                    continue;
                }
                foreach (var assembly in assemblies)
                {
                    diagnostics.Record(
                        PluginDiagnosticLevel.Trace,
                        "discovery",
                        GetDiagnosticPluginId(assembly.ManifestPath),
                        "candidate-found");
                    yield return assembly;
                }
            }
        }
        private static PluginCandidate ReadManifestCandidate(string directory, string manifest)
        {
            SafePath.RejectLinks(manifest);
            using var json = JsonDocument.Parse(File.ReadAllText(manifest));
            if (RequireManifestProperty(json.RootElement, "version").GetInt32() != 1) throw new InvalidDataException("Unsupported plugin manifest version.");
            var assembly = RequireManifestProperty(json.RootElement, "assembly").GetString() ?? throw new InvalidDataException("Invalid plugin manifest.");
            // Keep the bundle root distinct from the entry assembly path. A manifest
            // may place its entry assembly in a nested managed directory; the complete
            // bundle is shadow-copied with relative paths preserved and SafePath ensures
            // the manifest path cannot escape that bundle.
            return new PluginCandidate(SafePath.Resolve(directory, assembly), directory, manifest);
        }

        private PluginCandidate? Rediscover(PluginCandidate previous)
        {
            if (previous.ManifestPath is null || !File.Exists(previous.ManifestPath)) return previous;
            try { return ReadManifestCandidate(previous.BundleRoot, previous.ManifestPath); }
            catch (Exception ex)
            {
                diagnostics.Record(PluginDiagnosticLevel.Error, "discovery", GetDiagnosticPluginId(previous.ManifestPath), "candidate-rejected", ex);
                Notify(PluginError, new PluginErrorEventArgs(previous.ManifestPath, ex), nameof(PluginError));
                return null;
            }
        }

        private async Task<bool> LoadAsync(PluginCandidate discovery)
        {
            var path = discovery.AssemblyPath;
            var pluginId = GetDiagnosticPluginId(discovery.ManifestPath);
            var stage = "load";
            var correlationId = Guid.NewGuid().ToString("N");
            void Record(
                PluginDiagnosticLevel level,
                string recordStage,
                string? recordPluginId,
                string outcome,
                Exception? exception = null) =>
                diagnostics.Record(level, recordStage, recordPluginId, outcome, exception, correlationId);
            if (entries.Values.Any(e => string.Equals(e.Candidate.AssemblyPath, path, StringComparison.OrdinalIgnoreCase)))
            {
                Record(PluginDiagnosticLevel.Trace, "load", pluginId, "already-registered");
                return true;
            }

            if (!File.Exists(path))
            {
                if (discovery.ManifestPath is null || !File.Exists(discovery.ManifestPath))
                {
                    Record(PluginDiagnosticLevel.Trace, "discovery", pluginId, "candidate-disappeared");
                    return true;
                }

                var error = new FileNotFoundException("The entry assembly declared by plugin.json was not found.");
                Record(PluginDiagnosticLevel.Error, "manifest", pluginId, "entry-assembly-missing", error);
                TryLogWarning($"Plugin manifest entry assembly is missing ({error.GetType().Name}).");
                Notify(PluginError, new PluginErrorEventArgs(path, error), nameof(PluginError));
                return false;
            }

            PluginLoadContext? context = null;
            var candidates = new List<IGamePlugin>();
            string? shadow = null;
            try
            {
                Record(PluginDiagnosticLevel.Trace, stage, pluginId, "started");
                SafePath.RejectLinks(path);

                string? manifestId = null;
                if (discovery.ManifestPath is not null)
                {
                    stage = "manifest-validation";
                    SafePath.RejectLinks(discovery.ManifestPath);
                    using var json = JsonDocument.Parse(File.ReadAllText(discovery.ManifestPath));
                    if (RequireManifestProperty(json.RootElement, "version").GetInt32() != 1)
                        throw new InvalidDataException("Unsupported plugin manifest version.");
                    manifestId = RequireManifestProperty(json.RootElement, "id").GetString();
                    if (string.IsNullOrWhiteSpace(manifestId))
                        throw new InvalidDataException("Invalid plugin manifest ID.");
                    pluginId = manifestId;
                }

                // Shadow copies let Windows replace plugin files while an old context finishes unloading.
                stage = "shadow-copy";
                shadow = Path.Combine(managerShadowRoot, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(shadow);
                var sourceDirectory = discovery.BundleRoot;
                var manifest = discovery.ManifestPath;
                var relativeAssembly = Path.GetRelativePath(sourceDirectory, path);
                SafePath.Resolve(sourceDirectory, relativeAssembly);
                if (manifest is not null)
                    CopyManifestBundleDirectory(sourceDirectory, sourceDirectory, shadow);
                else
                    CopyLegacyBundleDirectory(sourceDirectory, sourceDirectory, shadow, isRoot: true);

                var shadowAssembly = SafePath.Resolve(shadow, relativeAssembly);
                stage = "assembly-load";
                context = new PluginLoadContext(shadowAssembly, diagnostics, pluginId, correlationId);
                var assembly = context.LoadFromAssemblyPath(shadowAssembly);

                stage = "type-discovery";
                var types = assembly.GetTypes().Where(t => typeof(IGamePlugin).IsAssignableFrom(t) && !t.IsAbstract).ToArray();
                if (manifest is not null && types.Length != 1)
                    throw new InvalidDataException("Manifest bundles must identify exactly one plugin.");
                if (types.Length == 0)
                {
                    Record(PluginDiagnosticLevel.Trace, stage, pluginId, "no-plugin-types");
                    return true;
                }

                stage = "plugin-construction";
                foreach (var type in types)
                {
                    var candidate = (IGamePlugin?)Activator.CreateInstance(type, logger)
                        ?? throw new InvalidDataException("Invalid plugin constructor.");
                    candidates.Add(candidate);
                    pluginId = candidate.PluginId;
                    if (string.IsNullOrWhiteSpace(candidate.PluginId) ||
                        entries.ContainsKey(candidate.PluginId) ||
                        candidates.Count(p => p.PluginId == candidate.PluginId) != 1)
                        throw new InvalidDataException("Duplicate or empty plugin ID.");
                    if (manifestId is not null && manifestId != candidate.PluginId)
                        throw new InvalidDataException("Plugin ID differs from manifest.");
                }

                foreach (var candidate in candidates)
                {
                    pluginId = candidate.PluginId;
                    stage = "dependency-resolution";
                    foreach (var dependency in candidate.Dependencies)
                    {
                        var pluginDependency = candidates.FirstOrDefault(p => p.PluginId == dependency.Name)
                            ?? (entries.TryGetValue(dependency.Name, out var entry) ? entry.Plugin : null);
                        var version = pluginDependency?.Version ??
                            context.LoadFromAssemblyName(new AssemblyName(dependency.Name)).GetName().Version;
                        if (version < dependency.MinVersion)
                            throw new PluginValidationException("Dependency version is too old.");
                    }

                    stage = "configuration-validation";
                    if (!await candidate.ValidateConfigurationAsync().ConfigureAwait(false))
                        throw new PluginValidationException("Plugin configuration is invalid.");

                    stage = "plugin-initialization";
                    Record(PluginDiagnosticLevel.Trace, stage, pluginId, "started");
                    await candidate.InitializeAsync().ConfigureAwait(false);
                    Record(PluginDiagnosticLevel.Trace, stage, pluginId, "succeeded");
                }

                stage = "registry-registration";
                var loaded = candidates.ToArray();
                lock (renderLock)
                    foreach (var candidate in loaded)
                        entries[candidate.PluginId] = new(discovery, shadow, candidate, context);
                candidates.Clear();
                context = null;
                shadow = null;

                foreach (var candidate in loaded)
                {
                    Record(PluginDiagnosticLevel.Information, "load", candidate.PluginId, "loaded");
                    Notify(PluginLoaded, new PluginLoadEventArgs(candidate), nameof(PluginLoaded));
                }
                return true;
            }
            catch (BadImageFormatException ex) when (discovery.ManifestPath is null && stage == "assembly-load")
            {
                // Native dependencies are expected during assembly loading in legacy bundles.
                // Only verbose tracing records these skipped candidates; failures in later stages are errors.
                Record(PluginDiagnosticLevel.Trace, "assembly-load", pluginId, "legacy-native-assembly-skipped", ex);
                return true;
            }
            catch (Exception ex)
            {
                Record(PluginDiagnosticLevel.Error, stage, pluginId, "failed", ex);
                TryLogWarning($"Plugin could not load during {stage} ({ex.GetType().Name}).");
                Notify(PluginError, new PluginErrorEventArgs(path, ex), nameof(PluginError));
                return false;
            }
            finally
            {
                foreach (var candidate in candidates)
                {
                    try { await candidate.ShutdownAsync().ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        Record(PluginDiagnosticLevel.Warning, "rollback-shutdown", candidate.PluginId, "failed", ex);
                    }

                    try { if (candidate is IDisposable d) d.Dispose(); }
                    catch (Exception ex)
                    {
                        Record(PluginDiagnosticLevel.Warning, "rollback-disposal", candidate.PluginId, "failed", ex);
                    }
                }

                try { context?.Unload(); }
                catch (Exception ex) { Record(PluginDiagnosticLevel.Warning, "context-unload", pluginId, "failed", ex); }
                if (shadow is not null) ScheduleShadowCleanup(shadow);
            }
        }

        private static string GetDiagnosticPluginId(string? manifestPath)
        {
            if (manifestPath is null) return "legacy";
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(manifestPath));
                if (json.RootElement.ValueKind == JsonValueKind.Object &&
                    json.RootElement.TryGetProperty("id", out var id) &&
                    id.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(id.GetString()))
                    return id.GetString()!;
            }
            catch { /* The discovery/load event records the structured failure type. */ }

            return "manifest";
        }
        private static void CopyManifestBundleDirectory(string directory, string bundleRoot, string shadowRoot)
        {
            SafePath.RejectLinks(directory);
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                // Reject links on files as well as directories before resolving/copying: File.Copy
                // follows a file symlink, which could otherwise smuggle content from outside the bundle.
                SafePath.RejectLinks(file);
                var relative = Path.GetRelativePath(bundleRoot, file);
                var source = SafePath.Resolve(bundleRoot, relative);
                var target = SafePath.Resolve(shadowRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target);
            }
            foreach (var child in Directory.EnumerateDirectories(directory))
                CopyManifestBundleDirectory(child, bundleRoot, shadowRoot);
        }

        private static void CopyLegacyBundleDirectory(
            string directory, string bundleRoot, string shadowRoot, bool isRoot = false, bool privateTree = false)
        {
            SafePath.RejectLinks(directory);

            // A manifest is an explicit bundle boundary, even underneath a directory normally
            // treated as private plugin content. Never merge a declared child plugin into its parent.
            var manifestPath = Path.Combine(directory, "plugin.json");
            var hasManifest = File.Exists(manifestPath);
            if (hasManifest)
            {
                SafePath.RejectLinks(manifestPath);
                if (!isRoot) return;
            }

            var keepPrivateTree = isRoot || privateTree || LegacyDependencyDirectoryNames.Contains(Path.GetFileName(directory));
            if (!isRoot && !keepPrivateTree)
            {
                var hasAssemblies = Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly).Any();
                if (hasAssemblies)
                    return; // An unmanifested nested assembly directory retains its discovery boundary.
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

        private const int ShadowCleanupAttempts = 300;
        private static readonly TimeSpan ShadowCleanupRetryDelay = TimeSpan.FromMilliseconds(100);

        private static void ScheduleShadowCleanup(string path)
        {
            // Keep the task independent of plugin/context objects so collectible unload can finish.
            // Windows may retain a collectible assembly's file handles until a later GC; retry long
            // enough for unload to complete instead of abandoning a shadow tree after two seconds.
            _ = Task.Run(async () =>
            {
                await Task.Delay(ShadowCleanupRetryDelay).ConfigureAwait(false);
                await TryDeleteShadowTreeAsync(path, ShadowCleanupAttempts).ConfigureAwait(false);
            });
        }

        private static void ScheduleManagerShadowCleanup(string path)
        {
            _ = Task.Run(async () =>
            {
                // Collectible plugin assemblies may keep Windows file handles alive beyond the
                // original 30-second retry window. Keep this low-frequency reaper alive until the
                // uniquely owned manager root is actually gone; a bounded retry loop can leave a
                // permanent shadow directory after the CLR releases those handles late.
                var retryDelayMs = ShadowCleanupRetryDelay.TotalMilliseconds;
                while (true)
                {
                    try
                    {
                        using (LockShadowRoot(path))
                            await TryDeleteShadowTreeAsync(path, 1).ConfigureAwait(false);
                        if (!Directory.Exists(path))
                        {
                            try { File.Delete(path + ".lock"); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                            return;
                        }
                    }
                    catch (IOException) { } // A live manager or concurrent cleaner currently owns the lock.
                    catch (UnauthorizedAccessException) { }

                    await Task.Delay(TimeSpan.FromMilliseconds(retryDelayMs)).ConfigureAwait(false);
                    retryDelayMs = Math.Min(retryDelayMs * 2, TimeSpan.FromSeconds(2).TotalMilliseconds);
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
                    if (disposed) break;
                    if (pendingChanges.IsEmpty) continue;
                    var changed = pendingChanges.Keys.ToArray();
                    diagnostics.Record(PluginDiagnosticLevel.Trace, "hot-reload", "host", $"change-batch-{changed.Length}");
                    foreach (var path in changed) pendingChanges.TryRemove(path, out _);
                    await lifecycle.WaitAsync(lifetime.Token).ConfigureAwait(false);
                    try
                    {
                        // Disposal may win the semaphore race after the timer check above.
                        if (disposed) break;

                        // Track deferred paths separately so a long-running plugin lease does not
                        // make the general discovery pass repeat on every watcher tick.
                        var requeuedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var group in entries.Values.ToArray().GroupBy(e => e.Candidate))
                        {
                            var directory = Path.TrimEndingDirectorySeparator(group.Key.BundleRoot);
                            var groupChanges = changed.Where(p => p.Equals(directory, StringComparison.OrdinalIgnoreCase) ||
                                p.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToArray();
                            if (groupChanges.Length == 0) continue;
                            var entryRemoved = !File.Exists(group.Key.AssemblyPath) ||
                                (group.Key.ManifestPath is not null && !File.Exists(group.Key.ManifestPath));
                            var reloadable = group.All(e => e.Plugin.SupportsHotReload);
                            if (!reloadable && !entryRemoved) continue;
                            // Lease acquisition also holds lifecycle, so an idle group stays idle until unloaded.
                            if (group.Any(e => e.HasActiveLeases))
                            {
                                foreach (var path in groupChanges)
                                {
                                    pendingChanges[path] = 0;
                                    requeuedPaths.Add(path);
                                }
                                continue;
                            }
                            var ids = group.Select(e => e.Plugin.PluginId).ToArray();
                            foreach (var id in ids) await UnloadAsync(id).ConfigureAwait(false);
                            if (!reloadable) continue;
                            var replacement = Rediscover(group.Key);
                            if (replacement is not null && await LoadAsync(replacement).ConfigureAwait(false))
                                foreach (var id in ids.Where(entries.ContainsKey)) Notify(PluginReloaded, new PluginReloadEventArgs(id), nameof(PluginReloaded));
                        }
                        // A wholly deferred batch has no new discovery work. Running discovery
                        // here would repeatedly copy invalid/legacy candidates every 400 ms until
                        // the outstanding lease completes.
                        if (changed.Any(path => !requeuedPaths.Contains(path)))
                            foreach (var candidate in DiscoverPlugins()) await LoadAsync(candidate).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        diagnostics.Record(PluginDiagnosticLevel.Error, "hot-reload", "host", "batch-failed", ex);
                        TryLogWarning($"Plugin reload failed ({ex.GetType().Name}).");
                    }
                    finally { lifecycle.Release(); }
                }
            }
            catch (OperationCanceledException) { }
        }
        private async Task UnloadAsync(string id)
        {
            Entry? entry;
            lock (renderLock) entries.TryRemove(id, out entry);
            renderFailures.TryRemove(id, out _);
            if (entry is null)
            {
                diagnostics.Record(PluginDiagnosticLevel.Trace, "unload", id, "not-registered");
                return;
            }

            diagnostics.Record(PluginDiagnosticLevel.Information, "unload", id, "started");
            await entry.WaitForLeasesAsync().ConfigureAwait(false);
            try { await entry.Plugin.ShutdownAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                diagnostics.Record(PluginDiagnosticLevel.Warning, "plugin-shutdown", id, "failed", ex);
                TryLogWarning($"Plugin shutdown failed ({ex.GetType().Name}).");
            }
            finally
            {
                try { if (entry.Plugin is IDisposable d) d.Dispose(); }
                catch (Exception ex)
                {
                    diagnostics.Record(PluginDiagnosticLevel.Warning, "plugin-disposal", id, "failed", ex);
                    TryLogWarning($"Plugin disposal failed ({ex.GetType().Name}).");
                }
                finally
                {
                    if (!entries.Values.Any(e => ReferenceEquals(e.Context, entry.Context)))
                    {
                        try { entry.Context.Unload(); }
                        catch (Exception ex) { diagnostics.Record(PluginDiagnosticLevel.Warning, "context-unload", id, "failed", ex); }
                        // Host disposal schedules one manager-root cleanup after all plugin
                        // contexts have been asked to unload; avoid competing child/root reapers.
                        if (!disposed) ScheduleShadowCleanup(entry.ShadowDirectory);
                    }
                }
                diagnostics.Record(PluginDiagnosticLevel.Information, "unload", id, "completed");
                Notify(PluginUnloaded, new PluginUnloadEventArgs(id), nameof(PluginUnloaded));
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
                var correlationId = Guid.NewGuid().ToString("N");
                void RecordExecution(PluginDiagnosticLevel level, string outcome, Exception? exception = null) =>
                    diagnostics.Record(level, "plugin-execution", id, outcome, exception, correlationId);

                try
                {
                    RecordExecution(PluginDiagnosticLevel.Trace, "started");
                    cancellation.Token.ThrowIfCancellationRequested();
                    var result = await action(entry.Plugin, cancellation.Token).ConfigureAwait(false);
                    RecordExecution(PluginDiagnosticLevel.Trace, "succeeded");
                    return result;
                }
                catch (OperationCanceledException)
                {
                    RecordExecution(PluginDiagnosticLevel.Trace, "cancelled");
                    throw;
                }
                catch (Exception ex)
                {
                    RecordExecution(PluginDiagnosticLevel.Error, "failed", ex);
                    try { TryLogWarning($"Plugin operation failed ({ex.GetType().Name})."); } catch { }
                    throw;
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
            {
                foreach (var plugin in GetLoadedPlugins().Where(p => p.SupportsImGui))
                {
                    var id = plugin.PluginId;
                    try { plugin.RenderImGui(); }
                    catch (Exception ex)
                    {
                        var firstFailure = renderFailures.TryAdd(id, 0);
                        diagnostics.Record(
                            firstFailure ? PluginDiagnosticLevel.Error : PluginDiagnosticLevel.Trace,
                            "plugin-render",
                            id,
                            "failed",
                            ex);
                        if (firstFailure)
                        {
                            try { TryLogWarning($"Plugin renderer failed ({ex.GetType().Name})."); } catch { }
                        }
                    }
                }
            }
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
                    catch (Exception ex)
                    {
                        diagnostics.Record(PluginDiagnosticLevel.Error, "renderer", "host", "failed", ex);
                        try { TryLogWarning($"Renderer stopped ({ex.GetType().Name})."); } catch { }
                    }
                    finally
                    {
                        try { renderer?.Dispose(); }
                        catch (Exception ex)
                        {
                            diagnostics.Record(PluginDiagnosticLevel.Warning, "renderer-disposal", "host", "failed", ex);
                            try { TryLogWarning($"Renderer disposal failed ({ex.GetType().Name})."); } catch { }
                        }
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
            // Serialize this state transition with InitializeAsync and watcher batches. In
            // particular, never inspect/tear down watcher fields while initialization assigns them.
            await lifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                if (disposed) return;
                disposed = true;
                watcher?.Dispose();
                diagnostics.Record(PluginDiagnosticLevel.Information, "dispose", "host", "started");
            }
            finally { lifecycle.Release(); }

            // Cancel after releasing lifecycle: cancellation callbacks may themselves finish
            // plugin work that needs to release a lease and/or leave a lifecycle-protected path.
            await lifetime.CancelAsync().ConfigureAwait(false);
            if (watchTask is not null) await watchTask.ConfigureAwait(false);
            if (renderStopped is not null) await renderStopped.Task.ConfigureAwait(false);

            var cleanupCompleted = false;
            try
            {
                await UnloadAllPluginsAsync().ConfigureAwait(false);
                cleanupCompleted = true;
            }
            finally
            {
                await TryDeleteShadowTreeAsync(managerShadowRoot, 1).ConfigureAwait(false);
                shadowRootLock?.Dispose();
                shadowRootLock = null;
                try
                {
                    if (!Directory.Exists(managerShadowRoot)) File.Delete(managerShadowRoot + ".lock");
                    else ScheduleManagerShadowCleanup(managerShadowRoot);
                }
                catch (IOException) { ScheduleManagerShadowCleanup(managerShadowRoot); }
                catch (UnauthorizedAccessException) { ScheduleManagerShadowCleanup(managerShadowRoot); }

                diagnostics.Record(
                    cleanupCompleted ? PluginDiagnosticLevel.Information : PluginDiagnosticLevel.Warning,
                    "dispose",
                    "host",
                    cleanupCompleted ? "completed" : "cleanup-incomplete");
                lifetime.Dispose();
            }
        }
    }

    /// <summary>Indicates that a plugin candidate failed manifest, compatibility, or dependency validation.</summary>
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
