using System.Collections.Concurrent;
using El_Garnan_Plugin_Loader.Base;
using El_Garnan_Plugin_Loader.Interfaces;
using El_Garnan_Plugin_Loader.Models;
using Xunit;

namespace WebLaunch.Tests;

public sealed class PluginNotificationTests
{
    private sealed class Logger : ILogger
    {
        public ConcurrentBag<string> Messages { get; } = [];
        public void Debug(string message) { }
        public void Information(string message) => Messages.Add(message);
        public void Warning(string message) => Messages.Add(message);
        public void Error(string message) => Messages.Add(message);
        public void Error(string message, Exception exception) => Messages.Add(message);
    }

    private sealed class NotificationPlugin(ILogger logger) : GamePluginBase(logger)
    {
        public override string PluginId => "notification-test";
        public override string Name => "Notification test";
        public override string Description => "Test-only notification producer.";
        public override string TargetApplication => "test.exe";
        public override Version Version => new(1, 0, 0);
        public override IReadOnlyCollection<PluginDependency> Dependencies => [];
        protected override Task InitializeInternalAsync() => Task.CompletedTask;
        protected override Task<bool> LaunchGameInternalAsync(GameLaunchParameters parameters) => Task.FromResult(true);
        protected override Task ShutdownInternalAsync() => Task.CompletedTask;
        public void Publish(int value) => ShowNotification("Concurrent", value.ToString());
    }

    [Fact]
    public void ConcurrentNotificationProducersDoNotCorruptTheInternalQueue()
    {
        var plugin = new NotificationPlugin(new Logger());
        Parallel.For(0, 2_000, plugin.Publish);

        Assert.Equal(2_000, plugin.NotificationQueue.Count);
        Assert.Equal(2_000, plugin.NotificationQueue.Select(n => n.Message).Distinct().Count());
    }
}
