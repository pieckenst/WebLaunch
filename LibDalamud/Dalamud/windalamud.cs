using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Newtonsoft.Json;
using Steamworks;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading.Tasks;
using XIVLauncher.Common.PlatformAbstractions;

namespace LibDalamud.Common.Dalamud
{
    public class WindowsDalamudRunner : IDalamudRunner
    {
        public Process? Run(FileInfo runner, bool fakeLogin, bool noPlugins, bool noThirdPlugins, FileInfo gameExe, string gameArgs, IDictionary<string, string> environment, DalamudLoadMethod loadMethod, DalamudStartInfo startInfo)
        {
            var inheritableCurrentProcess = GetInheritableCurrentProcessHandle();

            var launchArguments = new List<string>
        {
            "launch",
            $"--mode={(loadMethod == DalamudLoadMethod.EntryPoint ? "entrypoint" : "inject")}",
            $"--handle-owner={(long)inheritableCurrentProcess.Handle}",
            $"--game=\"{gameExe.FullName}\"",
            $"--dalamud-working-directory=\"{startInfo.WorkingDirectory}\"",
            $"--dalamud-configuration-path=\"{startInfo.ConfigurationPath}\"",
            $"--dalamud-plugin-directory=\"{startInfo.PluginDirectory}\"",
            $"--dalamud-dev-plugin-directory=\"{startInfo.DefaultPluginDirectory}\"",
            $"--dalamud-asset-directory=\"{startInfo.AssetDirectory}\"",
            $"--dalamud-client-language={(int)startInfo.Language}",
            $"--dalamud-delay-initialize={startInfo.DelayInitializeMs}",
            $"--dalamud-tspack-b64={Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(startInfo.TroubleshootingPackData))}",
        };

            if (loadMethod == DalamudLoadMethod.ACLonly)
                launchArguments.Add("--without-dalamud");

            if (fakeLogin)
                launchArguments.Add("--fake-arguments");

            if (noPlugins)
                launchArguments.Add("--no-plugin");

            if (noThirdPlugins)
                launchArguments.Add("--no-3rd-plugin");

            launchArguments.Add("--");
            launchArguments.Add(gameArgs);

            var psi = new ProcessStartInfo(runner.FullName)
            {
                Arguments = string.Join(" ", launchArguments),
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var keyValuePair in environment)
            {
                if (psi.EnvironmentVariables.ContainsKey(keyValuePair.Key))
                    psi.EnvironmentVariables[keyValuePair.Key] = keyValuePair.Value;
                else
                    psi.EnvironmentVariables.Add(keyValuePair.Key, keyValuePair.Value);
            }

            try
            {
                var dalamudProcess = Process.Start(psi);
                var output = dalamudProcess.StandardOutput.ReadLine();

                if (output == null)
                    throw new DalamudRunnerException("An internal Dalamud error has occured");

                try
                {
                    var dalamudConsoleOutput = JsonConvert.DeserializeObject<DalamudConsoleOutput>(output);
                    Process gameProcess;

                    if (dalamudConsoleOutput.Handle == 0)
                    {
                        Console.WriteLine($"Dalamud returned NULL process handle, attempting to recover by creating a new one from pid {dalamudConsoleOutput.Pid}...");
                        gameProcess = Process.GetProcessById(dalamudConsoleOutput.Pid);
                    }
                    else
                    {
                        gameProcess = new ExistingProcess((IntPtr)dalamudConsoleOutput.Handle);
                    }

                    try
                    {
                        Console.WriteLine($"Got game process handle {gameProcess.Handle} with pid {gameProcess.Id}");
                    }
                    catch (InvalidOperationException ex)
                    {
                        Console.WriteLine(ex.Message, $"Dalamud returned invalid process handle {gameProcess.Handle}, attempting to recover by creating a new one from pid {dalamudConsoleOutput.Pid}...");
                        gameProcess = Process.GetProcessById(dalamudConsoleOutput.Pid);
                        Console.WriteLine($"Recovered with process handle {gameProcess.Handle}");
                    }

                    if (gameProcess.Id != dalamudConsoleOutput.Pid)
                        Console.WriteLine($"Internal Process ID {gameProcess.Id} does not match Dalamud provided one {dalamudConsoleOutput.Pid}");

                    return gameProcess;
                }
                catch (JsonReaderException ex)
                {
                    Console.WriteLine("Could not parse Dalamud process result.");
                    return null;
                }
            }
            catch (Exception ex)
            {
                throw new DalamudRunnerException("Error trying to start Dalamud.", ex);
            }
        }

        /// <summary>
        /// DUPLICATE_* values for DuplicateHandle's dwDesiredAccess.
        /// </summary>
        [Flags]
        private enum DuplicateOptions : uint
        {
            /// <summary>
            /// Closes the source handle. This occurs regardless of any error status returned.
            /// </summary>
            CloseSource = 0x00000001,

            /// <summary>
            /// Ignores the dwDesiredAccess parameter. The duplicate handle has the same access as the source handle.
            /// </summary>
            SameAccess = 0x00000002,
        }

        /// <summary>
        /// Duplicates an object handle.
        /// </summary>
        /// <param name="hSourceProcessHandle">
        /// A handle to the process with the handle to be duplicated.
        ///
        /// The handle must have the PROCESS_DUP_HANDLE access right.
        /// </param>
        /// <param name="hSourceHandle">
        /// The handle to be duplicated. This is an open object handle that is valid in the context of the source process.
        /// For a list of objects whose handles can be duplicated, see the following Remarks section.
        /// </param>
        /// <param name="hTargetProcessHandle">
        /// A handle to the process that is to receive the duplicated handle.
        ///
        /// The handle must have the PROCESS_DUP_HANDLE access right.
        /// </param>
        /// <param name="lpTargetHandle">
        /// A pointer to a variable that receives the duplicate handle. This handle value is valid in the context of the target process.
        ///
        /// If hSourceHandle is a pseudo handle returned by GetCurrentProcess or GetCurrentThread, DuplicateHandle converts it to a real handle to a process or thread, respectively.
        ///
        /// If lpTargetHandle is NULL, the function duplicates the handle, but does not return the duplicate handle value to the caller. This behavior exists only for backward compatibility with previous versions of this function. You should not use this feature, as you will lose system resources until the target process terminates.
        ///
        /// This parameter is ignored if hTargetProcessHandle is NULL.
        /// </param>
        /// <param name="dwDesiredAccess">
        /// The access requested for the new handle. For the flags that can be specified for each object type, see the following Remarks section.
        ///
        /// This parameter is ignored if the dwOptions parameter specifies the DUPLICATE_SAME_ACCESS flag. Otherwise, the flags that can be specified depend on the type of object whose handle is to be duplicated.
        ///
        /// This parameter is ignored if hTargetProcessHandle is NULL.
        /// </param>
        /// <param name="bInheritHandle">
        /// A variable that indicates whether the handle is inheritable. If TRUE, the duplicate handle can be inherited by new processes created by the target process. If FALSE, the new handle cannot be inherited.
        ///
        /// This parameter is ignored if hTargetProcessHandle is NULL.
        /// </param>
        /// <param name="dwOptions">
        /// Optional actions.
        /// </param>
        /// <returns>
        /// If the function succeeds, the return value is nonzero.
        ///
        /// If the function fails, the return value is zero. To get extended error information, call GetLastError.
        /// </returns>
        /// <remarks>
        /// See https://docs.microsoft.com/en-us/windows/win32/api/handleapi/nf-handleapi-duplicatehandle.
        /// </remarks>
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DuplicateHandle(
            IntPtr hSourceProcessHandle,
            IntPtr hSourceHandle,
            IntPtr hTargetProcessHandle,
            out IntPtr lpTargetHandle,
            uint dwDesiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
            DuplicateOptions dwOptions);

        private static Process GetInheritableCurrentProcessHandle()
        {
            if (!DuplicateHandle(Process.GetCurrentProcess().Handle, Process.GetCurrentProcess().Handle, Process.GetCurrentProcess().Handle, out var inheritableCurrentProcessHandle, 0, true, DuplicateOptions.SameAccess))
            {
                Console.WriteLine("Failed to call DuplicateHandle: Win32 error code {0}", Marshal.GetLastWin32Error());
                return null;
            }

            return new ExistingProcess(inheritableCurrentProcessHandle);
        }
    }
    public class ExistingProcess : Process
    {
        public ExistingProcess(IntPtr handle)
        {
            SetHandle(handle);
        }

        private void SetHandle(IntPtr handle)
        {
            var baseType = GetType().BaseType;
            if (baseType == null)
                return;

            var setProcessHandleMethod = baseType.GetMethod("SetProcessHandle",
                BindingFlags.NonPublic | BindingFlags.Instance);
            setProcessHandleMethod?.Invoke(this, new object[] { new SafeProcessHandle(handle, true) });
        }
    }
    public class WindowsDalamudCompatibilityCheck : IDalamudCompatibilityCheck
    {
        public void EnsureCompatibility()
        {
            if (!CheckVcRedists())
                throw new IDalamudCompatibilityCheck.NoRedistsException();

            EnsureArchitecture();
        }

        private static void EnsureArchitecture()
        {
            var arch = RuntimeInformation.ProcessArchitecture;

            switch (arch)
            {
                case Architecture.X86:
                    throw new IDalamudCompatibilityCheck.ArchitectureNotSupportedException("Dalamud is not supported on x86 architecture.");

                case Architecture.X64:
                    break;

                case Architecture.Arm:
                    throw new IDalamudCompatibilityCheck.ArchitectureNotSupportedException("Dalamud is not supported on ARM32.");

                case Architecture.Arm64:
                    throw new IDalamudCompatibilityCheck.ArchitectureNotSupportedException("x64 emulation was not detected. Please make sure to run XIVLauncher with x64 emulation.");
            }
        }

        [DllImport("kernel32", SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        private static bool CheckLibrary(string fileName)
        {
            if (LoadLibrary(fileName) != IntPtr.Zero)
            {
                Console.WriteLine("Found " + fileName);
                return true;
            }
            else
            {
                Console.WriteLine("Could not find " + fileName);
            }
            return false;
        }

        private static bool CheckVcRedists()
        {
            // snipped from https://stackoverflow.com/questions/12206314/detect-if-visual-c-redistributable-for-visual-studio-2012-is-installed
            // and https://github.com/bitbeans/RedistributableChecker

            var vc2022Paths = new List<string>
        {
            @"SOFTWARE\Microsoft\DevDiv\VC\Servicing\14.0\RuntimeMinimum",
            @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64",
            @"SOFTWARE\Classes\Installer\Dependencies\Microsoft.VS.VC_RuntimeMinimumVSU_amd64,v14",
            @"SOFTWARE\Classes\Installer\Dependencies\VC,redist.x64,amd64,14.31,bundle",
            @"SOFTWARE\Classes\Installer\Dependencies\VC,redist.x64,amd64,14.30,bundle",
            @"SOFTWARE\Classes\Installer\Dependencies\VC,redist.x64,amd64,14.29,bundle",
            @"SOFTWARE\Classes\Installer\Dependencies\VC,redist.x64,amd64,14.28,bundle",
            // technically, this was introduced in VCrun2017 with 14.16
            // but we shouldn't go that far
            // here's a legacy vcrun2017 check
            @"Installer\Dependencies\,,amd64,14.0,bundle",
            // here's one for vcrun2015
            @"SOFTWARE\Classes\Installer\Dependencies\{d992c12e-cab2-426f-bde3-fb8c53950b0d}"
        };

            var dllPaths = new List<string>
        {
            "ucrtbase_clr0400",
            "vcruntime140_clr0400",
            "vcruntime140"
        };

            var passedRegistry = false;
            var passedDllChecks = true;

            foreach (var path in vc2022Paths)
            {
                Console.WriteLine("Checking Registry key: " + path);
                var vcregcheck = Registry.LocalMachine.OpenSubKey(path, false);
                if (vcregcheck == null) continue;

                var vcVersioncheck = vcregcheck.GetValue("Version") ?? "";

                if (((string)vcVersioncheck).StartsWith("14", StringComparison.Ordinal))
                {
                    passedRegistry = true;
                    Console.WriteLine("Passed Registry Check with: " + path);
                    break;
                }
            }

            foreach (var path in dllPaths)
            {
                Console.WriteLine("Checking for DLL: " + path);
                passedDllChecks = passedDllChecks && CheckLibrary(path);
            }

            // Display our findings
            if (!passedRegistry)
            {
                Console.WriteLine("Failed all registry checks to find any Visual C++ 2015-2022 Runtimes.");
            }

            if (!passedDllChecks)
            {
                Console.WriteLine("Missing DLL files required by Dalamud.");
            }

            return (passedRegistry && passedDllChecks);
        }
    }
    public class WindowsRestartManager : IDisposable
    {
        public delegate void RmWriteStatusCallback(uint percentageCompleted);

        private const int RM_SESSION_KEY_LEN = 16; // sizeof GUID
        private const int CCH_RM_SESSION_KEY = RM_SESSION_KEY_LEN * 2;
        private const int CCH_RM_MAX_APP_NAME = 255;
        private const int CCH_RM_MAX_SVC_NAME = 63;
        private const int RM_INVALID_TS_SESSION = -1;
        private const int RM_INVALID_PROCESS = -1;
        private const int ERROR_MORE_DATA = 234;

        [StructLayout(LayoutKind.Sequential)]
        public struct RmUniqueProcess
        {
            public int dwProcessId; // PID
            public FILETIME ProcessStartTime; // Process creation time
        }

        public enum RmAppType
        {
            /// <summary>
            /// Application type cannot be classified in known categories
            /// </summary>
            RmUnknownApp = 0,

            /// <summary>
            /// Application is a windows application that displays a top-level window
            /// </summary>
            RmMainWindow = 1,

            /// <summary>
            /// Application is a windows app but does not display a top-level window
            /// </summary>
            RmOtherWindow = 2,

            /// <summary>
            /// Application is an NT service
            /// </summary>
            RmService = 3,

            /// <summary>
            /// Application is Explorer
            /// </summary>
            RmExplorer = 4,

            /// <summary>
            /// Application is Console application
            /// </summary>
            RmConsole = 5,

            /// <summary>
            /// Application is critical system process where a reboot is required to restart
            /// </summary>
            RmCritical = 1000,
        }

        [Flags]
        public enum RmRebootReason
        {
            /// <summary>
            /// A system restart is not required.
            /// </summary>
            RmRebootReasonNone = 0x0,

            /// <summary>
            /// The current user does not have sufficient privileges to shut down one or more processes.
            /// </summary>
            RmRebootReasonPermissionDenied = 0x1,

            /// <summary>
            /// One or more processes are running in another Terminal Services session.
            /// </summary>
            RmRebootReasonSessionMismatch = 0x2,

            /// <summary>
            /// A system restart is needed because one or more processes to be shut down are critical processes.
            /// </summary>
            RmRebootReasonCriticalProcess = 0x4,

            /// <summary>
            /// A system restart is needed because one or more services to be shut down are critical services.
            /// </summary>
            RmRebootReasonCriticalService = 0x8,

            /// <summary>
            /// A system restart is needed because the current process must be shut down.
            /// </summary>
            RmRebootReasonDetectedSelf = 0x10,
        }

        [Flags]
        private enum RmShutdownType
        {
            RmForceShutdown = 0x1, // Force app shutdown
            RmShutdownOnlyRegistered = 0x10 // Only shutdown apps if all apps registered for restart
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct RmProcessInfo
        {
            public RmUniqueProcess UniqueProcess;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_APP_NAME + 1)]
            public string AppName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_SVC_NAME + 1)]
            public string ServiceShortName;

            public RmAppType ApplicationType;
            public int AppStatus;
            public int TSSessionId;

            [MarshalAs(UnmanagedType.Bool)]
            public bool bRestartable;

            public Process Process
            {
                get
                {
                    try
                    {
                        Process process = Process.GetProcessById(UniqueProcess.dwProcessId);
                        long fileTime = process.StartTime.ToFileTime();

                        if ((uint)UniqueProcess.ProcessStartTime.dwLowDateTime != (uint)(fileTime & uint.MaxValue))
                            return null;

                        if ((uint)UniqueProcess.ProcessStartTime.dwHighDateTime != (uint)(fileTime >> 32))
                            return null;

                        return process;
                    }
                    catch (Exception)
                    {
                        return null;
                    }
                }
            }
        }

        [DllImport("rstrtmgr", CharSet = CharSet.Unicode)]
        private static extern int RmStartSession(out int dwSessionHandle, int sessionFlags, StringBuilder strSessionKey);

        [DllImport("rstrtmgr")]
        private static extern int RmEndSession(int dwSessionHandle);

        [DllImport("rstrtmgr")]
        private static extern int RmShutdown(int dwSessionHandle, RmShutdownType lAtionFlags, RmWriteStatusCallback fnStatus);

        [DllImport("rstrtmgr")]
        private static extern int RmRestart(int dwSessionHandle, int dwRestartFlags, RmWriteStatusCallback fnStatus);

        [DllImport("rstrtmgr")]
        private static extern int RmGetList(int dwSessionHandle, out int nProcInfoNeeded, ref int nProcInfo, [In, Out] RmProcessInfo[] rgAffectedApps, out RmRebootReason dwRebootReasons);

        [DllImport("rstrtmgr", CharSet = CharSet.Unicode)]
        private static extern int RmRegisterResources(int dwSessionHandle,
                                                      int nFiles, string[] rgsFileNames,
                                                      int nApplications, RmUniqueProcess[] rgApplications,
                                                      int nServices, string[] rgsServiceNames);

        private readonly int sessionHandle;
        private readonly string sessionKey;

        public WindowsRestartManager()
        {
            var sessKey = new StringBuilder(CCH_RM_SESSION_KEY + 1);
            ThrowOnFailure(RmStartSession(out sessionHandle, 0, sessKey));
            sessionKey = sessKey.ToString();
        }

        public void Register(IEnumerable<FileInfo> files = null, IEnumerable<Process> processes = null, IEnumerable<string> serviceNames = null)
        {
            string[] filesArray = files?.Select(f => f.FullName).ToArray() ?? Array.Empty<string>();
            RmUniqueProcess[] processesArray = processes?.Select(f => new RmUniqueProcess
            {
                dwProcessId = f.Id,
                ProcessStartTime = new FILETIME
                {
                    dwLowDateTime = (int)(f.StartTime.ToFileTime() & uint.MaxValue),
                    dwHighDateTime = (int)(f.StartTime.ToFileTime() >> 32),
                }
            }).ToArray() ?? Array.Empty<RmUniqueProcess>();
            string[] servicesArray = serviceNames?.ToArray() ?? Array.Empty<string>();
            ThrowOnFailure(RmRegisterResources(sessionHandle,
                filesArray.Length, filesArray,
                processesArray.Length, processesArray,
                servicesArray.Length, servicesArray));
        }

        public void Shutdown(bool forceShutdown = true, bool shutdownOnlyRegistered = false, RmWriteStatusCallback cb = null)
        {
            ThrowOnFailure(RmShutdown(sessionHandle, (forceShutdown ? RmShutdownType.RmForceShutdown : 0) | (shutdownOnlyRegistered ? RmShutdownType.RmShutdownOnlyRegistered : 0), cb));
        }

        public void Restart(RmWriteStatusCallback cb = null)
        {
            ThrowOnFailure(RmRestart(sessionHandle, 0, cb));
        }

        public List<RmProcessInfo> GetInterferingProcesses(out RmRebootReason rebootReason)
        {
            var count = 0;
            var infos = new RmProcessInfo[count];
            var err = 0;

            for (var i = 0; i < 16; i++)
            {
                err = RmGetList(sessionHandle, out int needed, ref count, infos, out rebootReason);

                switch (err)
                {
                    case 0:
                        return infos.Take(count).ToList();

                    case ERROR_MORE_DATA:
                        infos = new RmProcessInfo[count = needed];
                        break;

                    default:
                        ThrowOnFailure(err);
                        break;
                }
            }

            ThrowOnFailure(err);

            // should not reach
            throw new InvalidOperationException();
        }

        private void ReleaseUnmanagedResources()
        {
            ThrowOnFailure(RmEndSession(sessionHandle));
        }

        public void Dispose()
        {
            ReleaseUnmanagedResources();
            GC.SuppressFinalize(this);
        }

        ~WindowsRestartManager()
        {
            ReleaseUnmanagedResources();
        }

        private void ThrowOnFailure(int err)
        {
            if (err != 0)
                throw new Win32Exception(err);
        }
    }
    public class WindowsSteam : ISteam
    {
        public WindowsSteam()
        {
            SteamUtils.OnGamepadTextInputDismissed += b => OnGamepadTextInputDismissed?.Invoke(b);
        }

        public void Initialize(uint appId)
        {
            // workaround because SetEnvironmentVariable doesn't actually touch the process environment on unix
            if (Environment.OSVersion.Platform == PlatformID.Unix)
            {
                [System.Runtime.InteropServices.DllImport("c")]
                static extern int setenv(string name, string value, int overwrite);

                setenv("SteamAppId", appId.ToString(), 1);
            }

            SteamClient.Init(appId);
        }

        public bool IsValid => SteamClient.IsValid;

        public bool BLoggedOn => SteamClient.IsLoggedOn;

        public bool BOverlayNeedsPresent => SteamUtils.DoesOverlayNeedPresent;

        public void Shutdown()
        {
            SteamClient.Shutdown();
        }

        public async Task<byte[]?> GetAuthSessionTicketAsync()
        {
            var ticket = await SteamUser.GetAuthSessionTicketAsync().ConfigureAwait(true);
            return ticket?.Data;
        }

        public bool IsAppInstalled(uint appId)
        {
            return SteamApps.IsAppInstalled(appId);
        }

        public string GetAppInstallDir(uint appId)
        {
            return SteamApps.AppInstallDir(appId);
        }

        public bool ShowGamepadTextInput(bool password, bool multiline, string description, int maxChars, string existingText = "")
        {
            return SteamUtils.ShowGamepadTextInput(password ? GamepadTextInputMode.Password : GamepadTextInputMode.Normal, multiline ? GamepadTextInputLineMode.MultipleLines : GamepadTextInputLineMode.SingleLine, description, maxChars, existingText);
        }

        public string GetEnteredGamepadText()
        {
            return SteamUtils.GetEnteredGamepadText();
        }

        public bool ShowFloatingGamepadTextInput(ISteam.EFloatingGamepadTextInputMode mode, int x, int y, int width, int height)
        {
            // Facepunch.Steamworks doesn't have this...
            return false;
        }

        public bool IsRunningOnSteamDeck() => false;

        public uint GetServerRealTime() => (uint)((DateTimeOffset)SteamUtils.SteamServerTime).ToUnixTimeSeconds();

        public void ActivateGameOverlayToWebPage(string url, bool modal = false)
        {
            SteamFriends.OpenWebOverlay(url, modal);
        }

        public event Action<bool> OnGamepadTextInputDismissed;
    }
}
