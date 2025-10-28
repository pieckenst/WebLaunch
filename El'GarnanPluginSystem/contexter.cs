using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace El_Garnan_Plugin_Loader
{
    /// <summary>
    /// Represents a custom assembly load context for loading plugins.
    /// </summary>
    public class PluginLoadContext : AssemblyLoadContext
    {
        private AssemblyDependencyResolver _pluginResolver;
        private AssemblyDependencyResolver _processResolver;
        private string _pluginPath;

        /// <summary>
        /// Initializes a new instance of the <see cref="PluginLoadContext"/> class with the specified plugin path.
        /// </summary>
        /// <param name="pluginPath">The path to the plugin directory.</param>
        public PluginLoadContext(string pluginPath)
        {
            _pluginPath = pluginPath;
            _pluginResolver = new AssemblyDependencyResolver(pluginPath);
            _processResolver = new AssemblyDependencyResolver(AppContext.BaseDirectory);
        }

        /// <summary>
        /// Loads the specified assembly by its name.
        /// </summary>
        /// <param name="assemblyName">The name of the assembly to load.</param>
        /// <returns>The loaded assembly, or null if the assembly cannot be found.</returns>
        protected override Assembly Load(AssemblyName assemblyName)
        {
            Assembly sharedAssembly = AssemblyLoadContext.Default.Assemblies
                .FirstOrDefault(a => string.Equals(a.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase));

            if (sharedAssembly != null)
            {
                return sharedAssembly;
            }

            string assemblyPath = _pluginResolver.ResolveAssemblyToPath(assemblyName) ??
                                  _processResolver.ResolveAssemblyToPath(assemblyName);

            return assemblyPath != null
                ? LoadFromAssemblyPath(assemblyPath)
                : null;
        }

        /// <summary>
        /// Loads the specified unmanaged DLL by its name.
        /// </summary>
        /// <param name="unmanagedDllName">The name of the unmanaged DLL to load.</param>
        /// <returns>A pointer to the loaded unmanaged DLL, or IntPtr.Zero if the DLL cannot be found.</returns>
        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            string libraryPath = _pluginResolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (libraryPath == null)
            {
                libraryPath = _processResolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            }

            if (libraryPath != null)
            {
                return LoadUnmanagedDllFromPath(libraryPath);
            }

            return IntPtr.Zero;
        }
    }
}
