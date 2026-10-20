using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using WebLaunch.Core;
using Xunit;
namespace WebLaunch.Tests;

public sealed class UpdaterTests
{
    private sealed class VersionStore : IInstalledVersionStore
    {
        public string? Version;
        public int FailWritesAfterMutation;
        public string? Read(string root) => Version;
        public void Write(string root, string version)
        {
            Version = version;
            if (FailWritesAfterMutation > 0)
            {
                FailWritesAfterMutation--;
                throw new IOException("Synthetic version metadata persistence failure after a partial write.");
            }
        }
    }
    private sealed class Provider(byte[] archive, string checksum) : HttpMessageHandler
    {
        public int Downloads;
        public bool FailDownload;
        public bool FailMetadata;
        public string UpdatesJson = "[]";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (FailMetadata && request.RequestUri!.AbsolutePath is "/latest.json" or "/updates.json")
                throw new HttpRequestException("Synthetic metadata transport failure");
            HttpContent content;
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/latest.json": content = new StringContent($"{{\"version\":\"1\",\"file\":\"game.zip\",\"checksum\":\"{checksum}\"}}", Encoding.UTF8, "application/json"); break;
                case "/updates.json": content = new StringContent(UpdatesJson, Encoding.UTF8, "application/json"); break;
                case "/game.zip":
                    Downloads++;
                    if (FailDownload) throw new HttpRequestException("Synthetic transport failure");
                    content = new ByteArrayContent(archive); break;
                default: throw new InvalidOperationException("Unexpected provider route");
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request });
        }
    }
    private static byte[] Archive()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        { using var writer = new StreamWriter(zip.CreateEntry("bin/client/game.txt").Open()); writer.Write("verified game data"); }
        return stream.ToArray();
    }
    [Fact] public async Task CorruptDownloadDoesNotInstallOrAdvanceVersion()
    {
        using var root = new TemporaryDirectory(); var version = new VersionStore();
        using var client = new HttpClient(new Provider(Archive(), new string('0', 32)));
        var updater = new SpellbornUpdater(client, version, new ArchiveInstaller());
        await Assert.ThrowsAsync<InvalidDataException>(() => updater.EnsureUpdatedAsync(root.Path, new Progress<LaunchStatus>(), default));
        Assert.Null(version.Version);
        Assert.False(File.Exists(Path.Combine(root.Path, "bin", "client", "game.txt")));
        Assert.False(Directory.Exists(Path.Combine(root.Path, ".weblaunch-transaction")));
    }
    [Fact] public async Task VerifiedDownloadInstallsAndUpdatesVersion()
    {
        using var root = new TemporaryDirectory(); var version = new VersionStore(); var bytes = Archive();
        using var client = new HttpClient(new Provider(bytes, Convert.ToHexString(MD5.HashData(bytes))));
        await new SpellbornUpdater(client, version, new ArchiveInstaller()).EnsureUpdatedAsync(root.Path, new Progress<LaunchStatus>(), default);
        Assert.Equal("1", version.Version); Assert.Equal("verified game data", File.ReadAllText(Path.Combine(root.Path, "bin/client/game.txt")));
    }
    [Fact] public async Task FailedVersionMetadataCommitRestoresMetadataAndPreviousFiles()
    {
        using var root = new TemporaryDirectory();
        var installedFile = Path.Combine(root.Path, "bin/client/game.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(installedFile)!);
        File.WriteAllText(installedFile, "previous installed data");

        var bytes = Archive();
        var checksum = Convert.ToHexString(MD5.HashData(bytes));
        var version = new VersionStore { Version = "1", FailWritesAfterMutation = 1 };
        var provider = new Provider(bytes, checksum)
        {
            UpdatesJson = $"[{{\"update\":{{\"version\":\"2\",\"file\":\"game.zip\",\"checksum\":\"{checksum}\",\"applies_to\":\"1\",\"enabled\":\"true\"}}}}]"
        };
        using var client = new HttpClient(provider);
        var updater = new SpellbornUpdater(client, version, new ArchiveInstaller());

        await Assert.ThrowsAsync<IOException>(() =>
            updater.EnsureUpdatedAsync(root.Path, new Progress<LaunchStatus>(), default));

        Assert.Equal("1", version.Version);
        Assert.Equal("previous installed data", File.ReadAllText(installedFile));
        Assert.False(Directory.Exists(Path.Combine(root.Path, ".weblaunch-transaction")));
        Assert.True(updater.InstallationStarted);
    }

    [Fact]
    public async Task InterruptedInstallIsRecoveredBeforeOfflineMetadataFallback()
    {
        using var root = new TemporaryDirectory();
        var installedFile = Path.Combine(root.Path, "bin", "client", "game.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(installedFile)!);
        File.WriteAllText(installedFile, "partially updated data");

        var transaction = Path.Combine(root.Path, ".weblaunch-transaction");
        var backupFile = Path.Combine(transaction, "backup", "bin", "client", "game.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
        File.WriteAllText(backupFile, "previous installed data");
        Directory.CreateDirectory(Path.Combine(transaction, "stage"));
        File.WriteAllText(Path.Combine(transaction, "journal.json"),
            "{\"Changes\":[{\"Relative\":\"bin/client/game.txt\",\"Existed\":true}],\"Committed\":false,\"HasVersionMetadata\":true,\"PreviousVersion\":\"1\"}");

        var version = new VersionStore { Version = "2" };
        using var client = new HttpClient(new Provider(Archive(), new string('0', 32)) { FailMetadata = true });
        var updater = new SpellbornUpdater(client, version, new ArchiveInstaller());

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            updater.EnsureUpdatedAsync(root.Path, new Progress<LaunchStatus>(), default));

        Assert.Equal("1", version.Version);
        Assert.Equal("previous installed data", File.ReadAllText(installedFile));
        Assert.False(Directory.Exists(transaction));
        Assert.False(updater.InstallationStarted); // Recovery completed; offline launch is now consistent and safe.
    }

    [Fact]
    public async Task FailedInterruptedInstallRecoveryDisablesOfflineFallback()
    {
        using var root = new TemporaryDirectory();
        var installedFile = Path.Combine(root.Path, "bin", "client", "game.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(installedFile)!);
        File.WriteAllText(installedFile, "possibly partial data");
        var transaction = Path.Combine(root.Path, ".weblaunch-transaction");
        Directory.CreateDirectory(transaction);
        File.WriteAllText(Path.Combine(transaction, "journal.json"), "{ malformed journal");

        var version = new VersionStore { Version = "1" };
        using var client = new HttpClient(new Provider(Archive(), new string('0', 32)) { FailMetadata = true });
        var updater = new SpellbornUpdater(client, version, new ArchiveInstaller());

        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() =>
            updater.EnsureUpdatedAsync(root.Path, new Progress<LaunchStatus>(), default));

        Assert.True(updater.InstallationStarted);
        Assert.Equal("possibly partial data", File.ReadAllText(installedFile));
        Assert.True(Directory.Exists(transaction));
    }

    [Fact] public async Task TransientFailuresHaveBoundedRetries()
    {
        using var root = new TemporaryDirectory(); var version = new VersionStore(); var provider = new Provider(Archive(), new string('0', 32)) { FailDownload = true };
        using var client = new HttpClient(provider);
        await Assert.ThrowsAsync<HttpRequestException>(() => new SpellbornUpdater(client, version, new ArchiveInstaller()).EnsureUpdatedAsync(root.Path, new Progress<LaunchStatus>(), default));
        Assert.Equal(3, provider.Downloads); Assert.Null(version.Version);
    }
}
