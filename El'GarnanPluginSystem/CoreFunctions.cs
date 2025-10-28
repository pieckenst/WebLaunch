using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using System.IO;
using System.Linq;
using El_Garnan_Plugin_Loader.Interfaces;
using El_Garnan_Plugin_Loader.Models;

namespace El_Garnan_Plugin_Loader
{
    /// <summary>
    /// Core functions for managing plugins.
    /// </summary>
    public class CoreFunctions : IDisposable
    {
        private readonly string _pluginsPath;
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<string, IGamePlugin> _loadedPlugins;
        private readonly ConcurrentDictionary<string, FileSystemWatcher> _pluginWatchers;
        private readonly bool _enableHotReload;
        private readonly SemaphoreSlim _reloadLock = new(1, 1);
        private readonly HashSet<string> _loadedFiles = new();
        private readonly HashSet<string> _erroredFiles = new();
        private bool _isInitialized;
        private bool _isDisposed;
        private int _loadedCount;
        private int _failedCount;

        /// <summary>
        /// Event triggered when a plugin is loaded.
        /// </summary>
        public event EventHandler<PluginLoadEventArgs> PluginLoaded;

        /// <summary>
        /// Event triggered when a plugin is unloaded.
        /// </summary>
        public event EventHandler<PluginUnloadEventArgs> PluginUnloaded;

        /// <summary>
        /// Event triggered when a plugin is reloaded.
        /// </summary>
        public event EventHandler<PluginReloadEventArgs> PluginReloaded;

        /// <summary>
        /// Event triggered when a plugin encounters an error.
        /// </summary>
        public event EventHandler<PluginErrorEventArgs> PluginError;

        private IPluginRenderer _renderer;
        private bool _useStandaloneWindow;
        private bool _renderingStarted;

        /// <summary>
        /// Initializes a new instance of the <see cref="CoreFunctions"/> class.
        /// </summary>
        /// <param name="pluginsPath">The path to the plugins directory.</param>
        /// <param name="logger">The logger instance.</param>
        /// <param name="enableHotReload">Whether hot reload is enabled.</param>
        public CoreFunctions(string pluginsPath, ILogger logger, bool enableHotReload = false, bool useStandaloneWindow = true)
        {
            _pluginsPath = pluginsPath;
            _logger = logger;
            _enableHotReload = enableHotReload;
            _loadedPlugins = new ConcurrentDictionary<string, IGamePlugin>();
            _pluginWatchers = new ConcurrentDictionary<string, FileSystemWatcher>();
            _useStandaloneWindow = useStandaloneWindow;
            if (_useStandaloneWindow)
            {
                _renderer = new ImGuiPluginRenderer(logger);
            }
        }

        /// <summary>
        /// Asynchronously initializes the plugin system.
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_isInitialized)
                return;

            _logger.Information("Initializing plugin system...");
            
            try
            {
                if (!Directory.Exists(_pluginsPath))
                {
                    Directory.CreateDirectory(_pluginsPath);
                }

                await LoadPluginsAsync();
                
                if (_enableHotReload)
                {
                    SetupHotReload();
                }

                _isInitialized = true;
                _logger.Information($"Plugin system initialized successfully. Loaded: {_loadedCount}, Failed: {_failedCount}");
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to initialize plugin system", ex);
                throw;
            }
        }

        /// <summary>
        /// Asynchronously loads plugins from the plugins directory.
        /// </summary>
        private async Task LoadPluginsAsync()
        {
            _logger.Information($"Loading plugins from {_pluginsPath}");
            var pluginFiles = Directory.GetFiles(_pluginsPath, "*.dll", SearchOption.AllDirectories);
            
            foreach (var pluginPath in pluginFiles)
            {
                if (_loadedFiles.Contains(pluginPath) || _erroredFiles.Contains(pluginPath))
                {
                    continue;
                }

                try
                {
                    _logger.Debug($"Attempting to load plugin: {pluginPath}");
                    
                    var loadContext = new PluginLoadContext(pluginPath);
                    var assembly = loadContext.LoadFromAssemblyPath(pluginPath);
                    
                    foreach (var type in assembly.GetTypes())
                    {
                        if (!typeof(IGamePlugin).IsAssignableFrom(type) || type.IsAbstract)
                            continue;

                        var plugin = (IGamePlugin)Activator.CreateInstance(type, _logger);
                        
                        if (await ValidatePlugin(plugin))
                        {
                            await plugin.InitializeAsync();
                            
                            if (_loadedPlugins.TryAdd(plugin.PluginId, plugin))
                            {
                                _loadedCount++;
                                _loadedFiles.Add(pluginPath);
                                _logger.Information($"Loaded plugin: {plugin.Name} v{plugin.Version}");
                                PluginLoaded?.Invoke(this, new PluginLoadEventArgs(plugin));
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _failedCount++;
                    _erroredFiles.Add(pluginPath);
                    _logger.Error($"Failed to load plugin {pluginPath}", ex);
                    PluginError?.Invoke(this, new PluginErrorEventArgs(pluginPath, ex));
                }
            }
        }

        /// <summary>
        /// Validates the specified plugin.
        /// </summary>
        /// <param name="plugin">The plugin to validate.</param>
        /// <returns>A task representing the asynchronous operation, with a boolean result indicating success.</returns>
        private async Task<bool> ValidatePlugin(IGamePlugin plugin)
        {
            try
            {
                if (plugin.Dependencies?.Any() == true)
                {
                    foreach (var dependency in plugin.Dependencies)
                    {
                        // First check if it's a plugin dependency
                        if (!_loadedPlugins.TryGetValue(dependency.Name, out var dependencyPlugin))
                        {
                            // If not a plugin, check if it's a library in the base directory
                            var assemblyPath = Path.Combine(AppContext.BaseDirectory, $"{dependency.Name}.dll");
                            if (!File.Exists(assemblyPath))
                            {
                                // Finally check in the plugin directory
                                assemblyPath = Path.Combine(_pluginsPath, $"{dependency.Name}.dll");
                                if (!File.Exists(assemblyPath))
                                {
                                    throw new PluginValidationException($"Missing dependency: {dependency.Name}");
                                }
                            }

                            var assembly = Assembly.LoadFrom(assemblyPath);
                            var assemblyVersion = assembly.GetName().Version;
                            if (assemblyVersion < dependency.MinVersion)
                            {
                                throw new PluginValidationException($"Invalid dependency version: {dependency.Name} (>= {dependency.MinVersion})");
                            }
                        }
                    }
                }

                return await plugin.ValidateConfigurationAsync();
            }
            catch (Exception ex)
            {
                _logger.Error($"Plugin validation failed for {plugin.Name}", ex);
                return false;
            }
        }


        /// <summary>
        /// Sets up hot reload monitoring for the plugins directory.
        /// </summary>
        private void SetupHotReload()
        {
            foreach (var directory in Directory.GetDirectories(_pluginsPath, "*", SearchOption.AllDirectories))
            {
                var watcher = new FileSystemWatcher(directory)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    Filter = "*.dll",
                    EnableRaisingEvents = true
                };

                watcher.Changed += async (s, e) => await HandlePluginChangeAsync(e.FullPath);
                watcher.Created += async (s, e) => await HandlePluginChangeAsync(e.FullPath);
                watcher.Deleted += async (s, e) => await HandlePluginDeletionAsync(e.FullPath);

                _pluginWatchers.TryAdd(directory, watcher);
            }

            _logger.Information("Hot reload monitoring enabled");
        }

        /// <summary>
        /// Handles changes to a plugin file.
        /// </summary>
        /// <param name="pluginPath">The path to the plugin file.</param>
        private async Task HandlePluginChangeAsync(string pluginPath)
        {
            try
            {
                await _reloadLock.WaitAsync();
                await Task.Delay(100); // Debounce

                var pluginId = Path.GetFileNameWithoutExtension(pluginPath);
                if (_loadedPlugins.TryGetValue(pluginId, out var existingPlugin))
                {
                    if (!existingPlugin.SupportsHotReload)
                    {
                        _logger.Warning($"Plugin {pluginId} does not support hot reload");
                        return;
                    }

                    await UnloadPluginAsync(pluginId);
                }

                await LoadPluginAsync(pluginPath);
                PluginReloaded?.Invoke(this, new PluginReloadEventArgs(pluginId));
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to handle plugin change for {pluginPath}", ex);
                PluginError?.Invoke(this, new PluginErrorEventArgs(pluginPath, ex));
            }
            finally
            {
                _reloadLock.Release();
            }
        }

        /// <summary>
        /// Handles the deletion of a plugin file.
        /// </summary>
        /// <param name="pluginPath">The path to the plugin file.</param>
        private async Task HandlePluginDeletionAsync(string pluginPath)
        {
            var pluginId = Path.GetFileNameWithoutExtension(pluginPath);
            await UnloadPluginAsync(pluginId);
        }

        /// <summary>
        /// Asynchronously loads a plugin from the specified path.
        /// </summary>
        /// <param name="pluginPath">The path to the plugin file.</param>
        private async Task LoadPluginAsync(string pluginPath)
        {
            try
            {
                var loadContext = new PluginLoadContext(pluginPath);
                var assembly = loadContext.LoadFromAssemblyPath(pluginPath);

                foreach (var type in assembly.GetTypes())
                {
                    if (!typeof(IGamePlugin).IsAssignableFrom(type) || type.IsAbstract)
                        continue;

                    var plugin = (IGamePlugin)Activator.CreateInstance(type, _logger);
                    
                    if (await ValidatePlugin(plugin))
                    {
                        await plugin.InitializeAsync();
                        
                        if (_loadedPlugins.TryAdd(plugin.PluginId, plugin))
                        {
                            _loadedFiles.Add(pluginPath);
                            _logger.Information($"Loaded plugin: {plugin.Name} v{plugin.Version}");
                            PluginLoaded?.Invoke(this, new PluginLoadEventArgs(plugin));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _erroredFiles.Add(pluginPath);
                _logger.Error($"Failed to load plugin {pluginPath}", ex);
                throw;
            }
        }

        /// <summary>
        /// Asynchronously unloads the specified plugin.
        /// </summary>
        /// <param name="pluginId">The ID of the plugin to unload.</param>
        private async Task UnloadPluginAsync(string pluginId)
        {
            if (_loadedPlugins.TryRemove(pluginId, out var plugin))
            {
                try
                {
                    await plugin.ShutdownAsync();
                    _loadedFiles.Remove(pluginId);
                    PluginUnloaded?.Invoke(this, new PluginUnloadEventArgs(pluginId));
                }
                catch (Exception ex)
                {
                    _logger.Error($"Error unloading plugin {pluginId}", ex);
                }
            }
        }

        /// <summary>
        /// Gets the plugin with the specified ID.
        /// </summary>
        /// <param name="pluginId">The ID of the plugin to get.</param>
        /// <returns>The plugin with the specified ID.</returns>
        public IGamePlugin GetPlugin(string pluginId)
        {
            return _loadedPlugins.TryGetValue(pluginId, out var plugin) 
                ? plugin 
                : throw new KeyNotFoundException($"Plugin {pluginId} not found");
        }

        /// <summary>
        /// Gets all loaded plugins.
        /// </summary>
        /// <returns>An enumerable of all loaded plugins.</returns>
        public IEnumerable<IGamePlugin> GetLoadedPlugins() => _loadedPlugins.Values;

        /// <summary>
        /// Asynchronously unloads all loaded plugins.
        /// </summary>
        public async Task UnloadAllPluginsAsync()
        {
            foreach (var plugin in _loadedPlugins.Values)
            {
                try
                {
                    await plugin.ShutdownAsync();
                }
                catch (Exception ex)
                {
                    _logger.Error($"Error unloading plugin {plugin.Name}", ex);
                }
            }
            
            _loadedPlugins.Clear();
            _loadedFiles.Clear();
        }


        /// <summary>
        /// Renders ImGui interfaces for all plugins that support it
        /// </summary>
        public void RenderPluginInterfaces()
        {
            foreach (var plugin in _loadedPlugins.Values)
            {
                if (plugin.SupportsImGui)
                {
                    plugin.RenderImGui();
                }
            }
        }

        public void EnableStandaloneRenderer()
        {
            if (_useStandaloneWindow && _renderer != null)
            {
                return;
            }

            _useStandaloneWindow = true;
            _renderer ??= new ImGuiPluginRenderer(_logger);
        }

        public void StartRendering()
        {
            if (_renderingStarted || !_useStandaloneWindow || _renderer == null)
            {
                return;
            }

            _renderingStarted = true;

            _renderer.Initialize();
            _renderer.SetPlugins(_loadedPlugins.Values);

            Task.Run(() =>
            {
                while (true)
                {
                    _renderer.Render();
                    Thread.Sleep(16); // ~60 FPS
                }
            });

            Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(1));

                var imGuiNotificationServices = _loadedPlugins.Values
                    .Where(p => p.SupportsImGui)
                    .OfType<INotificationService>()
                    .ToList();

                if (!imGuiNotificationServices.Any())
                {
                    _logger.Debug("ImGui renderer initialized but no notification-capable plugins were found.");
                    return;
                }

                foreach (var notifier in imGuiNotificationServices)
                {
                    try
                    {
                        notifier.ShowNotification("ImGui Debug", "Renderer is active for this plugin.", NotificationType.Info);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning($"Failed to send ImGui debug notification: {ex.Message}");
                    }
                }
            });
        }

        /// <summary>
        /// Disposes the resources used by the <see cref="CoreFunctions"/> class.
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed)
                return;

            foreach (var watcher in _pluginWatchers.Values)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            
            _pluginWatchers.Clear();
            _reloadLock.Dispose();
            _isDisposed = true;
        }
    }

    

    /// <summary>
    /// Exception thrown when plugin validation fails.
    /// </summary>
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
