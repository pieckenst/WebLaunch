using System.Collections.Concurrent;
using System.Diagnostics;
using XIVLauncher.Common.Addon;

namespace CoreLibLaunchSupport;

/// <summary>Owns Dalamud addon lifetime independently of the Elgarnan plugin host and launch request.</summary>
public sealed class DalamudGameSession
{
    private static readonly ConcurrentDictionary<int, DalamudGameSession> Active = new();
    private readonly Process process;
    private readonly AddonManager addons;
    private readonly CancellationTokenSource stopping = new();
    private readonly object stopLock = new();
    private bool stopped;
    public Task Completion { get; private set; } = Task.CompletedTask;
    private DalamudGameSession(Process process, AddonManager addons) { this.process = process; this.addons = addons; }

    public static DalamudGameSession Start(Process game, IEnumerable<IAddon> enabledAddons)
    {
        // Own a separate handle: disposing the launch result must not invalidate the exit observer.
        var observer = Process.GetProcessById(game.Id);
        _ = observer.Handle;
        var manager = new AddonManager();
        var session = new DalamudGameSession(observer, manager);
        if (!Active.TryAdd(game.Id, session)) { observer.Dispose(); throw new InvalidOperationException("Game session already tracked."); }
        try
        {
            manager.RunAddons(game.Id, enabledAddons.ToList());
            session.Completion = session.ObserveExitAsync(game.Id);
            return session;
        }
        catch
        {
            Active.TryRemove(game.Id, out _);
            manager.StopAddons(); observer.Dispose(); session.stopping.Dispose();
            throw;
        }
    }
    private async Task ObserveExitAsync(int pid)
    {
        try { await process.WaitForExitAsync(stopping.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        finally
        {
            try { addons.StopAddons(); }
            finally
            {
                Active.TryRemove(pid, out _); process.Dispose();
                lock (stopLock) { stopped = true; stopping.Dispose(); }
            }
        }
    }
    public async Task StopAsync()
    {
        lock (stopLock) { if (!stopped) stopping.Cancel(); }
        await Completion.ConfigureAwait(false);
    }
    public static Task StopAllAsync() => Task.WhenAll(Active.Values.Select(session => session.StopAsync()));
}
