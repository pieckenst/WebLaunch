using System.Net;
using System.IO.Compression;
using System.Security.Cryptography;
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
    [Fact] public async Task VersionWriteFailureAfterCommitDoesNotLaunchStaleInstallation()
    {
        using var root = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(root.Path, ".weblaunch-version"), "1.0");
        // A directory at the temporary metadata path forces the real version store to fail.
        Directory.CreateDirectory(Path.Combine(root.Path, ".weblaunch-version.tmp"));
        using var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("game.txt").Open());
            writer.Write("updated game files");
        }
        var bytes = archive.ToArray();
        var checksum = Convert.ToHexString(MD5.HashData(bytes));
        var requests = 0;
        using var http = new HttpClient(new Transport(_ =>
        {
            HttpContent content = ++requests switch
            {
                1 => new StringContent("{\"version\":\"1.0\"}"),
                2 => new StringContent($"[{{\"update\":{{\"version\":\"2.0\",\"applies_to\":\"1.0\",\"file\":\"game.zip\",\"checksum\":\"{checksum}\"}}}}]"),
                3 => new ByteArrayContent(bytes),
                _ => throw new InvalidOperationException("Unexpected provider request")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://files.spellborn.org/game.zip")
            });
        }));
        var plugin = new SpellbornPlugin(new Logger(), http);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => plugin.LaunchAsync(new() { GamePath = root.Path }, new Progress<LaunchStatus>(), default));
        Assert.Equal("updated game files", File.ReadAllText(Path.Combine(root.Path, "game.txt")));
        Assert.Equal("1.0", File.ReadAllText(Path.Combine(root.Path, ".weblaunch-version")));
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
