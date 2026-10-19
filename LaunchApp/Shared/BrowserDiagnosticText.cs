using System.Text.RegularExpressions;

namespace LaunchApp.Shared;

internal static class BrowserDiagnosticText
{
    private static readonly Regex SensitiveAssignment = new(
        @"(?i)(?<key>""?(?:password|passwd|otp|token|sid|session(?:id)?|authorization|cookie|secret|username|credential(?:s)?|api[_ -]?key)""?)\s*[:=]\s*(?:(?:bearer|basic|digest|negotiate|ntlm|token)\s+)?(?:""(?:\\.|[^""\\])*(?:""|$)|'(?:\\.|[^'\\])*(?:'|$)|[^,\s;&}]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Url = new(
        @"(?i)\bhttps?://[^\s""'<>]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WindowsPath = new(
        @"(?i)(?:[a-z]:\\|\\\\)[^\s""'<>|]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Payload = new(
        @"(?i)\b(?:request|response)\s+(?:body|payload|content|headers?)\s*[:=].*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static string? SafeSummary(string message)
    {
        var firstLine = message.Replace('\r', '\n').Split('\n', 2)[0].Trim();
        firstLine = Regex.Replace(firstLine, @"^(?:JavaScript interop error|JSException|Error):\s*", "", RegexOptions.IgnoreCase);
        firstLine = Payload.Replace(firstLine, "request/response payload=[redacted]");
        firstLine = SensitiveAssignment.Replace(firstLine, match => match.Groups["key"].Value + "=[redacted]");
        firstLine = Url.Replace(firstLine, "[url redacted]");
        firstLine = WindowsPath.Replace(firstLine, "[path redacted]");
        firstLine = Regex.Replace(firstLine, @"\s{2,}", " ").Trim();
        if (firstLine.Length > 180) firstLine = firstLine[..180] + "…";
        return firstLine.Length == 0 ? null : firstLine;
    }
}
