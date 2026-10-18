using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibDalamud;
using LibDalamud.Common.Dalamud;
using Serilog;
using XIVLauncher.Common.PatcherIpc;
using XIVLauncher.Common.Patching.Rpc;
using XIVLauncher.Common.Patching.ZiPatch;
using XIVLauncher.Common.Patching.ZiPatch.Util;

namespace XIVLauncher.Common.Patching;

public class RemotePatchInstaller
{
    private readonly IRpc rpc;
    private readonly ConcurrentQueue<PatcherIpcStartInstall> queuedInstalls = new();
    private readonly Thread patcherThread;
    private readonly CancellationTokenSource patcherCancelToken = new();

    public bool IsDone { get; private set; }

    public bool IsFailed { get; private set; }

    public bool HasQueuedInstalls => !this.queuedInstalls.IsEmpty;

    public RemotePatchInstaller(IRpc rpc)
    {
        this.rpc = rpc;
        this.rpc.MessageReceived += RemoteCallHandler;

        Log.Information("[PATCHER] IPC connected");

        rpc.SendMessage(new PatcherIpcEnvelope
        {
            OpCode = PatcherIpcOpCode.Hello,
            Data = DateTime.Now
        });

        Log.Information("[PATCHER] sent hello");

        this.patcherThread = new Thread(this.ProcessPatches);
    }

    public void Start()
    {
        this.patcherThread.Start();
    }

    private void ProcessPatches()
    {
        try
        {
            while (!this.patcherCancelToken.IsCancellationRequested)
            {
                if (!RunInstallQueue())
                {
                    IsFailed = true;
                    return;
                }

                Thread.Sleep(1000);
            }
        }
        catch (Exception ex)
        {
            Log.Error("[PATCHER] Installer worker failed ({ErrorType})", ex.GetType().Name);
            LaunchCancellationContext.WriteTrace(() => $"Patch installer worker failed: {ex}");
            this.rpc.SendMessage(new PatcherIpcEnvelope
            {
                OpCode = PatcherIpcOpCode.InstallFailed
            });
        }
    }

    private void RemoteCallHandler(PatcherIpcEnvelope envelope)
    {
        switch (envelope.OpCode)
        {
            case PatcherIpcOpCode.Bye:
                Task.Run(() =>
                {
                    Thread.Sleep(3000);
                    IsDone = true;
                });
                break;

            case PatcherIpcOpCode.StartInstall:

                var installData = (PatcherIpcStartInstall)envelope.Data;
                this.queuedInstalls.Enqueue(installData);
                break;

            case PatcherIpcOpCode.Finish:
                var path = (DirectoryInfo)envelope.Data;

                try
                {
                    VerToBck(path);
                    Log.Information("VerToBck done");
                }
                catch (Exception ex)
                {
                    Log.Error("Could not back up patch version metadata ({ErrorType})", ex.GetType().Name);
                    LaunchCancellationContext.WriteTrace(() => $"VerToBck failed: {ex}");
                    this.rpc.SendMessage(new PatcherIpcEnvelope
                    {
                        OpCode = PatcherIpcOpCode.InstallFailed
                    });
                }

                break;
        }
    }

    private bool RunInstallQueue()
    {
        if (this.queuedInstalls.TryDequeue(out var installData))
        {
            // Ensure that subdirs exist
            if (!installData.GameDirectory.Exists)
                installData.GameDirectory.Create();

            installData.GameDirectory.CreateSubdirectory("game");
            installData.GameDirectory.CreateSubdirectory("boot");

            try
            {
                InstallPatch(installData.PatchFile.FullName,
                    Path.Combine(installData.GameDirectory.FullName,
                        installData.Repo == Repository.Boot ? "boot" : "game"));

                try
                {
                    installData.Repo.SetVer(installData.GameDirectory, installData.VersionId);
                    this.rpc.SendMessage(new PatcherIpcEnvelope
                    {
                        OpCode = PatcherIpcOpCode.InstallOk
                    });

                    try
                    {
                        if (!installData.KeepPatch)
                            installData.PatchFile.Delete();
                    }
                    catch (Exception exception)
                    {
                        Log.Error("Could not delete the processed patch file ({ErrorType})", exception.GetType().Name);
                        LaunchCancellationContext.WriteTrace(() => $"Could not delete processed patch file: {exception}");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("Could not persist patch version metadata ({ErrorType})", ex.GetType().Name);
                    LaunchCancellationContext.WriteTrace(() => $"Could not set patch version file: {ex}");
                    this.rpc.SendMessage(new PatcherIpcEnvelope
                    {
                        OpCode = PatcherIpcOpCode.InstallFailed
                    });

                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.Error("[PATCHER] Patch installation failed ({ErrorType})", ex.GetType().Name);
                LaunchCancellationContext.WriteTrace(() => $"Patch install failed: {ex}");
                this.rpc.SendMessage(new PatcherIpcEnvelope
                {
                    OpCode = PatcherIpcOpCode.InstallFailed
                });

                return false;
            }
        }

        return true;
    }

    public static void InstallPatch(string patchPath, string gamePath)
    {
        Log.Information("[PATCHER] Applying patch");
        LaunchCancellationContext.WriteTrace(() => $"Patch installation paths: patch={patchPath}; destination={gamePath}");

        using var patchFile = ZiPatchFile.FromFileName(patchPath);

        using (var store = new SqexFileStreamStore())
        {
            var config = new ZiPatchConfig(gamePath) { Store = store };

            foreach (var chunk in patchFile.GetChunks())
                chunk.ApplyChunk(config);
        }

        Log.Information("[PATCHER] Patch installed");
        LaunchCancellationContext.WriteTrace(() => $"Patch installed: {patchPath}");
    }

    private static void VerToBck(DirectoryInfo gamePath)
    {
        Thread.Sleep(500);

        foreach (var repository in Enum.GetValues(typeof(Repository)).Cast<Repository>())
        {
            // Overwrite the old BCK with the new game version
            var ver = repository.GetVer(gamePath);

            try
            {
                repository.SetVer(gamePath, ver, true);
            }
            catch (Exception ex)
            {
                Log.Error("[PATCHER] Could not copy version metadata to backup ({ErrorType})", ex.GetType().Name);
                LaunchCancellationContext.WriteTrace(() => $"Could not copy patch metadata to backup: {ex}");

                if (ver != Constants.BASE_GAME_VERSION)
                    throw;
            }
        }
    }
}