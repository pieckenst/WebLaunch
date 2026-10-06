using System.IO;
using El_Garnan_Plugin_Loader;
using El_Garnan_Plugin_Loader.Interfaces;
using El_Garnan_Plugin_Loader.Models;
using WebLaunch.Core;

namespace handlerlaunch;

public sealed class DesktopLaunchService(CoreFunctions plugins, Action<LaunchStatus> desktopStatus) : ILaunchService
{
    public async Task LaunchAsync(LaunchRequest request, IProgress<LaunchStatus> progress, CancellationToken cancellationToken)
    {
        request.Validate();
        if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(request.GamePath) || request.GamePath.StartsWith(@"\\"))
            throw new ArgumentException("Select a local absolute Windows installation path.");
        SafePath.RejectLinks(request.GamePath);
        var parameters = new GameLaunchParameters
        {
            GamePath = Path.GetFullPath(request.GamePath), DirectX11 = true, IsSteam = request.IsSteam, Language = 1, Region = 3,
            ExpansionLevel = Enumerable.Range(1, 5).Where(n => Directory.Exists(Path.Combine(request.GamePath, "game", "sqpack", "ex" + n))).DefaultIfEmpty(0).Max(),
            Credentials = new() { Username = request.Username, Password = request.Password, OTP = request.Otp }
        };
        try
        {
            var ok = await plugins.UsePluginAsync(request.Game + "-launcher", async plugin =>
            {
                if (plugin is ICancellableGamePlugin modern)
                    return await modern.LaunchAsync(parameters, new WebLaunch.Bridge.InlineProgress<LaunchStatus>(s => { progress.Report(s); desktopStatus(s); }), cancellationToken);
                // Compatibility values are handed only to trusted, in-process plugins, never ProcessStartInfo.Environment.
                parameters.EnvironmentVariables["FFXIV_USERNAME"] = request.Username;
                parameters.EnvironmentVariables["FFXIV_PASSWORD"] = request.Password;
                parameters.EnvironmentVariables["FFXIV_OTP"] = request.Otp;
                cancellationToken.ThrowIfCancellationRequested();
                return await plugin.LaunchGameAsync(parameters);
            }, cancellationToken);
            if (!ok) throw new InvalidOperationException("The game could not start.");
        }
        finally
        {
            parameters.Credentials.Password = ""; parameters.Credentials.OTP = "";
            parameters.EnvironmentVariables.Clear(); request.ClearCredentials();
        }
    }
}
