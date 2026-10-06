using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace WebLaunch.Core;

public interface IInstalledVersionStore
{
    string? Read(string root);
    void Write(string root, string version);
}
public sealed class SpellbornUpdater(HttpClient client, IInstalledVersionStore versions, ArchiveInstaller installer)
{
    private static readonly Uri BaseUri = new("https://files.spellborn.org/");
    private sealed class Release
    {
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("file")] public string File { get; set; } = "";
        [JsonPropertyName("checksum")] public string Checksum { get; set; } = "";
        [JsonPropertyName("applies_to")] public string AppliesTo { get; set; } = "";
        [JsonPropertyName("enabled")] public string Enabled { get; set; } = "true";
    }
    public async Task EnsureUpdatedAsync(string root, IProgress<LaunchStatus> progress, CancellationToken token)
    {
        var version = versions.Read(root);
        var latest = await client.GetFromJsonAsync<Release>(new Uri(BaseUri, "latest.json"), token) ?? throw new InvalidDataException("Missing release metadata.");
        if (version is null || version == "false")
        {
            await InstallReleaseAsync(latest, root, progress, token);
            version = latest.Version;
        }
        // Retain the provider's existing incremental update wire format.
        await using var updateStream = await client.GetStreamAsync(new Uri(BaseUri, "updates.json"), token);
        using var json = await JsonDocument.ParseAsync(updateStream, cancellationToken: token);
        var visited = new HashSet<string>(StringComparer.Ordinal) { version };
        for (var i = 0; i < 100; i++)
        {
            var next = json.RootElement.EnumerateArray().Select(e => e.GetProperty("update").Deserialize<Release>())
                .FirstOrDefault(r => r is not null && r.AppliesTo == version && r.Enabled == "true");
            if (next is null) return;
            if (string.IsNullOrWhiteSpace(next.Version) || !visited.Add(next.Version)) throw new InvalidDataException("Invalid update sequence.");
            await InstallReleaseAsync(next, root, progress, token);
            version = next.Version;
        }
        throw new InvalidDataException("Too many incremental updates.");
    }
    private async Task InstallReleaseAsync(Release release, string root, IProgress<LaunchStatus> progress, CancellationToken token)
    {
        SafePath.Resolve(root, release.File);
        if (string.IsNullOrWhiteSpace(release.Version) || release.Checksum.Length != 32 || !release.Checksum.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("Invalid release metadata.");
        var url = new Uri(BaseUri, release.File.Replace('\\', '/'));
        if (url.Scheme != "https" || url.Host != BaseUri.Host) throw new InvalidDataException("Invalid download origin.");
        var temporary = Path.Combine(Path.GetTempPath(), "weblaunch-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            progress.Report(new("updating", "Downloading Chronicles of Spellborn…"));
            await DownloadAsync(url, temporary, token);
            await using (var stream = File.OpenRead(temporary))
            {
                var hash = await MD5.HashDataAsync(stream, token); // Provider format, integrity only; HTTPS authenticates transport.
                if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(release.Checksum)))
                    throw new InvalidDataException("Download checksum mismatch.");
            }
            progress.Report(new("updating", "Installing verified game files…"));
            await installer.InstallAsync(temporary, root, token, () => versions.Write(root, release.Version));
        }
        finally { File.Delete(temporary); }
    }
    private async Task DownloadAsync(Uri url, string file, CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                if (response.RequestMessage?.RequestUri is not { Scheme: "https" } final || final.Host != BaseUri.Host)
                    throw new InvalidDataException("Unexpected download redirect.");
                await using var input = await response.Content.ReadAsStreamAsync(token);
                await using var output = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None);
                await input.CopyToAsync(output, token);
                return;
            }
            catch (HttpRequestException) when (attempt < 2) { await Task.Delay(TimeSpan.FromSeconds(attempt + 1), token); }
        }
    }
}
