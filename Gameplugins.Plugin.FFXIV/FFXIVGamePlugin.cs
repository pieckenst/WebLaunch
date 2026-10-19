using CoreLibLaunchSupport;
using El_Garnan_Plugin_Loader.Base;
using El_Garnan_Plugin_Loader.Interfaces;
using El_Garnan_Plugin_Loader.Models;
using ImGuiNET;
using WebLaunch.Core;

namespace GamePlugins.FFXIV;

public class FFXIVGamePlugin : GamePluginBase, ICancellableGamePlugin
{
    private CancellationTokenSource? lifetime;
    private Task? statusPolling;
    private string serverStatus = "Checking server status…";
    public override string PluginId => "ffxiv-launcher";
    public override string Name => "FFXIV Game Launcher";
    public override string Description => "Launches Final Fantasy XIV with authentication";
    public override string TargetApplication => "ffxiv_dx11.exe";
    public override Version Version => new(1, 1, 0);
    public override bool SupportsImGui => true;
    public override IReadOnlyCollection<PluginDependency> Dependencies => [];
    public FFXIVGamePlugin(ILogger logger) : base(logger) { }

    protected override Task InitializeInternalAsync()
    {
        lifetime = new();
        statusPolling = PollStatusAsync(lifetime.Token);
        return Task.CompletedTask;
    }

    private async Task PollStatusAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            do
            {
                try
                {
                    var gate = networklogic.CheckGateStatusAsync(token);
                    var login = networklogic.CheckLoginStatusAsync(token);
                    await Task.WhenAll(gate, login);
                    serverStatus = (await gate, await login) switch
                    {
                        (null, _) or (_, null) => "Server status unknown",
                        (true, true) => "Servers available",
                        _ => "Servers unavailable"
                    };
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    Logger.Warning($"FFXIV server status check failed: {ex.Message}");
                    serverStatus = "Server status unavailable";
                }
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) { }
    }

    public override void RenderImGui()
    {
        // The host owns the ImGui context, thread, and frame lifetime.
        var visible = ImGuiNET.ImGui.Begin("FFXIV Launcher");
        try { if (visible) ImGuiNET.ImGui.TextUnformatted(serverStatus); }
        finally { ImGuiNET.ImGui.End(); }
    }

    protected override Task<bool> LaunchGameInternalAsync(GameLaunchParameters parameters) =>
        LaunchCoreAsync(parameters, null, CancellationToken.None);

    public async Task<bool> LaunchAsync(
        GameLaunchParameters parameters,
        IProgress<LaunchStatus> progress,
        CancellationToken cancellationToken)
    {
        await LaunchLock.WaitAsync(cancellationToken);
        try
        {
            return await LaunchCoreAsync(parameters, progress, cancellationToken);
        }
        finally
        {
            LaunchLock.Release();
        }
    }

    private async Task<bool> LaunchCoreAsync(
        GameLaunchParameters parameters,
        IProgress<LaunchStatus>? progress,
        CancellationToken token)
    {
        using var correlationScope = FfxivTraceLogger.BeginCorrelation();
        var correlationId = correlationScope.Id;

        // Keep traces useful without persisting a user's local folder path. The marker-file
        // booleans distinguish a path-transfer problem from selecting the wrong installation root.
        var gameRoot = parameters.GamePath ?? "";
        var bootDirectory = Path.Combine(gameRoot, "boot");
        var gameDirectory = Path.Combine(gameRoot, "game");
        var gameExecutable = Path.Combine(gameDirectory, "ffxiv_dx11.exe");
        var bootExecutable = Path.Combine(bootDirectory, "ffxivboot.exe");
        var gameVersion = Path.Combine(gameDirectory, "ffxivgame.ver");
        FfxivTraceLogger.StageLazy(
            correlationId,
            "Plugin launch requested",
            () => $"GamePathLength={gameRoot.Length}; RootExists={Directory.Exists(gameRoot)}; BootDirectoryExists={Directory.Exists(bootDirectory)}; GameDirectoryExists={Directory.Exists(gameDirectory)}; BootExecutableExists={File.Exists(bootExecutable)}; GameExecutableExists={File.Exists(gameExecutable)}; GameVersionExists={File.Exists(gameVersion)}; Steam={parameters.IsSteam}; DX11={parameters.DirectX11}; Region={parameters.Region}");

        var credentials = parameters.Credentials ?? new GameCredentials
        {
            Username = parameters.EnvironmentVariables.GetValueOrDefault("FFXIV_USERNAME", ""),
            Password = parameters.EnvironmentVariables.GetValueOrDefault("FFXIV_PASSWORD", ""),
            OTP = parameters.EnvironmentVariables.GetValueOrDefault("FFXIV_OTP", "")
        };

        FfxivTraceLogger.Trace(
            correlationId,
            $"Credential input present: username={(!string.IsNullOrEmpty(credentials.Username))}; password={(!string.IsNullOrEmpty(credentials.Password))}; otp={(!string.IsNullOrEmpty(credentials.OTP))}");

        try
        {
            if (!credentials.IsValid)
                throw new ArgumentException("Username and password are required.");

            if (!Directory.Exists(gameRoot) || !Directory.Exists(bootDirectory) ||
                !Directory.Exists(gameDirectory) || !File.Exists(gameExecutable) || !File.Exists(gameVersion))
                throw new DirectoryNotFoundException(
                    "The selected folder is not a complete FFXIV installation. Choose the folder containing the boot and game directories, including game/ffxiv_dx11.exe and game/ffxivgame.ver.");

            FfxivTraceLogger.Stage(correlationId, "Authentication stage starting");
            progress?.Report(new("authenticating", "Signing in to Final Fantasy XIV…"));

            var sid = await networklogic.GetRealSidAsync(
                parameters.GamePath,
                credentials.Username,
                credentials.Password,
                credentials.OTP,
                parameters.IsSteam,
                token);

            token.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(sid) || sid == "BAD")
            {
                FfxivTraceLogger.Warn(correlationId, "Authentication stage returned no usable session identifier");
                return false;
            }

            FfxivTraceLogger.Stage(correlationId, "Authentication stage completed");
            progress?.Report(new("launching", "Preparing Final Fantasy XIV…"));

            FfxivTraceLogger.Stage(correlationId, "Game launch stage starting");
            using var process = await networklogic.LaunchGameAsync(
                parameters.GamePath,
                sid,
                parameters.Language,
                parameters.DirectX11,
                parameters.ExpansionLevel,
                parameters.IsSteam,
                parameters.Region,
                token);

            if (process is null)
            {
                FfxivTraceLogger.Error(correlationId, "Game launch stage returned a null process");
                return false;
            }

            var running = !process.HasExited;
            FfxivTraceLogger.Stage(correlationId, "Plugin launch completed", $"PID={process.Id}; Running={running}");
            return running;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            FfxivTraceLogger.Warn(correlationId, "Plugin launch cancelled");
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // A timed-out internal HTTP/runtime operation is not a user cancellation. Preserve
            // the distinction so the desktop UI reports a launch failure and the trace retains
            // the original timeout/cancellation details.
            var failure = new TimeoutException("An internal FFXIV launch operation timed out or was interrupted.", ex);
            Logger.Error($"FFXIV launch failed: {failure.GetType().Name}");
            FfxivTraceLogger.Error(correlationId, "Plugin launch was interrupted outside caller cancellation", failure);
            throw failure;
        }
        catch (Exception ex)
        {
            Logger.Error($"FFXIV launch failed: {ex.GetType().Name}");
            FfxivTraceLogger.Error(correlationId, "Plugin launch failed", ex);
            throw;
        }
        finally
        {
            credentials.Password = "";
            credentials.OTP = "";
            credentials.Token = "";
            parameters.EnvironmentVariables.Remove("FFXIV_PASSWORD");
            parameters.EnvironmentVariables.Remove("FFXIV_OTP");
            FfxivTraceLogger.Stage(correlationId, "Credentials cleared from launch request");
        }
    }

    protected override async Task ShutdownInternalAsync()
    {
        if (lifetime is null) return;
        await lifetime.CancelAsync();
        if (statusPolling is not null) await statusPolling;
        lifetime.Dispose();
        lifetime = null;
    }

    protected override Task<bool> ValidateConfigurationInternalAsync() => Task.FromResult(true);
}
