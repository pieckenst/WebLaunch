using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Security;
using System.Net;
using System.Runtime.InteropServices;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LibDalamud.Common.Dalamud;
using Microsoft.Win32.SafeHandles;
using System.Reflection;
using XIVLauncher.Common.PlatformAbstractions;
using Serilog;
using XIVLauncher.Common.Addon;
using LibDalamud;
using static XIVLauncher.Common.Game.Launcher;
using XIVLauncher.Common.Encryption;
using XIVLauncher.Common.Game.Exceptions;
using Newtonsoft.Json;

namespace CoreLibLaunchSupport
{
    public class NoValidSubscriptionException : Exception
{
    public NoValidSubscriptionException(string message) : base(message) { }
}
    public enum DpiAwareness
    {
        Aware,
        Unaware,
    }
    public class GameExitedException : Exception
    {
        public GameExitedException()
            : base("Game exited prematurely.")
        {
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

    public interface IGameRunner
    {
        Process? Start(string path, string workingDirectory, string arguments, IDictionary<string, string> environment, DpiAwareness dpiAwareness);
    }

    public sealed record ProcessLaunchOptions(
        string ExecutablePath,
        string WorkingDirectory,
        string Arguments,
        IReadOnlyDictionary<string, string>? EnvironmentVariables,
        DpiAwareness DpiAwareness);

    public interface IProcessLauncher
    {
        Process Launch(ProcessLaunchOptions options, Action<Process>? onBeforeResume = null);
    }

    public sealed class NativeProcessLauncher : IProcessLauncher
    {
        // Definitions taken from PInvoke.net (with some changes)
        private static class PInvoke
        {
            #region Constants
            public const UInt32 STANDARD_RIGHTS_ALL = 0x001F0000;
            public const UInt32 SPECIFIC_RIGHTS_ALL = 0x0000FFFF;
            public const UInt32 PROCESS_VM_WRITE = 0x0020;

            public const UInt32 GRANT_ACCESS = 1;

            public const UInt32 SECURITY_DESCRIPTOR_REVISION = 1;

            public const UInt32 CREATE_SUSPENDED = 0x00000004;

            public const UInt32 TOKEN_QUERY = 0x0008;
            public const UInt32 TOKEN_ADJUST_PRIVILEGES = 0x0020;

            public const UInt32 PRIVILEGE_SET_ALL_NECESSARY = 1;

            public const UInt32 SE_PRIVILEGE_ENABLED = 0x00000002;
            public const UInt32 SE_PRIVILEGE_REMOVED = 0x00000004;

            public enum MULTIPLE_TRUSTEE_OPERATION
            {
                NO_MULTIPLE_TRUSTEE,
                TRUSTEE_IS_IMPERSONATE
            }

            public enum TRUSTEE_FORM
            {
                TRUSTEE_IS_SID,
                TRUSTEE_IS_NAME,
                TRUSTEE_BAD_FORM,
                TRUSTEE_IS_OBJECTS_AND_SID,
                TRUSTEE_IS_OBJECTS_AND_NAME
            }

            public enum TRUSTEE_TYPE
            {
                TRUSTEE_IS_UNKNOWN,
                TRUSTEE_IS_USER,
                TRUSTEE_IS_GROUP,
                TRUSTEE_IS_DOMAIN,
                TRUSTEE_IS_ALIAS,
                TRUSTEE_IS_WELL_KNOWN_GROUP,
                TRUSTEE_IS_DELETED,
                TRUSTEE_IS_INVALID,
                TRUSTEE_IS_COMPUTER
            }

            public enum SE_OBJECT_TYPE
            {
                SE_UNKNOWN_OBJECT_TYPE,
                SE_FILE_OBJECT,
                SE_SERVICE,
                SE_PRINTER,
                SE_REGISTRY_KEY,
                SE_LMSHARE,
                SE_KERNEL_OBJECT,
                SE_WINDOW_OBJECT,
                SE_DS_OBJECT,
                SE_DS_OBJECT_ALL,
                SE_PROVIDER_DEFINED_OBJECT,
                SE_WMIGUID_OBJECT,
                SE_REGISTRY_WOW64_32KEY
            }
            public enum SECURITY_INFORMATION
            {
                OWNER_SECURITY_INFORMATION = 1,
                GROUP_SECURITY_INFORMATION = 2,
                DACL_SECURITY_INFORMATION = 4,
                SACL_SECURITY_INFORMATION = 8,
                UNPROTECTED_SACL_SECURITY_INFORMATION = 0x10000000,
                UNPROTECTED_DACL_SECURITY_INFORMATION = 0x20000000,
                PROTECTED_SACL_SECURITY_INFORMATION = 0x40000000
            }
            #endregion

            #region Structures
            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto, Pack = 0)]
            public struct TRUSTEE : IDisposable
            {
                public IntPtr pMultipleTrustee;
                public MULTIPLE_TRUSTEE_OPERATION MultipleTrusteeOperation;
                public TRUSTEE_FORM TrusteeForm;
                public TRUSTEE_TYPE TrusteeType;
                private IntPtr ptstrName;

                void IDisposable.Dispose()
                {
                    if (ptstrName != IntPtr.Zero) Marshal.Release(ptstrName);
                }

                public string Name { get { return Marshal.PtrToStringAuto(ptstrName); } }
            }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto, Pack = 0)]
            public struct EXPLICIT_ACCESS
            {
                uint grfAccessPermissions;
                uint grfAccessMode;
                uint grfInheritance;
                TRUSTEE Trustee;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct SECURITY_DESCRIPTOR
            {
                public byte Revision;
                public byte Sbz1;
                public UInt16 Control;
                public IntPtr Owner;
                public IntPtr Group;
                public IntPtr Sacl;
                public IntPtr Dacl;
            }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            public struct STARTUPINFO
            {
                public Int32 cb;
                public string lpReserved;
                public string lpDesktop;
                public string lpTitle;
                public Int32 dwX;
                public Int32 dwY;
                public Int32 dwXSize;
                public Int32 dwYSize;
                public Int32 dwXCountChars;
                public Int32 dwYCountChars;
                public Int32 dwFillAttribute;
                public Int32 dwFlags;
                public Int16 wShowWindow;
                public Int16 cbReserved2;
                public IntPtr lpReserved2;
                public IntPtr hStdInput;
                public IntPtr hStdOutput;
                public IntPtr hStdError;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct PROCESS_INFORMATION
            {
                public IntPtr hProcess;
                public IntPtr hThread;
                public int dwProcessId;
                public UInt32 dwThreadId;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct SECURITY_ATTRIBUTES
            {
                public int nLength;
                public IntPtr lpSecurityDescriptor;
                public bool bInheritHandle;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct LUID
            {
                public UInt32 LowPart;
                public Int32 HighPart;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct PRIVILEGE_SET
            {
                public UInt32 PrivilegeCount;
                public UInt32 Control;
                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
                public LUID_AND_ATTRIBUTES[] Privilege;
            }

            public struct LUID_AND_ATTRIBUTES
            {
                public LUID Luid;
                public UInt32 Attributes;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct TOKEN_PRIVILEGES
            {
                public UInt32 PrivilegeCount;
                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
                public LUID_AND_ATTRIBUTES[] Privileges;
            }
            #endregion

            #region Methods
            [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
            public static extern void BuildExplicitAccessWithName(
                ref EXPLICIT_ACCESS pExplicitAccess,
                string pTrusteeName,
                uint AccessPermissions,
                uint AccessMode,
                uint Inheritance);

            [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
            public static extern int SetEntriesInAcl(
                int cCountOfExplicitEntries,
                ref EXPLICIT_ACCESS pListOfExplicitEntries,
                IntPtr OldAcl,
                out IntPtr NewAcl);

            [DllImport("advapi32.dll", SetLastError = true)]
            public static extern bool InitializeSecurityDescriptor(
                out SECURITY_DESCRIPTOR pSecurityDescriptor,
                uint dwRevision);

            [DllImport("advapi32.dll", SetLastError = true)]
            public static extern bool SetSecurityDescriptorDacl(
                ref SECURITY_DESCRIPTOR pSecurityDescriptor,
                bool bDaclPresent,
                IntPtr pDacl,
                bool bDaclDefaulted);

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
            public static extern bool CreateProcess(
               string lpApplicationName,
               string lpCommandLine,
               ref SECURITY_ATTRIBUTES lpProcessAttributes,
               IntPtr lpThreadAttributes,
               bool bInheritHandles,
               UInt32 dwCreationFlags,
               IntPtr lpEnvironment,
               string lpCurrentDirectory,
               [In] ref STARTUPINFO lpStartupInfo,
               out PROCESS_INFORMATION lpProcessInformation);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool CloseHandle(IntPtr hObject);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern uint ResumeThread(IntPtr hThread);

            [DllImport("advapi32.dll", SetLastError = true)]
            public static extern bool OpenProcessToken(
                IntPtr ProcessHandle,
                UInt32 DesiredAccess,
                out IntPtr TokenHandle);

            [DllImport("advapi32.dll", SetLastError = true)]
            public static extern bool LookupPrivilegeValue(string lpSystemName, string lpName, ref LUID lpLuid);

            [DllImport("advapi32.dll", SetLastError = true)]
            public static extern bool PrivilegeCheck(
                IntPtr ClientToken,
                ref PRIVILEGE_SET RequiredPrivileges,
                out bool pfResult);

            [DllImport("advapi32.dll", SetLastError = true)]
            public static extern bool AdjustTokenPrivileges(
                IntPtr TokenHandle,
                bool DisableAllPrivileges,
                ref TOKEN_PRIVILEGES NewState,
                UInt32 BufferLengthInBytes,
                IntPtr PreviousState,
                UInt32 ReturnLengthInBytes);

            [DllImport("advapi32.dll", SetLastError = true)]
            public static extern uint GetSecurityInfo(
                IntPtr handle,
                SE_OBJECT_TYPE ObjectType,
                SECURITY_INFORMATION SecurityInfo,
                IntPtr pSidOwner,
                IntPtr pSidGroup,
                out IntPtr pDacl,
                IntPtr pSacl,
                IntPtr pSecurityDescriptor);

            [DllImport("advapi32.dll", SetLastError = true)]
            public static extern uint SetSecurityInfo(
                IntPtr handle,
                SE_OBJECT_TYPE ObjectType,
                SECURITY_INFORMATION SecurityInfo,
                IntPtr psidOwner,
                IntPtr psidGroup,
                IntPtr pDacl,
                IntPtr pSacl);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern IntPtr GetCurrentProcess();
            #endregion
        }

        public Process Launch(ProcessLaunchOptions options, Action<Process>? onBeforeResume = null)
        {
            Process? process = null;

            var userName = Environment.UserName;

            Log.Debug("[{Component}] Preparing process launch for {Executable} with working dir {WorkingDirectory}",
                nameof(NativeProcessLauncher), options.ExecutablePath, options.WorkingDirectory);

            var pExplicitAccess = new PInvoke.EXPLICIT_ACCESS();
            PInvoke.BuildExplicitAccessWithName(
                ref pExplicitAccess,
                userName,
                PInvoke.STANDARD_RIGHTS_ALL | PInvoke.SPECIFIC_RIGHTS_ALL & ~PInvoke.PROCESS_VM_WRITE,
                PInvoke.GRANT_ACCESS,
                0);

            if (PInvoke.SetEntriesInAcl(1, ref pExplicitAccess, IntPtr.Zero, out var newAcl) != 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (!PInvoke.InitializeSecurityDescriptor(out var secDesc, PInvoke.SECURITY_DESCRIPTOR_REVISION))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (!PInvoke.SetSecurityDescriptorDacl(ref secDesc, true, newAcl, false))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var psecDesc = Marshal.AllocHGlobal(Marshal.SizeOf<PInvoke.SECURITY_DESCRIPTOR>());
            Marshal.StructureToPtr(secDesc, psecDesc, true);

            var lpProcessInformation = new PInvoke.PROCESS_INFORMATION();
            var lpEnvironment = IntPtr.Zero;

            try
            {
                var environment = options.EnvironmentVariables ?? new Dictionary<string, string>();
                if (environment.Count > 0)
                {
                    var formattedEnvironment = string.Join("\0", environment.Select(entry => $"{entry.Key}={entry.Value}")) + "\0\0";
                    lpEnvironment = Marshal.StringToHGlobalUni(formattedEnvironment);
                }

                var lpProcessAttributes = new PInvoke.SECURITY_ATTRIBUTES
                {
                    nLength = Marshal.SizeOf<PInvoke.SECURITY_ATTRIBUTES>(),
                    lpSecurityDescriptor = psecDesc,
                    bInheritHandle = false
                };

                var lpStartupInfo = new PInvoke.STARTUPINFO
                {
                    cb = Marshal.SizeOf<PInvoke.STARTUPINFO>()
                };

                var compatLayerPrev = Environment.GetEnvironmentVariable("__COMPAT_LAYER");
                Environment.SetEnvironmentVariable("__COMPAT_LAYER", BuildCompatLayer(options.DpiAwareness));

                Log.Debug("[{Component}] Creating suspended process. Arguments: {Arguments}",
                    nameof(NativeProcessLauncher), options.Arguments);

                if (!PInvoke.CreateProcess(
                        null,
                        $"\"{options.ExecutablePath}\" {options.Arguments}".Trim(),
                        ref lpProcessAttributes,
                        IntPtr.Zero,
                        false,
                        PInvoke.CREATE_SUSPENDED,
                        lpEnvironment,
                        options.WorkingDirectory,
                        ref lpStartupInfo,
                        out lpProcessInformation))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                Environment.SetEnvironmentVariable("__COMPAT_LAYER", compatLayerPrev);

                DisableSeDebug(lpProcessInformation.hProcess);

                process = new ExistingProcess(lpProcessInformation.hProcess);

                onBeforeResume?.Invoke(process);

                PInvoke.ResumeThread(lpProcessInformation.hThread);

                EnsureGameWindowReady(process);

                ApplySecurityDescriptor(lpProcessInformation.hProcess);

                Log.Debug("[{Component}] Process launched successfully with PID {Pid}",
                    nameof(NativeProcessLauncher), process.Id);

                return process;
            }
            catch
            {
                try
                {
                    process?.Kill();
                    if (process != null)
                    {
                        Log.Warning("[{Component}] Killed partially initialized process {Pid} after failure",
                            nameof(NativeProcessLauncher), process.Id);
                    }
                }
                catch
                {
                    // ignored as we're rethrowing the original exception
                }

                Log.Error("[{Component}] Failed to launch process {Executable}",
                    nameof(NativeProcessLauncher), options.ExecutablePath);

                throw;
            }
            finally
            {
                Marshal.FreeHGlobal(psecDesc);

                if (!IntPtr.Equals(lpEnvironment, IntPtr.Zero))
                {
                    Marshal.FreeHGlobal(lpEnvironment);
                }

                if (!IntPtr.Equals(lpProcessInformation.hThread, IntPtr.Zero))
                {
                    PInvoke.CloseHandle(lpProcessInformation.hThread);
                }
            }
        }

        private static string BuildCompatLayer(DpiAwareness dpiAwareness)
        {
            var compat = "RunAsInvoker ";
            compat += dpiAwareness switch
            {
                DpiAwareness.Aware => "HighDPIAware",
                DpiAwareness.Unaware => "DPIUnaware",
                _ => throw new ArgumentOutOfRangeException(nameof(dpiAwareness), dpiAwareness, "Unsupported DPI awareness value")
            };

            return compat;
        }

        private static void EnsureGameWindowReady(Process process)
        {
            try
            {
                do
                {
                    process.WaitForInputIdle();
                    Thread.Sleep(100);
                } while (IntPtr.Zero == TryFindGameWindow(process));
            }
            catch (InvalidOperationException)
            {
                throw new GameExitedException();
            }
        }

        private static void ApplySecurityDescriptor(IntPtr processHandle)
        {
            if (PInvoke.GetSecurityInfo(
                    PInvoke.GetCurrentProcess(),
                    PInvoke.SE_OBJECT_TYPE.SE_KERNEL_OBJECT,
                    PInvoke.SECURITY_INFORMATION.DACL_SECURITY_INFORMATION,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    out var pACL,
                    IntPtr.Zero,
                    IntPtr.Zero) != 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (PInvoke.SetSecurityInfo(
                    processHandle,
                    PInvoke.SE_OBJECT_TYPE.SE_KERNEL_OBJECT,
                    PInvoke.SECURITY_INFORMATION.DACL_SECURITY_INFORMATION |
                    PInvoke.SECURITY_INFORMATION.UNPROTECTED_DACL_SECURITY_INFORMATION,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    pACL,
                    IntPtr.Zero) != 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        private static void DisableSeDebug(IntPtr ProcessHandle)
        {
            if (!PInvoke.OpenProcessToken(ProcessHandle, PInvoke.TOKEN_QUERY | PInvoke.TOKEN_ADJUST_PRIVILEGES, out var TokenHandle))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var luidDebugPrivilege = new PInvoke.LUID();
            if (!PInvoke.LookupPrivilegeValue(null, "SeDebugPrivilege", ref luidDebugPrivilege))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var RequiredPrivileges = new PInvoke.PRIVILEGE_SET
            {
                PrivilegeCount = 1,
                Control = PInvoke.PRIVILEGE_SET_ALL_NECESSARY,
                Privilege = new PInvoke.LUID_AND_ATTRIBUTES[1]
            };

            RequiredPrivileges.Privilege[0].Luid = luidDebugPrivilege;
            RequiredPrivileges.Privilege[0].Attributes = PInvoke.SE_PRIVILEGE_ENABLED;

            if (!PInvoke.PrivilegeCheck(TokenHandle, ref RequiredPrivileges, out bool bResult))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (bResult) // SeDebugPrivilege is enabled; try disabling it
            {
                var TokenPrivileges = new PInvoke.TOKEN_PRIVILEGES
                {
                    PrivilegeCount = 1,
                    Privileges = new PInvoke.LUID_AND_ATTRIBUTES[1]
                };

                TokenPrivileges.Privileges[0].Luid = luidDebugPrivilege;
                TokenPrivileges.Privileges[0].Attributes = PInvoke.SE_PRIVILEGE_REMOVED;

                if (!PInvoke.AdjustTokenPrivileges(TokenHandle, false, ref TokenPrivileges, 0, IntPtr.Zero, 0))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }

            PInvoke.CloseHandle(TokenHandle);
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr hWndChildAfter, string className, IntPtr windowTitle);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool IsWindowVisible(IntPtr hWnd);

        private static IntPtr TryFindGameWindow(Process process)
        {
            IntPtr hwnd = IntPtr.Zero;
            while (IntPtr.Zero != (hwnd = FindWindowEx(IntPtr.Zero, hwnd, "FFXIVGAME", IntPtr.Zero)))
            {
                GetWindowThreadProcessId(hwnd, out uint pid);

                if (pid == process.Id && IsWindowVisible(hwnd))
                {
                    break;
                }
            }
            return hwnd;
        }
    }

    [Obsolete("Use NativeProcessLauncher directly instead.")]
    public static class NativeAclFix
    {
        private static readonly NativeProcessLauncher Launcher = new();

        public static Process LaunchGame(string workingDir, string exePath, string arguments, IDictionary<string, string> envVars, DpiAwareness dpiAwareness, Action<Process> beforeResume)
        {
            var options = new ProcessLaunchOptions(
                exePath,
                workingDir,
                arguments,
                new Dictionary<string, string>(envVars),
                dpiAwareness);

            return Launcher.Launch(options, beforeResume);
        }
    }

    public class WindowsGameRunner : IGameRunner
    {
        private readonly DalamudLauncher dalamudLauncher;
        private readonly bool dalamudOk;
        private readonly DirectoryInfo dotnetRuntimePath;
        private readonly IProcessLauncher nativeLauncher;

        public WindowsGameRunner(
            DalamudLauncher dalamudLauncher,
            bool dalamudOk,
            DirectoryInfo dotnetRuntimePath,
            IProcessLauncher? nativeLauncher = null)
        {
            this.dalamudLauncher = dalamudLauncher;
            this.dalamudOk = dalamudOk;
            this.dotnetRuntimePath = dotnetRuntimePath;
            this.nativeLauncher = nativeLauncher ?? new NativeProcessLauncher();
        }

        public Process Start(string path, string workingDirectory, string arguments, IDictionary<string, string> environment, DpiAwareness dpiAwareness)
        {
            if (dalamudOk)
            {
                var compat = "RunAsInvoker ";
                compat += dpiAwareness switch
                {
                    DpiAwareness.Aware => "HighDPIAware",
                    DpiAwareness.Unaware => "DPIUnaware",
                    _ => throw new ArgumentOutOfRangeException()
                };
                environment.Add("__COMPAT_LAYER", compat);

                var prevDalamudRuntime = Environment.GetEnvironmentVariable("DALAMUD_RUNTIME");
                if (string.IsNullOrWhiteSpace(prevDalamudRuntime))
                    environment.Add("DALAMUD_RUNTIME", dotnetRuntimePath.FullName);

                return this.dalamudLauncher.Run(new FileInfo(path), arguments, environment);
            }

            var options = new ProcessLaunchOptions(
                path,
                workingDirectory,
                arguments,
                new Dictionary<string, string>(environment),
                dpiAwareness);

            return nativeLauncher.Launch(options);
        }
    }

    public sealed class LauncherPaths
    {
        private const string DalamudPathEnvironmentVariable = "WEBLAUNCH_DALAMUD_PATH";
        private const string DefaultDalamudPath = @"D:\\HandleGame\\Dalamud";

        public LauncherPaths(string? dalamudPathOverride = null)
        {
            DalamudDirectory = ResolveDalamudDirectory(dalamudPathOverride);
        }

        public DirectoryInfo DalamudDirectory { get; }

        private static DirectoryInfo ResolveDalamudDirectory(string? overridePath)
        {
            var configuredPath = overridePath;

            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                configuredPath = Environment.GetEnvironmentVariable(DalamudPathEnvironmentVariable);
            }

            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                configuredPath = DefaultDalamudPath;
            }

            Log.Verbose("[{Component}] Resolving Dalamud path from value {ConfiguredPath}",
                nameof(LauncherPaths), configuredPath);

            if (!Directory.Exists(configuredPath))
            {
                Directory.CreateDirectory(configuredPath);
                Log.Information("[{Component}] Created Dalamud directory at {ConfiguredPath}",
                    nameof(LauncherPaths), configuredPath);
            }

            return new DirectoryInfo(configuredPath);
        }
    }

    public interface IHttpClientProvider
    {
        HttpClient Client { get; }
    }

    public sealed class SharedHttpClientProvider : IHttpClientProvider, IDisposable
    {
        private readonly HttpClient client;
        private bool disposed;

        public SharedHttpClientProvider(TimeSpan? timeout = null)
        {
            client = CreateClient(timeout ?? TimeSpan.FromSeconds(30));
            Log.Debug("[{Component}] Initialized HTTP client with timeout {TimeoutSeconds}s",
                nameof(SharedHttpClientProvider), client.Timeout.TotalSeconds);
        }

        public HttpClient Client
        {
            get
            {
                ThrowIfDisposed();
                return client;
            }
        }

        private static HttpClient CreateClient(TimeSpan timeout)
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };

            var httpClient = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = timeout
            };

            httpClient.DefaultRequestHeaders.ExpectContinue = false;
            return httpClient;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            client.Dispose();
            disposed = true;
            Log.Debug("[{Component}] Disposed shared HTTP client", nameof(SharedHttpClientProvider));
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(SharedHttpClientProvider));
            }
        }
    }

    internal sealed class FfxivHashBuilder
    {
        public string BuildHashString(string bootPath, bool is64BitOnly)
        {
            if (is64BitOnly)
            {
                return $"ffxivboot64.exe/{GenerateHash(Path.Combine(bootPath, "ffxivboot64.exe"))}," +
                       $"ffxivlauncher64.exe/{GenerateHash(Path.Combine(bootPath, "ffxivlauncher64.exe"))}," +
                       $"ffxivupdater64.exe/{GenerateHash(Path.Combine(bootPath, "ffxivupdater64.exe"))}";
            }

            var bootExecutable = File.Exists(Path.Combine(bootPath, "ffxivboot64.exe"))
                ? "ffxivboot64.exe"
                : "ffxivboot.exe";

            var launcherExecutable = File.Exists(Path.Combine(bootPath, "ffxivlauncher64.exe"))
                ? "ffxivlauncher64.exe"
                : "ffxivlauncher.exe";

            var updaterExecutable = File.Exists(Path.Combine(bootPath, "ffxivupdater64.exe"))
                ? "ffxivupdater64.exe"
                : "ffxivupdater.exe";

            return string.Join(",",
                $"{bootExecutable}/{GenerateHash(Path.Combine(bootPath, bootExecutable))}",
                $"{launcherExecutable}/{GenerateHash(Path.Combine(bootPath, launcherExecutable))}",
                $"{updaterExecutable}/{GenerateHash(Path.Combine(bootPath, updaterExecutable))}");
        }

        private static string GenerateHash(string file)
        {
            byte[] filebytes = File.ReadAllBytes(file);
            var hash = SHA1.HashData(filebytes);
            string hashstring = string.Join("", hash.Select(b => b.ToString("x2")).ToArray());
            long length = new FileInfo(file).Length;
            return length + "/" + hashstring;
        }
    }

    internal sealed record FfxivSidRequest(string Username, string Password, string? Otp, bool IsSteam);

    internal sealed class FfxivAuthenticationService
    {
        private static readonly Uri LoginBaseUri = new("https://ffxiv-login.square-enix.com");
        private static readonly Uri LoginTopUri = new(LoginBaseUri, "/oauth/ffxivarr/login/top?lng=en&rgn=3");
        private static readonly Uri SidEndpointUri = new(LoginBaseUri, "/oauth/ffxivarr/login/login.send");
        private static readonly Uri PatchBaseUri = new("https://patch-gamever.ffxiv.com/");

        private readonly HttpClient httpClient;
        private readonly string userAgent;

        public FfxivAuthenticationService(HttpClient httpClient, string userAgent)
        {
            this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            this.userAgent = userAgent ?? throw new ArgumentNullException(nameof(userAgent));
        }

        public async Task<string> FetchStoredValueAsync(bool isSteam, CancellationToken cancellationToken)
        {
            var requestUri = new Uri(LoginBaseUri,
                $"/oauth/ffxivarr/login/top?lng=en&rgn=3&isft=0&issteam={(isSteam ? 1 : 0)}");

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.TryAddWithoutValidation("user-agent", userAgent);

            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var storedMatch = Regex.Match(content, "\\t<\\s*input .* name=\"_STORED_\" value=\"(?<stored>.*)\">", RegexOptions.Compiled);

            if (!storedMatch.Success)
            {
                throw new InvalidOperationException("Unable to locate stored token in login page response.");
            }

            return storedMatch.Groups["stored"].Value;
        }

        public async Task<string> RequestSessionIdAsync(FfxivSidRequest request, string storedValue, CancellationToken cancellationToken)
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, SidEndpointUri);
            httpRequest.Headers.TryAddWithoutValidation("user-agent", userAgent);
            httpRequest.Headers.Referrer = new Uri(LoginBaseUri,
                $"/oauth/ffxivarr/login/top?lng=en&rgn=3&isft=0&issteam={(request.IsSteam ? 1 : 0)}");

            var form = new Dictionary<string, string>
            {
                { "_STORED_", storedValue },
                { "sqexid", request.Username },
                { "password", request.Password },
                { "otppw", request.Otp ?? string.Empty }
            };

            httpRequest.Content = new FormUrlEncodedContent(form);

            using var response = await httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            var sidMatch = Regex.Match(payload, "sid,(?<sid>.*),terms", RegexOptions.Compiled);
            if (!sidMatch.Success)
            {
                if (payload.Contains("ID or password is incorrect", StringComparison.OrdinalIgnoreCase))
                {
                    return "BAD";
                }

                throw new InvalidOperationException("Session ID response did not contain a valid SID.");
            }

            return sidMatch.Groups["sid"].Value;
        }

        public async Task<string> RequestUniqueIdentifierAsync(string gameVersion, string sessionId, string hashPayload, CancellationToken cancellationToken)
        {
            var requestUri = new Uri(PatchBaseUri,
                $"http/win32/ffxivneo_release_game/{gameVersion}/{sessionId}");

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = new StringContent(hashPayload, Encoding.UTF8, "application/x-www-form-urlencoded")
            };

            httpRequest.Headers.TryAddWithoutValidation("user-agent", userAgent);
            httpRequest.Headers.Referrer = new Uri(LoginTopUri, "");
            httpRequest.Headers.TryAddWithoutValidation("X-Hash-Check", "enabled");

            using var response = await httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new NoValidSubscriptionException("No active subscription found for this account");
            }

            response.EnsureSuccessStatusCode();

            if (!response.Headers.TryGetValues("X-Patch-Unique-Id", out var values))
            {
                throw new NoValidSubscriptionException("Failed to obtain unique ID from server");
            }

            return values.First();
        }
    }

    internal sealed class WorldStatusService
    {
        private static readonly Uri GateStatusUri = new("http://frontier.ffxiv.com/worldStatus/gate_status.json");
        private static readonly Uri LoginStatusUri = new("http://frontier.ffxiv.com/worldStatus/login_status.json");

        private readonly HttpClient httpClient;
        private readonly string userAgent;

        public WorldStatusService(HttpClient httpClient, string userAgent)
        {
            this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            this.userAgent = userAgent ?? throw new ArgumentNullException(nameof(userAgent));
        }

        public Task<bool> CheckGateStatusAsync(CancellationToken cancellationToken) => CheckStatusAsync(GateStatusUri, cancellationToken);

        public Task<bool> CheckLoginStatusAsync(CancellationToken cancellationToken) => CheckStatusAsync(LoginStatusUri, cancellationToken);

        private async Task<bool> CheckStatusAsync(Uri uri, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("user-agent", userAgent);

            try
            {
                using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var jsonData = JsonConvert.DeserializeObject<dynamic>(payload);
                return Convert.ToBoolean(jsonData.status);
            }
            catch (TaskCanceledException)
            {
                return true;
            }
            catch
            {
                return true;
            }
        }
    }


    public enum LoginAction
    {
        Game,
        GameNoDalamud,
        GameNoLaunch,
        Repair,
        Fake,
    }
    public enum ClientLanguage
    {
        Japanese,
        English,
        German,
        French
    }
    public class launchers
    {
        public Process? LaunchGame(IGameRunner runner, string sessionId, int region, int expansionLevel,
                   bool isSteamServiceAccount, string additionalArguments,
                   DirectoryInfo gamePath, bool isDx11, ClientLanguage language,
                   bool encryptArguments, DpiAwareness dpiAwareness)
{
    Log.Information(
        $"XivGame::LaunchGame(steamServiceAccount:{isSteamServiceAccount}, args:{additionalArguments})");

    var exePath = Path.Combine(gamePath.FullName, "game", "ffxiv_dx11.exe");

    var environment = new Dictionary<string, string>();

    var argumentBuilder = new ArgumentBuilder()
                          .Append("DEV.DataPathType", "1")
                          .Append("DEV.MaxEntitledExpansionID", expansionLevel.ToString())
                          .Append("DEV.TestSID", sessionId)
                          .Append("DEV.UseSqPack", "1")
                          .Append("SYS.Region", region.ToString())
                          .Append("language", ((int)language).ToString())
                          .Append("resetConfig", "0")
                          .Append("ver", Repository.Ffxiv.GetVer(gamePath));

    if (isSteamServiceAccount)
    {
        environment.Add("IS_FFXIV_LAUNCH_FROM_STEAM", "1");
        argumentBuilder.Append("IsSteam", "1");
    }

    if (!string.IsNullOrEmpty(additionalArguments))
    {
        var regex = new Regex(@"\s*(?<key>[^\s=]+)\s*=\s*(?<value>([^=]*$|[^=]*\s(?=[^\s=]+)))\s*", RegexOptions.Compiled);
        foreach (Match match in regex.Matches(additionalArguments))
            argumentBuilder.Append(match.Groups["key"].Value, match.Groups["value"].Value.Trim());
    }

    if (!File.Exists(exePath))
        throw new BinaryNotPresentException(exePath);

    var workingDir = Path.Combine(gamePath.FullName, "game");

    var arguments = encryptArguments
        ? argumentBuilder.BuildEncrypted()
        : argumentBuilder.Build();

    return runner.Start(exePath, workingDir, arguments, environment, dpiAwareness);
}

    }
    public class networklogic
    {
        private static Storage storage;

        public static CommonUniqueIdCache UniqueIdCache;
        private static readonly string UserAgentTemplate = "SQEXAuthor/2.0.0(Windows 6.2; ja-jp; {0})";
        public List<AddonEntry>? Addons { get; set; }
        static string DalamudRolloutBucket { get; set; }
        private static readonly string UserAgent = GenerateUserAgent();
        private static readonly LauncherPaths Paths = new();
        private static readonly SharedHttpClientProvider HttpClientProvider = new(TimeSpan.FromSeconds(30));
        private static readonly FfxivHashBuilder HashBuilder = new();
        private static readonly FfxivAuthenticationService AuthService = new(HttpClientProvider.Client, UserAgent);
        private static readonly WorldStatusService StatusService = new(HttpClientProvider.Client, UserAgent);
        public static DalamudUpdater DalamudUpdater { get; private set; }
        public static DalamudOverlayInfoProxy DalamudLoadInfo { get; private set; }
        

        public static Process LaunchGame(string gamePath, string realsid, int language, bool dx11, int expansionlevel, bool isSteam, int region)
        {
            return LaunchGameAsync(gamePath, realsid, language, dx11, expansionlevel, isSteam, region).GetAwaiter().GetResult();
        }

        public static async Task<Process?> LaunchGameAsync(string gamePath, string realsid, int language, bool dx11, int expansionlevel, bool isSteam, int region, CancellationToken cancellationToken = default)
{
    storage = new Storage("protocolhandle");
    var dalamudOk = false;
    var gameArgs = string.Empty;
    IDalamudRunner dalamudRunner;
    launchers launcher = new launchers();
    IDalamudCompatibilityCheck dalamudCompatCheck;
    dalamudRunner = new WindowsDalamudRunner();
    dalamudCompatCheck = new WindowsDalamudCompatibilityCheck();
    var dalamudpath = Paths.DalamudDirectory;
    Log.Information("[{Component}] Launch request received (DX11: {Dx11}, Region: {Region}, Expansion: {Expansion}, Steam: {IsSteam})",
        nameof(networklogic), dx11, region, expansionlevel, isSteam);
    Log.Debug("[{Component}] Using Dalamud path {DalamudPath}", nameof(networklogic), dalamudpath.FullName);
    Troubleshooting.LogTroubleshooting(gamePath);
    DirectoryInfo gamePather = new DirectoryInfo(gamePath);
    DalamudLoadInfo = new DalamudOverlayInfoProxy();

    try
    {
        DalamudUpdater = new DalamudUpdater(storage.GetFolder("dalamud"), storage.GetFolder("runtime"), 
            storage.GetFolder("dalamudAssets"), storage.Root, null, null)
        {
            Overlay = DalamudLoadInfo
        };
        Log.Information("[{Component}] Starting Dalamud updater", nameof(networklogic));
        DalamudUpdater.Run();
    }
    catch (Exception ex)
    {
        Log.Error(ex, "[{Component}] Could not start Dalamud updater", nameof(networklogic));
    }

    var dalamudLauncher = new DalamudLauncher(dalamudRunner, DalamudUpdater, DalamudLoadMethod.DllInject,
        gamePather, dalamudpath, (LibDalamud.ClientLanguage)ClientLanguage.English, 0, false, false, false,
        Troubleshooting.GetTroubleshootingJson(gamePath));

    try
    {
        dalamudCompatCheck.EnsureCompatibility();
        Log.Information("[{Component}] Dalamud compatibility check passed", nameof(networklogic));
    }
    catch (IDalamudCompatibilityCheck.NoRedistsException ex)
    {
        Log.Error(ex, "[{Component}] No Dalamud redistributables found", nameof(networklogic));
        throw;
    }
    catch (IDalamudCompatibilityCheck.ArchitectureNotSupportedException ex)
    {
        Log.Error(ex, "[{Component}] Architecture not supported", nameof(networklogic));
        throw;
    }

    try
    {
        try
        {
            Log.Debug("[{Component}] Holding Dalamud for update", nameof(networklogic));
            dalamudOk = dalamudLauncher.HoldForUpdate(gamePather) == DalamudLauncher.DalamudInstallState.Ok;
        }
        catch (DalamudRunnerException ex)
        {
            Log.Error(ex, "[{Component}] Couldn't ensure Dalamud runner", nameof(networklogic));
            throw;
        }

        IGameRunner runner;
        runner = new WindowsGameRunner(dalamudLauncher, dalamudOk, DalamudUpdater.Runtime);
        Log.Information("[{Component}] Launching game executable", nameof(networklogic));
        Process ffxivgame = launcher.LaunchGame(runner, realsid,
            region, expansionlevel, isSteam, gameArgs, gamePather, dx11, ClientLanguage.English, true,
            DpiAwareness.Unaware);

        var addonMgr = new AddonManager();
        try
        {
            List<AddonEntry> xex = new List<AddonEntry>();
            var addons = xex.Where(x => x.IsEnabled).Select(x => x.Addon).Cast<IAddon>().ToList();
            addonMgr.RunAddons(ffxivgame.Id, addons);
            Log.Debug("[{Component}] Started {AddonCount} addons", nameof(networklogic), addons.Count);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            addonMgr.StopAddons();
            throw;
        }

        Log.Debug("Waiting for game to exit");
        await Task.Run(() => ffxivgame!.WaitForExit(), cancellationToken).ConfigureAwait(false);
        Log.Verbose("Game has exited");

        if (addonMgr.IsRunning)
        {
            addonMgr.StopAddons();
            Log.Debug("[{Component}] Stopped addon manager", nameof(networklogic));
        }
            
        Log.Information("[{Component}] Game session complete", nameof(networklogic));
        return ffxivgame;
    }
    catch (Exception exc)
    {
        Log.Error(exc, "[{Component}] Game launch failed", nameof(networklogic));
        switch(language)
        {
            case 0:
                Debug.WriteLine("実行可能ファイルを起動できませんでした。 ゲームパスは正しいですか? " + exc);
                break;
            case 1:
                Debug.WriteLine("Could not launch executable. Is your game path correct? " + exc);
                break;
            case 2:
                Debug.WriteLine("Die ausführbare Datei konnte nicht gestartet werden. Ist dein Spielpfad korrekt? " + exc);
                break;
            case 3:
                Debug.WriteLine("Impossible de lancer l'exécutable. Votre chemin de jeu est-il correct? " + exc);
                break;
            case 4:
                Debug.WriteLine("Не удалось запустить файл. Ввели ли вы корректный путь к игре? " + exc);
                break;
        }
    }
    return null;
}


        public static string GetRealSid(string gamePath, string username, string password, string otp, bool isSteam)
        {
            return GetRealSidAsync(gamePath, username, password, otp, isSteam).GetAwaiter().GetResult();
        }

        public static Task<string> GetRealSidAsync(string gamePath, string username, string password, string? otp, bool isSteam, CancellationToken cancellationToken = default)
        {
            return GetRealSidInternalAsync(gamePath, username, password, otp, isSteam, cancellationToken);
        }

        private static async Task<string> GetRealSidInternalAsync(string gamePath, string username, string password, string? otp, bool isSteam, CancellationToken cancellationToken)
        {
            try
            {
                if (!Directory.Exists(gamePath))
                {
                    throw new DirectoryNotFoundException($"Game directory not found: {gamePath}");
                }

                var bootPath = Path.Combine(gamePath, "boot");
                bool is64BitOnly = !File.Exists(Path.Combine(bootPath, "ffxivlauncher.exe")) &&
                                   File.Exists(Path.Combine(bootPath, "ffxivlauncher64.exe"));

                Log.Debug("[{Component}] Generating hash payload (64-bit only: {Is64BitOnly})", nameof(networklogic), is64BitOnly);
                var hashPayload = HashBuilder.BuildHashString(bootPath, is64BitOnly);
                var gameVersion = await GetLocalGameverAsync(gamePath, cancellationToken).ConfigureAwait(false);

                if (gameVersion.Equals("BAD", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warning("[{Component}] Unable to read local game version", nameof(networklogic));
                    return "BAD";
                }

                var stored = await AuthService.FetchStoredValueAsync(isSteam, cancellationToken).ConfigureAwait(false);
                Log.Verbose("[{Component}] Retrieved stored login token", nameof(networklogic));
                var sessionId = await AuthService.RequestSessionIdAsync(
                    new FfxivSidRequest(username, password, otp, isSteam),
                    stored,
                    cancellationToken).ConfigureAwait(false);

                if (sessionId.Equals("BAD", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warning("[{Component}] Authentication failed when requesting session id", nameof(networklogic));
                    return "BAD";
                }

                var uniqueId = await AuthService
                    .RequestUniqueIdentifierAsync(gameVersion, sessionId, hashPayload, cancellationToken)
                    .ConfigureAwait(false);
                Log.Information("[{Component}] Acquired unique session identifier", nameof(networklogic));
                return uniqueId;
            }
            catch (NoValidSubscriptionException ex)
            {
                Log.Warning(ex, "[{Component}] Subscription validation failed", nameof(networklogic));
                Console.WriteLine($"Subscription Error: {ex.Message}");
                return "BAD";
            }
            catch (Exception exc)
            {
                Log.Error(exc, "[{Component}] Failed to obtain session identifier", nameof(networklogic));
                Console.WriteLine($"GetRealSid Error: {exc.Message}");
                Console.WriteLine($"Stack trace: {exc.StackTrace}");
                return "BAD";
            }
        }

        private static async Task<string> GetLocalGameverAsync(string gamePath, CancellationToken cancellationToken)
        {
            try
            {
                await using var stream = new FileStream(Path.Combine(gamePath, "game", "ffxivgame.ver"), FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
                using var reader = new StreamReader(stream);
                var contents = await reader.ReadToEndAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return contents;
            }
            catch (Exception exc)
            {
                Debug.WriteLine("Unable to get local game version.\n" + exc);
                Log.Warning(exc, "[{Component}] Unable to open ffxivgame.ver", nameof(networklogic));
                return "BAD";
            }
        }

        public static bool CheckGateStatus() =>
            StatusService.CheckGateStatusAsync(CancellationToken.None).GetAwaiter().GetResult();

        public static bool CheckLoginStatus() =>
            StatusService.CheckLoginStatusAsync(CancellationToken.None).GetAwaiter().GetResult();

        public static Task<bool> CheckGateStatusAsync(CancellationToken cancellationToken = default) =>
            StatusService.CheckGateStatusAsync(cancellationToken);

        public static Task<bool> CheckLoginStatusAsync(CancellationToken cancellationToken = default) =>
            StatusService.CheckLoginStatusAsync(cancellationToken);

        private static string GenerateUserAgent()
        {
            return string.Format(UserAgentTemplate, MakeComputerId());
        }

        private static string MakeComputerId()
        {
            var hashString = Environment.MachineName + Environment.UserName + Environment.OSVersion +
                             Environment.ProcessorCount;

            using var sha1 = SHA1.Create();
            var bytes = new byte[5];

            Array.Copy(sha1.ComputeHash(Encoding.Unicode.GetBytes(hashString)), 0, bytes, 1, 4);

            var checkSum = (byte)-(bytes[1] + bytes[2] + bytes[3] + bytes[4]);
            bytes[0] = checkSum;

            return BitConverter.ToString(bytes).Replace("-", "").ToLower();
        }
    }
}