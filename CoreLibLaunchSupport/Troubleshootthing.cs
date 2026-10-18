using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text;
using Newtonsoft.Json;
using Serilog;
using LibDalamud.Common.Dalamud;

using LibDalamud.Common.Util;

namespace CoreLibLaunchSupport
{
    
    public static class RepoExtensions
    {
        public const string BASE_GAME_VERSION = "2012.01.01.0000.0000";
        private static DirectoryInfo GetRepoPath(this Repository repo, DirectoryInfo gamePath)
        {
            switch (repo)
            {
                case Repository.Boot:
                    return new DirectoryInfo(Path.Combine(gamePath.FullName, "boot"));
                case Repository.Ffxiv:
                    return new DirectoryInfo(Path.Combine(gamePath.FullName, "game"));
                case Repository.Ex1:
                    return new DirectoryInfo(Path.Combine(gamePath.FullName, "game", "sqpack", "ex1"));
                case Repository.Ex2:
                    return new DirectoryInfo(Path.Combine(gamePath.FullName, "game", "sqpack", "ex2"));
                case Repository.Ex3:
                    return new DirectoryInfo(Path.Combine(gamePath.FullName, "game", "sqpack", "ex3"));
                case Repository.Ex4:
                    return new DirectoryInfo(Path.Combine(gamePath.FullName, "game", "sqpack", "ex4"));
                default:
                    throw new ArgumentOutOfRangeException(nameof(repo), repo, null);
            }
        }

        public static FileInfo GetVerFile(this Repository repo, DirectoryInfo gamePath, bool isBck = false)
        {
            var repoPath = repo.GetRepoPath(gamePath).FullName;
            switch (repo)
            {
                case Repository.Boot:
                    return new FileInfo(Path.Combine(repoPath, "ffxivboot" + (isBck ? ".bck" : ".ver")));
                case Repository.Ffxiv:
                    return new FileInfo(Path.Combine(repoPath, "ffxivgame" + (isBck ? ".bck" : ".ver")));
                case Repository.Ex1:
                    return new FileInfo(Path.Combine(repoPath, "ex1" + (isBck ? ".bck" : ".ver")));
                case Repository.Ex2:
                    return new FileInfo(Path.Combine(repoPath, "ex2" + (isBck ? ".bck" : ".ver")));
                case Repository.Ex3:
                    return new FileInfo(Path.Combine(repoPath, "ex3" + (isBck ? ".bck" : ".ver")));
                case Repository.Ex4:
                    return new FileInfo(Path.Combine(repoPath, "ex4" + (isBck ? ".bck" : ".ver")));
                default:
                    throw new ArgumentOutOfRangeException(nameof(repo), repo, null);
            }
        }

        public static string GetVer(this Repository repo, DirectoryInfo gamePath, bool isBck = false)
        {
            var verFile = repo.GetVerFile(gamePath, isBck);

            if (!verFile.Exists)
                return BASE_GAME_VERSION;

            var ver = File.ReadAllText(verFile.FullName);
            return string.IsNullOrWhiteSpace(ver) ? BASE_GAME_VERSION : ver;
        }

        public static void SetVer(this Repository repo, DirectoryInfo gamePath, string newVer, bool isBck = false)
        {
            var verFile = GetVerFile(repo, gamePath, isBck);

            if (!verFile.Directory.Exists)
                verFile.Directory.Create();

            using var fileStream = verFile.Open(FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = Encoding.ASCII.GetBytes(newVer);
            fileStream.Write(buffer, 0, buffer.Length);
            fileStream.Flush();
        }

        public static bool IsBaseVer(this Repository repo, DirectoryInfo gamePath)
        {
            return repo.GetVer(gamePath) == BASE_GAME_VERSION;
        }

        // TODO
        public static string GetRepoHash(this Repository repo)
        {
            switch (repo)
            {
                case Repository.Boot:
                    return null;
                case Repository.Ffxiv:
                    return null;
                case Repository.Ex1:
                    return null;
                case Repository.Ex2:
                    return null;
                case Repository.Ex3:
                    return null;
                case Repository.Ex4:
                    return null;
                default:
                    throw new ArgumentOutOfRangeException(nameof(repo), repo, null);
            }
        }
    }
    public enum Repository
    {
        Boot,
        Ffxiv,
        Ex1,
        Ex2,
        Ex3,
        Ex4
    }
    public static class Troubleshooting
    {
        /// <summary>
        /// Gets the most recent exception to occur.
        /// </summary>
        public static Exception? LastException { get; private set; }
        /// <summary>
        /// Log the last exception in a parseable format to serilog.
        /// </summary>
        /// <param name="exception">The exception to log.</param>
        /// <param name="context">Additional context.</param>
        private class ExceptionPayload
        {
            public DateTime When { get; set; }

            public string Info { get; set; }

            public string Context { get; set; }
        }
        public enum Platform
        {
            Win32,
            Win32OnLinux,
            Mac,
        }
        private class TroubleshootingPayload
        {
            public DateTime When { get; set; }

            public bool IsDx11 { get; set; }

            public bool IsAutoLogin { get; set; }

            public bool IsUidCache { get; set; }

            public bool DalamudEnabled { get; set; }

            public DalamudLoadMethod DalamudLoadMethod { get; set; }

            public decimal DalamudInjectionDelay { get; set; }

            public bool SteamIntegration { get; set; }

            public bool EncryptArguments { get; set; }

            public string LauncherVersion { get; set; }

            public string LauncherHash { get; set; }

            public bool Official { get; set; }

            public DpiAwareness DpiAwareness { get; set; }

            public Platform Platform { get; set; }

            public string ObservedGameVersion { get; set; }

            public string ObservedEx1Version { get; set; }
            public string ObservedEx2Version { get; set; }
            public string ObservedEx3Version { get; set; }
            public string ObservedEx4Version { get; set; }

            public bool BckMatch { get; set; }

            public enum IndexIntegrityResult
            {
                Failed,
                Exception,
                NoGame,
                ReferenceNotFound,
                ReferenceFetchFailure,
                Success,
            }

            public IndexIntegrityResult IndexIntegrity { get; set; }
        }
        public static void LogException(Exception exception, string context)
        {
            LastException = exception;
            if (!FfxivTraceLogger.TraceEnabled)
            {
                Log.Warning("Detailed FFXIV exception diagnostics suppressed; set FFXIV_TRACE=1 to capture a local trace.");
                return;
            }

            try
            {
                var fixedContext = context?.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                var payload = new ExceptionPayload
                {
                    Context = fixedContext,
                    When = DateTime.Now,
                    Info = exception.ToString(),
                };
                var encodedPayload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(payload)));
                FfxivTraceLogger.Trace(FfxivTraceLogger.CorrelationOrNone, $"LASTEXCEPTION:{encodedPayload}");
            }
            catch (Exception ex)
            {
                Log.Error("Could not capture FFXIV exception diagnostics ({ErrorType})", ex.GetType().Name);
                FfxivTraceLogger.Trace(FfxivTraceLogger.CorrelationOrNone, $"Exception diagnostic capture failed: {ex}");
            }
        }
        internal static void LogTroubleshooting(string gamepath)
        {
            if (!FfxivTraceLogger.TraceEnabled)
            {
                Log.Debug("Detailed game integrity diagnostics omitted; set FFXIV_TRACE=1 to capture a local trace.");
                return;
            }

            try
            {
                var payload = GetTroubleshootingJson(gamepath);
                FfxivTraceLogger.Trace(FfxivTraceLogger.CorrelationOrNone, $"TROUBLESHXLTING:{payload}");
            }
            catch (Exception ex)
            {
                Log.Error("Could not capture game integrity diagnostics ({ErrorType})", ex.GetType().Name);
                FfxivTraceLogger.Trace(FfxivTraceLogger.CorrelationOrNone, $"Troubleshooting capture failed: {ex}");
            }
        }
        internal static string GetTroubleshootingJson(string gamepath)
        {
            var gamePather = gamepath;
            DirectoryInfo gamePath = new DirectoryInfo(gamePather);



            var ffxivVer = Repository.Ffxiv.GetVer(gamePath);
            var ffxivVerBck = Repository.Ffxiv.GetVer(gamePath, true);
            var ex1Ver = Repository.Ex1.GetVer(gamePath);
            var ex1VerBck = Repository.Ex1.GetVer(gamePath, true);
            var ex2Ver = Repository.Ex2.GetVer(gamePath);
            var ex2VerBck = Repository.Ex2.GetVer(gamePath, true);
            var ex3Ver = Repository.Ex3.GetVer(gamePath);
            var ex3VerBck = Repository.Ex3.GetVer(gamePath, true);
            var ex4Ver = Repository.Ex4.GetVer(gamePath);
            var ex4VerBck = Repository.Ex4.GetVer(gamePath, true);

            var payload = new TroubleshootingPayload
            {
                When = DateTime.Now,
                
                
                
                Platform = (Platform)PlatformHelpers.GetPlatform(),

                ObservedGameVersion = ffxivVer,
                ObservedEx1Version = ex1Ver,
                ObservedEx2Version = ex2Ver,
                ObservedEx3Version = ex3Ver,
                ObservedEx4Version = ex4Ver,

                BckMatch = ffxivVer == ffxivVerBck && ex1Ver == ex1VerBck && ex2Ver == ex2VerBck &&
                           ex3Ver == ex3VerBck && ex4Ver == ex4VerBck,

                
            };

            return JsonConvert.SerializeObject(payload);
        }
    }
}
