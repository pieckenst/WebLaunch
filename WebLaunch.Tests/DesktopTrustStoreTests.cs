using System.Security.Cryptography;
using handlerlaunch;
using WebLaunch.Core;
using Xunit;

namespace WebLaunch.Tests;

public sealed class DesktopTrustStoreTests
{
    private const string Origin = "https://pieckenst.github.io";

    private static string Fingerprint(DesktopTrustStore store) =>
        BridgeCrypto.Fingerprint(Convert.ToBase64String(store.Identity.ExportSubjectPublicKeyInfo()));

    [Fact]
    public void CorruptIdentityRotatesKeyAndInvalidatesBrowserAndLegacyTrust()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = new TemporaryDirectory();
        var identityPath = Path.Combine(root.Path, "identity.bin");
        string previousFingerprint;

        using (var original = new DesktopTrustStore(root.Path))
        {
            previousFingerprint = Fingerprint(original);
            original.Trust(Origin, previousFingerprint);
            original.SetLegacy(true);
        }

        // Invalid DPAPI bytes also model a foreign-user or partially written identity file.
        File.WriteAllBytes(identityPath, [0x01, 0x02, 0x03]);
        using var recovered = new DesktopTrustStore(root.Path);

        Assert.NotEqual(previousFingerprint, Fingerprint(recovered));
        Assert.False(recovered.IsTrusted(Origin, previousFingerprint));
        Assert.Null(recovered.LegacyUntil);
        Assert.True(File.Exists(identityPath));
        Assert.False(File.Exists(Path.Combine(root.Path, "browsers.bin")));
        Assert.False(File.Exists(Path.Combine(root.Path, "legacy.bin")));
    }

    [Fact]
    public void MissingIdentityInvalidatesTrustRecordsInsteadOfReusingThem()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = new TemporaryDirectory();
        string previousFingerprint;

        using (var original = new DesktopTrustStore(root.Path))
        {
            previousFingerprint = Fingerprint(original);
            original.Trust(Origin, previousFingerprint);
            original.SetLegacy(true);
        }

        File.Delete(Path.Combine(root.Path, "identity.bin"));
        using var recovered = new DesktopTrustStore(root.Path);

        Assert.NotEqual(previousFingerprint, Fingerprint(recovered));
        Assert.False(recovered.IsTrusted(Origin, previousFingerprint));
        Assert.Null(recovered.LegacyUntil);
    }

    [Fact]
    public void CorruptBrowserTrustFailsClosedAndClearsLegacyAuthorization()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = new TemporaryDirectory();
        string identityFingerprint;

        using (var original = new DesktopTrustStore(root.Path))
        {
            identityFingerprint = Fingerprint(original);
            original.Trust(Origin, identityFingerprint);
            original.SetLegacy(true);
        }

        File.WriteAllBytes(Path.Combine(root.Path, "browsers.bin"), [0x01, 0x02, 0x03]);
        using var recovered = new DesktopTrustStore(root.Path);

        Assert.Equal(identityFingerprint, Fingerprint(recovered));
        Assert.False(recovered.IsTrusted(Origin, identityFingerprint));
        Assert.Null(recovered.LegacyUntil);
        Assert.False(File.Exists(Path.Combine(root.Path, "browsers.bin")));
        Assert.False(File.Exists(Path.Combine(root.Path, "legacy.bin")));
    }

    [Fact]
    public void FailedTrustPersistenceDoesNotAppearDurableInMemory()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = new TemporaryDirectory();
        using var store = new DesktopTrustStore(root.Path);
        var fingerprint = Fingerprint(store);
        var destination = Path.Combine(root.Path, "browsers.bin");
        Directory.CreateDirectory(destination);

        Assert.ThrowsAny<Exception>(() => store.Trust(Origin, fingerprint));

        Assert.False(store.IsTrusted(Origin, fingerprint));
        Assert.False(File.Exists(destination + ".tmp"));
    }

    [Fact]
    public void StaleTemporaryWritesAreDiscardedWithoutDiscardingCommittedIdentity()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = new TemporaryDirectory();
        string fingerprint;
        using (var original = new DesktopTrustStore(root.Path))
            fingerprint = Fingerprint(original);

        foreach (var name in new[] { "identity.bin.tmp", "browsers.bin.tmp", "legacy.bin.tmp" })
            File.WriteAllText(Path.Combine(root.Path, name), "incomplete temporary value");

        using var reopened = new DesktopTrustStore(root.Path);
        Assert.Equal(fingerprint, Fingerprint(reopened));
        Assert.All(new[] { "identity.bin.tmp", "browsers.bin.tmp", "legacy.bin.tmp" },
            name => Assert.False(File.Exists(Path.Combine(root.Path, name))));
    }
}
