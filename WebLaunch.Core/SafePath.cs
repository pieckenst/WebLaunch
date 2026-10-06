namespace WebLaunch.Core;

public static class SafePath
{
    public static string Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Any(char.IsControl) || relative.Contains(':') ||
            relative.StartsWith('/') || relative.StartsWith('\\') || relative.Contains('*') || relative.Contains('?'))
            throw new InvalidDataException("Invalid relative file path.");
        var segments = relative.Replace('\\', '/').Split('/');
        foreach (var part in segments)
        {
            var device = part.Split('.')[0].ToUpperInvariant();
            if (part is ".." or "." || part.EndsWith(' ') || part.EndsWith('.') ||
                device is "CON" or "PRN" or "AUX" or "NUL" ||
                (device.Length == 4 && (device.StartsWith("COM") || device.StartsWith("LPT")) && char.IsAsciiDigit(device[3])))
                throw new InvalidDataException("Invalid relative file path.");
        }
        var fullRoot = Path.GetFullPath(root);
        RejectLinks(fullRoot);
        var full = Path.GetFullPath(Path.Combine(fullRoot, Path.Combine(segments)));
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("File path escapes the installation folder.");
        RejectLinks(full);
        return full;
    }

    public static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Links and reparse points are not allowed in installation paths.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
