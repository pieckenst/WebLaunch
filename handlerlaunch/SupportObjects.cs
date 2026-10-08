using Newtonsoft.Json;
using WebLaunch.Core;
using WMConsole;
using GamePlugins.Spellborn;
namespace handlerlaunch
{
    public class Update
    {
        [JsonProperty("applies_to")]
        public string appliesTo { get; set; }

        [JsonProperty("version")]
        public string version { get; set; }

        [JsonProperty("file")]
        public string file { get; set; }

        [JsonProperty("patchnotes")]
        public string patchnotes { get; set; }

        [JsonProperty("checksum")]
        public string checksum { get; set; }

        [JsonProperty("server")]
        public string files { get; set; }

        [JsonProperty("enabled")]
        public string enabled { get; set; }
    }
    public class UpdateJson
    {
        [JsonProperty("update")]
        public Update Update { get; set; }
    }

    // Compatibility facades; all parsing and installation use the shared implementations.
    public class SpellbornSupporter
    {
        public string passOverFromWeb = "";
        public string GetGamePathFromArgs(string[] args) => Parse(args).GamePath;
        private static LaunchRequest Parse(string[] args) => args.Length == 1
            ? LegacyProtocol.Parse(args[0], Program.Trust?.LegacyUntil, DateTimeOffset.UtcNow)
            : throw new ArgumentException("Expected one launch link.");
        public void StartupRoutine(string[] args)
        {
            var request = Parse(args);
            try { (Program.Launcher ?? throw new InvalidOperationException("Start the desktop host first.")).LaunchAsync(request, new Progress<LaunchStatus>(), CancellationToken.None).GetAwaiter().GetResult(); }
            finally { request.ClearCredentials(); }
        }
        public void UnzipFile(string file, string version) => new ArchiveInstaller().InstallAsync(file, passOverFromWeb, CancellationToken.None,
            () => new SpellbornVersionStore().Write(passOverFromWeb, version)).GetAwaiter().GetResult();
    }
    public class FFXIVhandler
    {
        public static void HandleFFXivReq(string[] args) => new SpellbornSupporter().StartupRoutine(args);
    }
}
