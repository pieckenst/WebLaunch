namespace WebLaunch.Core;

// Deliberately not a record: synthesized ToString must never expose credentials.
public sealed class LaunchRequest
{
    public int Version { get; set; } = 2;
    public string RequestId { get; set; } = Guid.NewGuid().ToString("N");
    public string Game { get; set; } = "ffxiv";
    public string GamePath { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Otp { get; set; } = "";
    public bool IsSteam { get; set; }
    public void Validate()
    {
        if (Version != 2 || !Guid.TryParseExact(RequestId, "N", out _) || Game is not ("ffxiv" or "spellborn"))
            throw new ArgumentException("Unsupported launch request.");
        ValidateGamePath(GamePath);
        if (Username is null || Password is null || Otp is null || Username.Length > 256 || Password.Length > 1024 || Otp.Length > 16)
            throw new ArgumentException("Login fields are too long.");
        if (Game == "ffxiv" && (string.IsNullOrWhiteSpace(Username) || string.IsNullOrEmpty(Password)))
            throw new ArgumentException("Enter your username and password.");
        if (Otp.Length != 0 && (Otp.Length != 6 || !Otp.All(char.IsAsciiDigit)))
            throw new ArgumentException("The optional one-time password must contain six digits.");
    }
    public static void ValidateGamePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.Any(char.IsControl))
            throw new ArgumentException("Choose a valid game installation folder.");
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/') ||
            path[2..].Any(c => c is ':' or '*' or '?' or '"' or '<' or '>' or '|') ||
            path[3..].Replace('\\', '/').Split('/').Any(part => part is ".." or "."))
            throw new ArgumentException("Use an absolute local Windows folder, such as C:\\Games.");
    }
    public void ClearCredentials() => (Username, Password, Otp) = ("", "", "");
    public override string ToString() => "LaunchRequest (credentials redacted)";
}

public sealed record LaunchStatus(string State, string Message, bool Completed = false, bool Success = false);
public interface ILaunchService
{
    Task LaunchAsync(LaunchRequest request, IProgress<LaunchStatus> progress, CancellationToken cancellationToken);
}
