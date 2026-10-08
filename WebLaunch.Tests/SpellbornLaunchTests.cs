using System.Net;
using El_Garnan_Plugin_Loader.Interfaces;
using GamePlugins.Spellborn;
using WebLaunch.Core;
using Xunit;

namespace WebLaunch.Tests;

public sealed class SpellbornLaunchTests
{
    private sealed class Logger : ILogger
    {
        public void Debug(string message) { }
        public void Information(string message) { }
        public void Warning(string message) { }
        public void Error(string message) { }
        public void Error(string message, Exception exception) { }
    }
    private sealed class Transport(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(token);
    }
    [Theory][InlineData(null)][InlineData("")][InlineData("false")][InlineData("1.0")]
    public async Task FailedUpdateOnlyContinuesForRecordedInstallation(string? version)
    {
        using var root = new TemporaryDirectory();
        if (version is not null) File.WriteAllText(Path.Combine(root.Path, ".weblaunch-version"), version);
        using var http = new HttpClient(new Transport(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
        var plugin = new SpellbornPlugin(new Logger(), http);
        var error = await Record.ExceptionAsync(() => plugin.LaunchAsync(new() { GamePath = root.Path }, new Progress<LaunchStatus>(), default));
        // A recorded installation reaches executable validation; a first install must retain the update failure.
        if (version == "1.0") Assert.IsType<FileNotFoundException>(error);
        else Assert.IsType<HttpRequestException>(error);
    }
    [Fact] public async Task InstalledVersionDoesNotSwallowCancellation()
    {
        using var root = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(root.Path, ".weblaunch-version"), "1.0");
        using var http = new HttpClient(new Transport(async token => { await Task.Delay(Timeout.Infinite, token); return new(HttpStatusCode.OK); }));
        var plugin = new SpellbornPlugin(new Logger(), http);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.LaunchAsync(new() { GamePath = root.Path }, new Progress<LaunchStatus>(), cancellation.Token));
    }
}
