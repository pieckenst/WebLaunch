using WebLaunch.Core;
using Xunit;

namespace WebLaunch.Tests;
public sealed class DiagnosticsTests
{
    private sealed class SyntheticLauncher(string outcome) : ILaunchService
    {
        public Task LaunchAsync(LaunchRequest request, IProgress<LaunchStatus> progress, CancellationToken token)
        {
            progress.Report(new("authenticating", "synthetic-secret-must-not-leak"));
            progress.Report(new("launched", "unconfirmed process", true, true));
            return outcome switch
            {
                "failed" => Task.FromException(new IOException("synthetic-password")),
                "cancelled" => Task.FromCanceled(new CancellationToken(true)),
                _ => Task.CompletedTask
            };
        }
    }
    private sealed class Progress(Action<LaunchStatus> report) : IProgress<LaunchStatus>
    {
        public void Report(LaunchStatus value) => report(value);
    }
    [Theory][InlineData("launched")][InlineData("failed")][InlineData("cancelled")]
    public async Task ObserverReportsRealTerminalOutcomeAndNeverPluginText(string outcome)
    {
        var states = new List<LaunchStatus>(); var ids = new List<string>();
        var request = new LaunchRequest { Game = "spellborn", GamePath = @"C:\Games" };
        var service = new ObservedLaunchService(new SyntheticLauncher(outcome), (id, status) => { ids.Add(id); states.Add(status); });
        var error = await Record.ExceptionAsync(() => service.LaunchAsync(request, new Progress(_ => { }), default));
        Assert.Equal(outcome == "launched", error is null);
        Assert.Equal(outcome, states[^1].State);
        Assert.All(states.SkipLast(1), state => Assert.False(state.Completed || state.Success));
        Assert.All(states, state => Assert.DoesNotContain("synthetic", state.Message));
        Assert.All(ids, id => Assert.Equal(request.RequestId, id));
    }
    [Fact] public async Task BrokenNotificationsCannotFailLaunch()
    {
        var service = new ObservedLaunchService(new SyntheticLauncher("launched"), (_, _) => throw new IOException());
        await service.LaunchAsync(new() { Game = "spellborn", GamePath = @"C:\Games" }, new Progress(_ => { }), default);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void DebugLogsUseOnlyControlledFieldsAndRotate(bool debug)
    {
        using var temp = new TemporaryDirectory();
        var log = new SafeDiagnosticLog(temp.Path, debug);
        log.Write(DiagnosticEvent.PluginDebug, "synthetic-secret", new("synthetic-secret", "synthetic-password"), "synthetic-secret");
        log.Write(DiagnosticEvent.LaunchState, "synthetic-secret", new("authenticating", "synthetic-password"), "synthetic-secret");
        var file = Path.Combine(temp.Path, "desktop.log");
        var text = File.ReadAllText(file);
        Assert.DoesNotContain("synthetic", text);
        Assert.Equal(debug, text.Contains("PluginDebug"));
        Assert.Contains("state=authenticating", text);
        Assert.Contains("plugin=external", text);
        File.WriteAllText(file, new string('x', 1_048_576));
        log.Write(DiagnosticEvent.HostReady);
        Assert.True(File.Exists(file + ".1"));
        Assert.True(new FileInfo(file).Length < 1024);
        Assert.Equal(2, Directory.GetFiles(temp.Path).Length);
    }
}
