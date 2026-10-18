using CoreLibLaunchSupport;
using El_Garnan_Plugin_Loader.Interfaces;
using El_Garnan_Plugin_Loader.Models;
using GamePlugins.FFXIV;
using LibDalamud.Common.Dalamud;
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
        public List<(string Message, Exception? Exception)> Errors { get; } = [];
        public void Debug(string message) { }
        public void Information(string message) { }
        public void Warning(string message) { }
        public void Error(string message) => Errors.Add((message, null));
        public void Error(string message, Exception exception) => Errors.Add((message, exception));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task ValidationFailuresStillClearCredentials(bool validCredentials)
    {
        using var root = new TemporaryDirectory();
        var logger = new PluginLogger();
        var plugin = new FFXIVGamePlugin(logger);
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
        var logged = Assert.Single(logger.Errors);
        Assert.Equal($"FFXIV launch failed: {error!.GetType().Name}", logged.Message);
        Assert.Null(logged.Exception);
        Assert.DoesNotContain(error.Message, logged.Message);
        Assert.Equal("", credentials.Password); Assert.Equal("", credentials.OTP); Assert.Equal("", credentials.Token);
        Assert.False(request.EnvironmentVariables.ContainsKey("FFXIV_PASSWORD"));
        Assert.False(request.EnvironmentVariables.ContainsKey("FFXIV_OTP"));
    }

    private sealed class LaunchProgress(Action<WebLaunch.Core.LaunchStatus> report) : IProgress<WebLaunch.Core.LaunchStatus>
    {
        public void Report(WebLaunch.Core.LaunchStatus value) => report(value);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task FfxivLaunchClassifiesCancellationUsingTheCallerToken(bool cancelCaller)
    {
        using var root = new TemporaryDirectory();
        var game = Directory.CreateDirectory(Path.Combine(root.Path, "game")).FullName;
        File.WriteAllText(Path.Combine(game, "ffxiv_dx11.exe"), "synthetic executable");
        var logger = new PluginLogger();
        var plugin = new FFXIVGamePlugin(logger);
        using var caller = new CancellationTokenSource();
        var credentials = new GameCredentials { Username = "test", Password = "synthetic-password", OTP = "123456", Token = "synthetic-token" };
        var request = new GameLaunchParameters { GamePath = root.Path, Credentials = credentials };
        request.EnvironmentVariables["FFXIV_PASSWORD"] = credentials.Password;
        request.EnvironmentVariables["FFXIV_OTP"] = credentials.OTP;
        var failure = new TaskCanceledException("synthetic timeout");
        // Raise the exception inside the launch try block without contacting the authentication service.
        var progress = new LaunchProgress(_ =>
        {
            if (cancelCaller) caller.Cancel();
            throw failure;
        });

        var error = await Record.ExceptionAsync(() => plugin.LaunchAsync(request, progress, caller.Token));

        Assert.Same(failure, error);
        Assert.Equal(cancelCaller, caller.IsCancellationRequested);
        if (cancelCaller) Assert.Empty(logger.Errors);
        else
        {
            var logged = Assert.Single(logger.Errors);
            Assert.Equal("FFXIV launch failed: TaskCanceledException", logged.Message);
            Assert.Null(logged.Exception);
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

    [Fact] public void UnsupportedPlatformsKeepSessionsWhenLegacyCacheCannotBeDeleted()
    {
        if (OperatingSystem.IsWindows()) return;
        using var root = new TemporaryDirectory();
        var file = Path.Combine(root.Path, "cache.json");
        File.WriteAllText(file, "legacy plaintext session");
        var mode = File.GetUnixFileMode(root.Path);
        try
        {
            File.SetUnixFileMode(root.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var cache = new CommonUniqueIdCache(new FileInfo(file));
            Assert.False(cache.HasValidCache("test"));
            cache.Add("test", "session", 1, 1);
            Assert.True(cache.TryGet("test", out var cached));
            Assert.Equal("session", cached.UniqueId);
            Assert.True(File.Exists(file));
        }
        finally { File.SetUnixFileMode(root.Path, mode); }
    }

    [Theory][InlineData("write")][InlineData("move")][InlineData("missing-directory")]
    public void WindowsCacheSaveFailuresKeepSessionsInMemory(string failure)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = new TemporaryDirectory();
        var file = Path.Combine(root.Path, "cache.json");
        var cache = new CommonUniqueIdCache(new FileInfo(file));
        if (failure == "write") Directory.CreateDirectory(file + ".tmp");
        else if (failure == "move") Directory.CreateDirectory(file);
        else Directory.Delete(root.Path);
        try
        {
            cache.Add("test", "session", 1, 1);
            Assert.True(cache.TryGet("test", out var cached));
            Assert.Equal("session", cached.UniqueId);
            Assert.False(File.Exists(file + ".tmp"));
        }
        finally { Directory.CreateDirectory(root.Path); }
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void WindowsCacheReadAndCorruptCacheDeleteFailuresStartEmpty(bool allowRead)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = new TemporaryDirectory();
        var file = Path.Combine(root.Path, "cache.json");
        File.WriteAllText(file, "legacy plaintext session");
        using var locked = File.Open(file, FileMode.Open, FileAccess.Read, allowRead ? FileShare.Read : FileShare.None);
        var cache = new CommonUniqueIdCache(new FileInfo(file));
        Assert.False(cache.HasValidCache("test"));
        Assert.True(File.Exists(file));
    }

    [Fact] public void WindowsReadOnlyCorruptCacheStartsEmpty()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = new TemporaryDirectory();
        var file = Path.Combine(root.Path, "cache.json");
        File.WriteAllText(file, "legacy plaintext session");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        try
        {
            var cache = new CommonUniqueIdCache(new FileInfo(file));
            Assert.False(cache.HasValidCache("test"));
            Assert.True(File.Exists(file));
        }
        finally { File.SetAttributes(file, FileAttributes.Normal); }
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LazyTraceFormattingFollowsTheExplicitTracePolicy(bool enabled)
    {
        var evaluated = false;
        var sinkCalled = false;
        using var scope = LaunchCancellationContext.Enter(
            CancellationToken.None,
            _ => sinkCalled = true,
            () => enabled);

        LaunchCancellationContext.WriteTrace(() =>
        {
            evaluated = true;
            return "synthetic-sensitive-detail";
        });

        Assert.Equal(enabled, evaluated);
        Assert.Equal(enabled, sinkCalled);
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

    [Fact] public void TraceLoggerRejectsSinkOpenFailure()
    {
        using var root = new TemporaryDirectory();
        // Opening a directory as a file fails on both Windows and Linux, even as an administrator.
        Assert.Throws<IOException>(() => FfxivTraceLogger.CreateTraceFileLogger(root.Path));
    }

    [Fact] public void TraceLoggerWritesToOpenedFile()
    {
        using var root = new TemporaryDirectory();
        var path = Path.Combine(root.Path, "trace.log");
        using (var logger = FfxivTraceLogger.CreateTraceFileLogger(path))
            logger.Information("synthetic trace event");
        Assert.Contains("synthetic trace event", File.ReadAllText(path));
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
