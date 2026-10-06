using System.Diagnostics;
using LibDalamud.Common.Dalamud;

namespace LibLaunchSupport;

// Original public API retained as adapters to the actively maintained implementation.
public enum DpiAwareness { Aware, Unaware }
public enum LoginAction { Game, GameNoDalamud, GameNoLaunch, Repair, Fake }
public interface IGameRunner
{
    Process? Start(string path, string workingDirectory, string arguments, IDictionary<string, string> environment, DpiAwareness dpiAwareness);
}
public static class NativeAclFix
{
    public static Process LaunchGame(string workingDir, string exePath, string arguments, IDictionary<string, string> envVars, DpiAwareness dpiAwareness, Action<Process> beforeResume) =>
        CoreLibLaunchSupport.NativeAclFix.LaunchGame(workingDir, exePath, arguments, envVars, (CoreLibLaunchSupport.DpiAwareness)dpiAwareness, beforeResume);
}
public class WindowsGameRunner : IGameRunner
{
    private readonly CoreLibLaunchSupport.WindowsGameRunner runner;
    public WindowsGameRunner(DalamudLauncher dalamudLauncher, bool dalamudOk, DirectoryInfo dotnetRuntimePath) => runner = new(dalamudLauncher, dalamudOk, dotnetRuntimePath);
    public Process Start(string path, string workingDirectory, string arguments, IDictionary<string, string> environment, DpiAwareness dpiAwareness) =>
        runner.Start(path, workingDirectory, arguments, environment, (CoreLibLaunchSupport.DpiAwareness)dpiAwareness);
}
public class networklogic
{
    public static Process LaunchGame(string gamePath, string realsid, int language, bool dx11, int expansionlevel, bool isSteam, int region) =>
        CoreLibLaunchSupport.networklogic.LaunchGame(gamePath, realsid, language, dx11, expansionlevel, isSteam, region);
    public static string GetRealSid(string gamePath, string username, string password, string otp, bool isSteam) =>
        CoreLibLaunchSupport.networklogic.GetRealSid(gamePath, username, password, otp, isSteam);
    public static string GetSid(string username, string password, string otp, bool isSteam) =>
        CoreLibLaunchSupport.networklogic.GetSidAsync(username, password, otp, isSteam).GetAwaiter().GetResult();
    public static bool GetGateStatus() => CoreLibLaunchSupport.networklogic.CheckGateStatus();
}
