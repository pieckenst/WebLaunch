using CoreLibLaunchSupport;
using El_Garnan_Plugin_Loader.Interfaces;
using El_Garnan_Plugin_Loader.Models;
using GamePlugins.FFXIV;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using XIVLauncher.Common.Addon;
using Xunit;

namespace WebLaunch.Tests;

[CollectionDefinition("Trace state", DisableParallelization = true)]
public sealed class TraceStateCollection { }

[Collection("Trace state")]
public sealed class ReviewRegressionTests
{
    private sealed class PluginLogger : El_Garnan_Plugin_Loader.Interfaces.ILogger
    {
        public void Debug(string message) { }
        public void Information(string message) { }
        public void Warning(string message) { }
        public void Error(string message) { }
        public void Error(string message, Exception exception) { }
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task ValidationFailuresStillClearCredentials(bool validCredentials)
    {
        using var root = new TemporaryDirectory();
        var plugin = new FFXIVGamePlugin(new PluginLogger());
        var credentials = new GameCredentials
        {
            Username = validCredentials ? "test" : "",
            Password = "synthetic-password", OTP = "123456", Token = "synthetic-token"
        };
        var request = new GameLaunchParameters { GamePath = root.Path, Credentials = credentials };
        request.EnvironmentVariables["FFXIV_PASSWORD"] = "synthetic-password";
        request.EnvironmentVariables["FFXIV_OTP"] = "123456";
        var error = await Record.ExceptionAsync(() => plugin.LaunchAsync(request, new Progress<WebLaunch.Core.LaunchStatus>(), default));
        if (validCredentials)
        {
            Assert.IsType<DirectoryNotFoundException>(error);
            Assert.Equal("Select the FFXIV installation folder containing boot and game.", error.Message);
        }
        else
        {
            Assert.IsType<ArgumentException>(error);
            Assert.Equal("Username and password are required.", error.Message);
        }
        Assert.Equal("", credentials.Password); Assert.Equal("", credentials.OTP); Assert.Equal("", credentials.Token);
        Assert.False(request.EnvironmentVariables.ContainsKey("FFXIV_PASSWORD"));
        Assert.False(request.EnvironmentVariables.ContainsKey("FFXIV_OTP"));
    }

    [Fact] public void UnsupportedPlatformsDeleteLegacyCacheAndKeepNewSessionsInMemory()
    {
        if (OperatingSystem.IsWindows()) return;
        using var root = new TemporaryDirectory();
        var file = Path.Combine(root.Path, "cache.json");
        File.WriteAllText(file, "legacy plaintext session");
        var cache = new CommonUniqueIdCache(new FileInfo(file));
        Assert.False(File.Exists(file));
        cache.Add("test", "session", 1, 1);
        Assert.True(cache.TryGet("test", out var cached)); Assert.Equal("session", cached.UniqueId);
        Assert.False(File.Exists(file));
    }

    [Fact] public void ConfiguredAddonEntriesReachTheEnabledLaunchSelection()
    {
        var original = networklogic.AddonEntries.ToArray();
        try
        {
            var enabled = new AddonEntry { IsEnabled = true, Addon = new() };
            var disabled = new AddonEntry { IsEnabled = false, Addon = new() };
            var configuration = new networklogic { Addons = [enabled, disabled] };
            Assert.Equal(new[] { enabled, disabled }, networklogic.AddonEntries);
            Assert.Same(enabled, Assert.Single(networklogic.AddonEntries, entry => entry.IsEnabled));
            configuration.Addons = configuration.Addons;
            Assert.Equal(2, networklogic.AddonEntries.Count);
        }
        finally { networklogic.AddonEntries.Clear(); networklogic.AddonEntries.AddRange(original); }
    }

    private sealed class Sink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
    [Fact] public void NormalTraceLoggerErrorsNeverIncludeExceptionDetails()
    {
        var original = Log.Logger;
        var sink = new Sink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        try
        {
            Log.Logger = logger;
            FfxivTraceLogger.Error("test", "Plugin launch failed", new IOException("synthetic-secret", new Exception("nested-secret")));
            var entry = Assert.Single(sink.Events);
            Assert.Null(entry.Exception);
            Assert.DoesNotContain("secret", entry.RenderMessage());
            Assert.Contains("I/O failure", entry.RenderMessage());
        }
        finally { Log.Logger = original; }
    }

    [Fact] public void TraceRetentionRemovesOnlyExpiredTraceFiles()
    {
        using var root = new TemporaryDirectory();
        var old = Path.Combine(root.Path, "ffxiv-trace-old.log");
        var recent = Path.Combine(root.Path, "ffxiv-trace-recent.log");
        var unrelated = Path.Combine(root.Path, "desktop.log");
        foreach (var file in new[] { old, recent, unrelated }) File.WriteAllText(file, "synthetic");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-8));
        File.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow.AddDays(-8));
        FfxivTraceLogger.CleanupTraceFiles(root.Path);
        Assert.False(File.Exists(old)); Assert.True(File.Exists(recent)); Assert.True(File.Exists(unrelated));
    }
}
