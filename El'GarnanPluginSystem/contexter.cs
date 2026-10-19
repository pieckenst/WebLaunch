using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace El_Garnan_Plugin_Loader
{
    /// <summary>
    /// Resolves one plugin's private dependencies before falling back to assemblies already
    /// available to the host. It shares the plugin contract/runtime services from the default
    /// context to preserve type identity across the plugin boundary.
    /// </summary>
    public sealed class PluginLoadContext : AssemblyLoadContext
    {
        private static readonly HashSet<string> SharedAssemblies = new(StringComparer.OrdinalIgnoreCase)
        {
            "Elgarnan", "WebLaunch.Core", "CoreLibLaunchSupport", "LibDalamud",
            "ImGui.NET", "Veldrid", "Veldrid.SDL2", "Serilog"
        };

        private readonly AssemblyDependencyResolver pluginResolver;
        private readonly AssemblyDependencyResolver processResolver;
        private readonly PluginDiagnostics? diagnostics;
        private readonly string diagnosticPluginId;
        private readonly string correlationId;

        /// <summary>Creates a collectible load context without host diagnostics.</summary>
        public PluginLoadContext(string pluginPath)
            : this(pluginPath, null, "legacy", Guid.NewGuid().ToString("N"))
        {
        }

        internal PluginLoadContext(
            string pluginPath,
            PluginDiagnostics? diagnostics,
            string pluginId,
            string correlationId)
            : base(isCollectible: true)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pluginPath);
            this.diagnostics = diagnostics;
            diagnosticPluginId = SafeLabel(pluginId, "legacy");
            this.correlationId = SafeLabel(correlationId, Guid.NewGuid().ToString("N"));
            pluginResolver = new AssemblyDependencyResolver(Path.GetFullPath(pluginPath));
            processResolver = new AssemblyDependencyResolver(typeof(PluginLoadContext).Assembly.Location);

            // This event is reached only when the normal plugin, host and runtime resolution paths
            // could not provide the assembly. Keep the event diagnostic-only; never probe arbitrary
            // locations or turn a failed lookup into an implicit load from an untrusted path.
            Resolving += (_, name) =>
            {
                Record(
                    PluginDiagnosticLevel.Error,
                    "assembly-resolution",
                    $"unresolved-{SafeLabel(name.Name, "unknown")}");
                return null;
            };
            ResolvingUnmanagedDll += (_, name) =>
            {
                Record(
                    PluginDiagnosticLevel.Warning,
                    "native-resolution",
                    $"unresolved-{SafeLabel(name, "unknown")}");
                return IntPtr.Zero;
            };
        }

        /// <summary>
        /// Loads a managed dependency from the plugin's bundle first, then the host's dependency
        /// graph. Returning null intentionally lets the runtime make its normal default-context
        /// resolution attempt after both explicit resolvers have been checked.
        /// </summary>
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var name = SafeLabel(assemblyName.Name, "unknown");
            if (SharedAssemblies.Contains(name))
            {
                Record(PluginDiagnosticLevel.Trace, "assembly-resolution", $"shared-default-context-{name}");
                return AssemblyLoadContext.Default.LoadFromAssemblyName(assemblyName);
            }

            string? path = null;
            try
            {
                path = pluginResolver.ResolveAssemblyToPath(assemblyName);
            }
            catch (Exception ex)
            {
                Record(PluginDiagnosticLevel.Warning, "assembly-resolution", $"plugin-resolver-failed-{name}", ex);
            }

            if (path is not null)
            {
                Record(PluginDiagnosticLevel.Trace, "assembly-resolution", $"plugin-bundle-resolved-{name}");
                return LoadFromAssemblyPath(path);
            }

            try
            {
                path = processResolver.ResolveAssemblyToPath(assemblyName);
            }
            catch (Exception ex)
            {
                Record(PluginDiagnosticLevel.Warning, "assembly-resolution", $"host-resolver-failed-{name}", ex);
            }

            if (path is not null)
            {
                Record(PluginDiagnosticLevel.Trace, "assembly-resolution", $"host-fallback-resolved-{name}");
                return LoadFromAssemblyPath(path);
            }

            Record(PluginDiagnosticLevel.Trace, "assembly-resolution", $"no-explicit-resolver-path-{name}");
            return null;
        }

        /// <summary>Resolves native libraries using the same plugin-first, host-fallback order.</summary>
        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var name = SafeLabel(unmanagedDllName, "unknown");
            string? path = null;
            try
            {
                path = pluginResolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            }
            catch (Exception ex)
            {
                Record(PluginDiagnosticLevel.Warning, "native-resolution", $"plugin-resolver-failed-{name}", ex);
            }

            if (path is not null)
            {
                Record(PluginDiagnosticLevel.Trace, "native-resolution", $"plugin-bundle-resolved-{name}");
                return LoadUnmanagedDllFromPath(path);
            }

            try
            {
                path = processResolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            }
            catch (Exception ex)
            {
                Record(PluginDiagnosticLevel.Warning, "native-resolution", $"host-resolver-failed-{name}", ex);
            }

            if (path is not null)
            {
                Record(PluginDiagnosticLevel.Trace, "native-resolution", $"host-fallback-resolved-{name}");
                return LoadUnmanagedDllFromPath(path);
            }

            Record(PluginDiagnosticLevel.Trace, "native-resolution", $"no-explicit-resolver-path-{name}");
            return IntPtr.Zero;
        }

        private void Record(
            PluginDiagnosticLevel level,
            string stage,
            string outcome,
            Exception? exception = null) =>
            diagnostics?.Record(level, stage, diagnosticPluginId, outcome, exception, correlationId);

        private static string SafeLabel(string? value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            var safe = new string(value
                .Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')
                .Take(48)
                .ToArray());
            return safe.Length == 0 ? fallback : safe;
        }
    }
}
