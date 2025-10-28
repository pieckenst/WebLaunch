using System;
using System.IO;
using System.Numerics;
using System.Reflection;

using ImGuiNET;

namespace El_Garnan_Plugin_Loader.Rendering
{
    public static class ImGuiHelpers
    {
        public static Vector2 ViewportSize => ImGui.GetIO().DisplaySize;
        public static float GlobalScale => ImGui.GetIO().FontGlobalScale;

        public static void TextWrapped(string text)
        {
            ImGui.PushTextWrapPos();
            ImGui.TextUnformatted(text);
            ImGui.PopTextWrapPos();
        }

        public static void CenteredText(string text)
        {
            CenterCursorForText(text);
            ImGui.TextUnformatted(text);
        }

        public static void CenterCursorForText(string text)
        {
            var textWidth = ImGui.CalcTextSize(text).X;
            CenterCursorFor((int)textWidth);
        }

        public static void CenterCursorFor(int itemWidth)
        {
            var window = (int)ImGui.GetWindowWidth();
            ImGui.SetCursorPosX(window / 2 - itemWidth / 2);
        }

        public static void AddTooltip(string text)
        {
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(text);
                ImGui.EndTooltip();
            }
        }
    }

    internal static class ImGuiResourceLoader
    {
        private const string ResourceRoot = "El_Garnan_Plugin_Loader";
        private static readonly Assembly Assembly = typeof(ImGuiResourceLoader).Assembly;

       internal static byte[] LoadBytes(params string[] pathSegments)
{
    if (pathSegments == null || pathSegments.Length == 0)
    {
        throw new ArgumentException("At least one path segment is required.", nameof(pathSegments));
    }

    var relativePath = string.Join("/", pathSegments);
    Console.WriteLine($"[DEBUG] Attempting to load resource: {relativePath}");

    // Log all available resources for debugging
    var allResources = Assembly.GetManifestResourceNames();
    Console.WriteLine($"[DEBUG] Available resources in assembly ({allResources.Length} total):");
    foreach (var resource in allResources)
    {
        Console.WriteLine($"  - {resource}");
    }

    // Try different resource name patterns
    var possibleResourceNames = new[]
    {
        // Try with full path including Resources/fonts
        $"{ResourceRoot}.Resources.fonts.{Path.GetFileName(relativePath)}",
        // Try with just the filename
        $"{ResourceRoot}.{Path.GetFileName(relativePath)}",
        // Try with the exact relative path
        $"{ResourceRoot}.{relativePath.Replace('/', '.')}",
        // Try just the filename without any prefix
        Path.GetFileName(relativePath)
    };

    foreach (var resourceName in possibleResourceNames.Distinct())
    {
        Console.WriteLine($"[DEBUG] Trying resource name: {resourceName}");
        using (var stream = Assembly.GetManifestResourceStream(resourceName))
        {
            if (stream != null)
            {
                Console.WriteLine($"[DEBUG] Successfully found embedded resource: {resourceName}");
                using (var ms = new MemoryStream())
                {
                    stream.CopyTo(ms);
                    var result = ms.ToArray();
                    Console.WriteLine($"[DEBUG] Loaded {result.Length} bytes from embedded resource");
                    return result;
                }
            }
        }
    }

    // If not found in resources, try the file system
    var filePath = Path.Combine(AppContext.BaseDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
    Console.WriteLine($"[DEBUG] Trying to load from file system: {filePath}");
    
    if (File.Exists(filePath))
    {
        var fileBytes = File.ReadAllBytes(filePath);
        Console.WriteLine($"[DEBUG] Successfully loaded {fileBytes.Length} bytes from file system");
        return fileBytes;
    }

    var error = $"Unable to locate resource '{relativePath}' in assembly (tried: {string.Join(", ", possibleResourceNames)}) or file system.";
    Console.WriteLine($"[ERROR] {error}");
    throw new FileNotFoundException(error);
}

        private static string BuildResourceName(string relativePath)
        {
            var sanitized = relativePath
                .Replace('\\', '.')
                .Replace('/', '.');
            return $"{ResourceRoot}.{sanitized}";
        }

        private static byte[] ReadAllBytes(Stream stream)
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }
    }
}
