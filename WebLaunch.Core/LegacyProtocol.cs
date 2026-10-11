using System.Security.Cryptography;
using System.Text;

namespace WebLaunch.Core;

public static class LegacyProtocol
{
    public static LaunchRequest Parse(string input, DateTimeOffset? allowedUntil, DateTimeOffset now)
    {
        if (allowedUntil is null || allowedUntil <= now || allowedUntil > now.AddHours(24))
            throw new InvalidOperationException("Legacy links are disabled. Connect from the updated website or explicitly enable legacy mode in the desktop launcher.");
        const string prefix = "HandleWebRequest:HandleReqLaunch?";
        if (input.Length > 8192 || !input.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || input.Any(char.IsControl))
            throw new ArgumentException("Invalid launch link.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        // The original grammar uses a bare '?' after the game selector, then ':?' separators.
        foreach (var part in input[prefix.Length..].Replace(":?", "?").Split('?'))
        {
            var index = part.IndexOf('=');
            if (index < 1 || !values.TryAdd(part[..index], Decode(part[(index + 1)..])))
                throw new ArgumentException("Invalid or repeated launch parameter.");
        }
        string[] allowed = ["ffxivhandle", "spellbornhandle", "login", "pass", "hash", "otp", "gamepath", "issteam"];
        if (values.Keys.Any(k => !allowed.Contains(k)) || values.ContainsKey("ffxivhandle") == values.ContainsKey("spellbornhandle"))
            throw new ArgumentException("Unsupported launch parameter.");
        var ffxiv = values.ContainsKey("ffxivhandle");
        if (values[ffxiv ? "ffxivhandle" : "spellbornhandle"] != "yes") throw new ArgumentException("Invalid game selector.");
        var request = new LaunchRequest { Game = ffxiv ? "ffxiv" : "spellborn", GamePath = values.GetValueOrDefault("gamepath", "") };
        if (ffxiv)
        {
            var hash = values.GetValueOrDefault("hash", "");
            if (hash.Length != 64 || !hash.All(char.IsAsciiHexDigit)) throw new ArgumentException("Invalid legacy payload.");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(values.GetValueOrDefault("pass", "")); }
            catch (FormatException) { throw new ArgumentException("Invalid legacy payload."); }
            var key = Encoding.UTF8.GetBytes(hash[..16]);
            try
            {
                for (var i = 0; i < bytes.Length; i++) bytes[i] ^= key[i % key.Length];
                request.Password = new UTF8Encoding(false, true).GetString(bytes);
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(hash)))
                    throw new ArgumentException("Invalid legacy payload.");
            }
            finally { CryptographicOperations.ZeroMemory(bytes); CryptographicOperations.ZeroMemory(key); }
            request.Username = values.GetValueOrDefault("login", "");
            request.Otp = values.GetValueOrDefault("otp", "");
            var steam = values.GetValueOrDefault("issteam", "no");
            if (steam is not ("yes" or "no")) throw new ArgumentException("Invalid Steam option.");
            request.IsSteam = steam == "yes";
        }
        request.Validate();
        return request;
    }

    private static string Decode(string value)
    {
        for (int i = 0; i < value.Length; i++)
            if (value[i] == '%' && (i + 2 >= value.Length || !char.IsAsciiHexDigit(value[++i]) || !char.IsAsciiHexDigit(value[++i])))
                throw new ArgumentException("Invalid URL encoding.");
        var decoded = Uri.UnescapeDataString(value);
        if (decoded.Any(char.IsControl)) throw new ArgumentException("Invalid launch parameter.");
        return decoded;
    }
}
