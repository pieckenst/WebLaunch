namespace WebLaunch.Core;

/// <summary>Credential-free protocol bootstrap. It never contains executable arguments or paths.</summary>
public sealed record BootstrapRequest(string Mode)
{
    public static bool TryParse(string value, out BootstrapRequest? request)
    {
        request = null;
        const string prefix = "HandleWebRequest:connect?";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        if (value.Length > 128 || value.Any(char.IsControl)) throw new ArgumentException("Invalid bootstrap link.");
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in value[prefix.Length..].Split('&'))
        {
            var parts = field.Split('=');
            if (parts.Length != 2 || !fields.TryAdd(parts[0], parts[1])) throw new ArgumentException("Invalid bootstrap field.");
        }
        if (fields.GetValueOrDefault("v") != "2" || fields.Keys.Any(k => k is not ("v" or "mode")))
            throw new ArgumentException("Unsupported bootstrap link.");
        var mode = fields.GetValueOrDefault("mode", "gui");
        if (mode is not ("gui" or "console")) throw new ArgumentException("Unsupported desktop mode.");
        request = new(mode);
        return true;
    }
}
