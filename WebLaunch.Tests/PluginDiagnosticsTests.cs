using El_Garnan_Plugin_Loader;
using El_Garnan_Plugin_Loader.Interfaces;
using Xunit;

namespace WebLaunch.Tests;

public sealed class PluginDiagnosticsTests
{
    private sealed class Logger : ILogger
    {
        public void Debug(string message) { }
        public void Information(string message) { }
        public void Warning(string message) { }
        public void Error(string message) { }
        public void Error(string message, Exception exception) { }
    }

    [Fact]
    public async Task MissingManifestAssemblyIsPersistedWithStageTypeAndHResultWithoutPrivatePaths()
    {
        using var root = new TemporaryDirectory();
        var pluginsRoot = Directory.CreateDirectory(Path.Combine(root.Path, "Plugins")).FullName;
        var bundle = Directory.CreateDirectory(Path.Combine(pluginsRoot, "FFXIV")).FullName;
        File.WriteAllText(Path.Combine(bundle, "plugin.json"),
            """{"version":1,"id":"ffxiv-launcher","assembly":"missing.dll"}""");

        await using var host = new CoreFunctions(
            pluginsRoot,
            new Logger(),
            enableHotReload: false,
            useStandaloneWindow: false,
            diagnosticsDirectory: Path.Combine(root.Path, "diagnostics"),
            debugDiagnostics: true);

        await host.InitializeAsync();

        var failure = Assert.Contains(
            host.GetDiagnosticSnapshot(256),
            entry => entry.Level == PluginDiagnosticLevel.Error &&
                     entry.Stage == "manifest" &&
                     entry.PluginId == "ffxiv-launcher" &&
                     entry.Outcome == "entry-assembly-missing" &&
                     entry.ExceptionType == nameof(FileNotFoundException));
        Assert.NotNull(failure.HResult);
        Assert.True(File.Exists(host.DiagnosticLogPath));

        var text = File.ReadAllText(host.DiagnosticLogPath);
        Assert.Contains("stage=manifest", text);
        Assert.Contains("plugin=ffxiv-launcher", text);
        Assert.Contains("outcome=entry-assembly-missing", text);
        Assert.Contains("FileNotFoundException", text);
        Assert.Contains("hr=0x", text);
        Assert.DoesNotContain(root.Path, text);
    }

    [Fact]
    public async Task EmptyPluginDirectoryProducesAnActionableDiagnosticInsteadOfSilentStartup()
    {
        using var root = new TemporaryDirectory();
        var pluginsRoot = Directory.CreateDirectory(Path.Combine(root.Path, "empty-plugins")).FullName;
        await using var host = new CoreFunctions(
            pluginsRoot,
            new Logger(),
            enableHotReload: false,
            useStandaloneWindow: false,
            diagnosticsDirectory: Path.Combine(root.Path, "diagnostics"),
            debugDiagnostics: false);

        await host.InitializeAsync();

        Assert.Contains(
            host.GetDiagnosticSnapshot(256),
            entry => entry.Level == PluginDiagnosticLevel.Warning &&
                     entry.Stage == "discovery" &&
                     entry.Outcome == "no-plugin-candidates");
        Assert.True(File.Exists(host.DiagnosticLogPath));
        Assert.Empty(host.GetLoadedPlugins());
    }

    [Fact]
    public void DiagnosticsKeepOnlyControlledFieldsAndRetainABoundedRecentHistory()
    {
        using var root = new TemporaryDirectory();
        var diagnostics = new PluginDiagnostics(root.Path, debugEnabled: true);
        var secret = "synthetic-password-and-private-path";
        diagnostics.Record(
            PluginDiagnosticLevel.Error,
            "assembly-load",
            "ffxiv-launcher",
            "failed",
            new IOException(secret));

        var failure = Assert.Single(diagnostics.GetRecent());
        Assert.Equal("assembly-load", failure.Stage);
        Assert.Equal("ffxiv-launcher", failure.PluginId);
        Assert.Equal("failed", failure.Outcome);
        Assert.Equal(nameof(IOException), failure.ExceptionType);
        Assert.NotNull(failure.HResult);

        for (var i = 0; i < 300; i++)
            diagnostics.Record(PluginDiagnosticLevel.Information, "load", "spellborn-launcher", $"loaded-{i}");

        var history = diagnostics.GetRecent();
        Assert.Equal(256, history.Count);
        Assert.Equal("loaded-299", history[0].Outcome);

        var text = File.ReadAllText(diagnostics.LogPath);
        Assert.DoesNotContain(secret, text);
        Assert.DoesNotContain(root.Path, text);
        Assert.DoesNotContain(nameof(IOException) + ": " + secret, text);
    }

    [Fact]
    public void TraceEntriesAreOmittedUnlessExplicitDebugDiagnosticsAreEnabled()
    {
        using var root = new TemporaryDirectory();
        var diagnostics = new PluginDiagnostics(root.Path, debugEnabled: false);

        diagnostics.Record(PluginDiagnosticLevel.Trace, "assembly-load", "ffxiv-launcher", "started");
        diagnostics.Record(PluginDiagnosticLevel.Warning, "discovery", "host", "no-plugin-candidates");

        var entries = diagnostics.GetRecent();
        Assert.Single(entries);
        Assert.Equal("no-plugin-candidates", entries[0].Outcome);
        Assert.Contains("no-plugin-candidates", File.ReadAllText(diagnostics.LogPath));
        Assert.DoesNotContain("assembly-load", File.ReadAllText(diagnostics.LogPath));
    }
}
