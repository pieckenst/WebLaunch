using System.IO;

namespace WebLaunch.Core;

/// <summary>
/// Stores Spellborn installation version metadata using an atomically replaced marker file.
/// The historical per-user registry value is read only as a compatibility fallback.
/// </summary>
public sealed class SpellbornVersionStore : IInstalledVersionStore
{
    public string? Read(string root)
    {
        var file = SafePath.Resolve(root, ".weblaunch-version");
        if (File.Exists(file)) return File.ReadAllText(file).Trim();

        if (OperatingSystem.IsWindows())
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\The Chronicles of Spellborn");
            if (key?.GetValue("installPath") is string previous && !string.IsNullOrWhiteSpace(previous))
            {
                string previousRoot;
                try { previousRoot = Path.GetFullPath(previous); }
                catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return null; }

                if (string.Equals(previousRoot, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                    return key.GetValue("installedVersion") as string;
            }
        }

        return null;
    }

    public void Write(string root, string version)
    {
        root = Path.GetFullPath(root);
        var file = SafePath.Resolve(root, ".weblaunch-version");
        var temporary = file + ".tmp";
        try
        {
            File.WriteAllText(temporary, version);
            File.Move(temporary, file, overwrite: true);

            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\The Chronicles of Spellborn")
                    ?? throw new IOException("Could not open the Spellborn installation metadata key.");
                key.SetValue("installPath", root);
                key.SetValue("installedVersion", version);
            }
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
