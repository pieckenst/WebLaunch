using System.IO.Compression;
using WebLaunch.Core;
using Xunit;
namespace WebLaunch.Tests;

public sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "weblaunch-test-" + Guid.NewGuid());
    public TemporaryDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, true);
}
public sealed class FilesystemTests
{
    [Theory]
    [InlineData("../escape")][InlineData("..\\escape")][InlineData("C:\\escape")][InlineData("/escape")][InlineData("\\\\server\\share")]
    [InlineData("a:stream")][InlineData("a/CON.txt")][InlineData("a/../b")][InlineData("a./b")][InlineData("a /b")]
    public void UnsafePathsAreRejected(string relative)
    {
        using var root = new TemporaryDirectory();
        Assert.Throws<InvalidDataException>(() => SafePath.Resolve(root.Path, relative));
    }
    [Fact] public void LinksCannotEscapeRoot()
    {
        using var root = new TemporaryDirectory(); using var outside = new TemporaryDirectory();
        try { Directory.CreateSymbolicLink(System.IO.Path.Combine(root.Path, "link"), outside.Path); }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows()) { return; }
        Assert.Throws<InvalidDataException>(() => SafePath.Resolve(root.Path, "link/file"));
    }
    private static string Zip(string folder, params (string Name, string Content)[] files)
    {
        var path = System.IO.Path.Combine(folder, Guid.NewGuid() + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in files) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(content); }
        return path;
    }
    [Fact] public async Task TraversalDoesNotModifyExistingInstallation()
    {
        using var root = new TemporaryDirectory(); using var downloads = new TemporaryDirectory();
        File.WriteAllText(System.IO.Path.Combine(root.Path, "game.txt"), "old");
        var zip = Zip(downloads.Path, ("game.txt", "new"), ("../outside.txt", "bad"));
        var version = "old";
        await Assert.ThrowsAsync<InvalidDataException>(() => new ArchiveInstaller().InstallAsync(zip, root.Path, default, () => version = "new"));
        Assert.Equal("old", File.ReadAllText(System.IO.Path.Combine(root.Path, "game.txt"))); Assert.Equal("old", version);
    }
    [Fact] public async Task MidCommitFailureRollsBackReplacedFiles()
    {
        using var root = new TemporaryDirectory(); using var downloads = new TemporaryDirectory();
        File.WriteAllText(System.IO.Path.Combine(root.Path, "a.txt"), "old");
        Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "blocked"));
        var zip = Zip(downloads.Path, ("a.txt", "new"), ("blocked", "collision"));
        var version = "old";
        await Assert.ThrowsAnyAsync<IOException>(() => new ArchiveInstaller().InstallAsync(zip, root.Path, default, () => version = "new"));
        Assert.Equal("old", File.ReadAllText(System.IO.Path.Combine(root.Path, "a.txt"))); Assert.Equal("old", version);
    }
    [Fact] public async Task CancelledInstallDoesNotCommitVersion()
    {
        using var root = new TemporaryDirectory(); using var downloads = new TemporaryDirectory();
        var zip = Zip(downloads.Path, ("game.txt", "new")); var committed = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ArchiveInstaller().InstallAsync(zip, root.Path, new CancellationToken(true), () => committed = true));
        Assert.False(committed); Assert.False(File.Exists(System.IO.Path.Combine(root.Path, "game.txt")));
    }
    [Fact] public async Task SuccessfulInstallCommitsFilesThenVersion()
    {
        using var root = new TemporaryDirectory(); using var downloads = new TemporaryDirectory();
        var zip = Zip(downloads.Path, ("bin/client/game.txt", "new")); var committed = false;
        await new ArchiveInstaller().InstallAsync(zip, root.Path, default, () => { Assert.Equal("new", File.ReadAllText(System.IO.Path.Combine(root.Path, "bin/client/game.txt"))); committed = true; });
        Assert.True(committed);
    }
    [Fact] public async Task InterruptedReplacementRecoversBeforeNextArchiveIsValidated()
    {
        using var root = new TemporaryDirectory(); using var downloads = new TemporaryDirectory();
        var transaction = Path.Combine(root.Path, ".weblaunch-transaction");
        Directory.CreateDirectory(Path.Combine(transaction, "backup"));
        Directory.CreateDirectory(Path.Combine(transaction, "stage"));
        File.WriteAllText(Path.Combine(root.Path, "game.txt"), "interrupted-new");
        File.WriteAllText(Path.Combine(transaction, "backup/game.txt"), "old");
        File.WriteAllText(Path.Combine(transaction, "journal.json"), """{"Changes":[{"Relative":"game.txt","Existed":true}],"Committed":false}""");
        var invalid = Zip(downloads.Path, ("../escape", "bad"));
        await Assert.ThrowsAsync<InvalidDataException>(() => new ArchiveInstaller().InstallAsync(invalid, root.Path, default));
        Assert.Equal("old", File.ReadAllText(Path.Combine(root.Path, "game.txt")));
        Assert.False(Directory.Exists(transaction));
    }

}
