using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using WebLaunch.Bridge;

namespace handlerlaunch;

public sealed class DesktopTrustStore : IBridgeTrustStore, IDisposable
{
    private readonly string directory;
    private readonly HashSet<string> browsers;
    public ECDsa Identity { get; }
    public DateTimeOffset? LegacyUntil { get; private set; }
    public DesktopTrustStore(string directory)
    {
        this.directory = directory;
        Directory.CreateDirectory(directory);
        Identity = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keyFile = Path.Combine(directory, "identity.bin");
        if (File.Exists(keyFile))
        {
            var key = ReadPrivate(keyFile);
            try { Identity.ImportPkcs8PrivateKey(key, out _); }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        else
        {
            var key = Identity.ExportPkcs8PrivateKey();
            try { SavePrivate(keyFile, key); }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        browsers = ReadJson<HashSet<string>>("browsers.bin") ?? [];
        LegacyUntil = ReadJson<DateTimeOffset?>("legacy.bin");
    }
    public bool IsTrusted(string origin, string fingerprint) => browsers.Contains(origin + "\n" + fingerprint);
    public void Trust(string origin, string fingerprint) { browsers.Add(origin + "\n" + fingerprint); SaveJson("browsers.bin", browsers); }
    public void ForgetAll() { browsers.Clear(); SaveJson("browsers.bin", browsers); }
    public void SetLegacy(bool enabled) { LegacyUntil = enabled ? DateTimeOffset.UtcNow.AddHours(24) : null; SaveJson("legacy.bin", LegacyUntil); }
    private T? ReadJson<T>(string file)
    {
        var path = Path.Combine(directory, file);
        if (!File.Exists(path)) return default;
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
    private static byte[] ReadPrivate(string file) => ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.CurrentUser);
    private static void SavePrivate(string file, byte[] bytes)
    {
        var temporary = file + ".tmp";
        File.WriteAllBytes(temporary, ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
        File.Move(temporary, file, true);
    }
    public void Dispose() => Identity.Dispose();
}
