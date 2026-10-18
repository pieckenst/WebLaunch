using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using WebLaunch.Bridge;

namespace handlerlaunch;

/// <summary>
/// Stores the desktop identity and browser trust as DPAPI-protected, atomically replaced files.
/// Corrupt or foreign-user cryptographic state fails closed and invalidates dependent trust;
/// ordinary storage-access failures are allowed to propagate instead of pretending persistence succeeded.
/// </summary>
public sealed class DesktopTrustStore : IBridgeTrustStore, IDisposable
{
    private readonly string directory;
    private HashSet<string> browsers = new(StringComparer.Ordinal);

    public ECDsa Identity { get; private set; }
    public DateTimeOffset? LegacyUntil { get; private set; }

    public DesktopTrustStore(string directory)
    {
        this.directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(this.directory);
        RemoveIncompleteTemporaryFiles();

        var keyFile = Path.Combine(this.directory, "identity.bin");
        if (FileExistsOrThrow(keyFile))
        {
            Identity = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            try
            {
                var key = ReadPrivate(keyFile);
                try
                {
                    Identity.ImportPkcs8PrivateKey(key, out var consumed);
                    if (consumed != key.Length)
                        throw new CryptographicException("Desktop identity contains trailing data.");
                }
                finally { CryptographicOperations.ZeroMemory(key); }
            }
            catch (CryptographicException)
            {
                // A corrupt key and a key protected for another Windows user are indistinguishable
                // here. In both cases rotate the identity and discard every trust record bound to it.
                InvalidateIdentityAndTrust(keyFile);
                Identity.Dispose();
                Identity = CreateAndPersistIdentity(keyFile);
            }
        }
        else
        {
            // A missing identity alongside trust data indicates a partial reset/interrupted write.
            // Do not preserve fingerprints that are no longer anchored to this desktop identity.
            if (FileExistsOrThrow(Path.Combine(this.directory, "browsers.bin")) ||
                FileExistsOrThrow(Path.Combine(this.directory, "legacy.bin")))
                ClearDependentTrust();

            Identity = CreateAndPersistIdentity(keyFile);
        }

        try
        {
            browsers = ReadJson<HashSet<string>>("browsers.bin") ?? new(StringComparer.Ordinal);
            browsers = new HashSet<string>(browsers, StringComparer.Ordinal);
            LegacyUntil = ReadJson<DateTimeOffset?>("legacy.bin");
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            // Corrupt or foreign-user trust records are not trusted. Fail closed but leave a valid
            // identity intact so the user can pair again. Unauthorized/I/O failures remain visible.
            ClearDependentTrust();
        }
    }

    public bool IsTrusted(string origin, string fingerprint) =>
        browsers.Contains(origin + "\n" + fingerprint);

    public void Trust(string origin, string fingerprint)
    {
        var replacement = new HashSet<string>(browsers, StringComparer.Ordinal)
        {
            origin + "\n" + fingerprint
        };
        SaveJson("browsers.bin", replacement);
        browsers = replacement;
    }

    public void ForgetAll()
    {
        var replacement = new HashSet<string>(StringComparer.Ordinal);
        SaveJson("browsers.bin", replacement);
        browsers = replacement;
    }

    public void SetLegacy(bool enabled)
    {
        var replacement = enabled ? DateTimeOffset.UtcNow.AddHours(24) : (DateTimeOffset?)null;
        SaveJson("legacy.bin", replacement);
        LegacyUntil = replacement;
    }

    private T? ReadJson<T>(string file)
    {
        var path = Path.Combine(directory, file);
        if (!FileExistsOrThrow(path)) return default;

        var bytes = ReadPrivate(path);
        try { return JsonSerializer.Deserialize<T>(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private void SaveJson<T>(string file, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        try { SavePrivate(Path.Combine(directory, file), bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private void ClearDependentTrust()
    {
        DeleteIfPresent(Path.Combine(directory, "browsers.bin"));
        DeleteIfPresent(Path.Combine(directory, "browsers.bin.tmp"));
        DeleteIfPresent(Path.Combine(directory, "legacy.bin"));
        DeleteIfPresent(Path.Combine(directory, "legacy.bin.tmp"));
        browsers = new HashSet<string>(StringComparer.Ordinal);
        LegacyUntil = null;
    }

    private void InvalidateIdentityAndTrust(string keyFile)
    {
        DeleteIfPresent(keyFile);
        DeleteIfPresent(keyFile + ".tmp");
        ClearDependentTrust();
    }

    private void RemoveIncompleteTemporaryFiles()
    {
        foreach (var name in new[] { "identity.bin.tmp", "browsers.bin.tmp", "legacy.bin.tmp" })
            DeleteIfPresent(Path.Combine(directory, name));
    }

    private static bool FileExistsOrThrow(string file)
    {
        try
        {
            using var stream = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static void DeleteIfPresent(string file)
    {
        // Do not use File.Exists here: it can turn some access failures into "missing" state.
        if (FileExistsOrThrow(file))
            File.Delete(file);
    }

    private ECDsa CreateAndPersistIdentity(string keyFile)
    {
        var identity = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var key = identity.ExportPkcs8PrivateKey();
        try { SavePrivate(keyFile, key); }
        catch { identity.Dispose(); throw; }
        finally { CryptographicOperations.ZeroMemory(key); }
        return identity;
    }

    private static byte[] ReadPrivate(string file) =>
        ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.CurrentUser);

    private static void SavePrivate(string file, byte[] bytes)
    {
        var temporary = file + ".tmp";
        try
        {
            var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            try { File.WriteAllBytes(temporary, protectedBytes); }
            finally { CryptographicOperations.ZeroMemory(protectedBytes); }
            File.Move(temporary, file, true);
        }
        finally
        {
            // A temporary file is never authoritative. Best-effort cleanup must not hide the write error.
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public void Dispose() => Identity.Dispose();
}
