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
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool stopped;
    public Task Completion => completion.Task;
    private DalamudGameSession(Process process, AddonManager addons) { this.process = process; this.addons = addons; }

    public static DalamudGameSession Start(Process game, IEnumerable<IAddon> enabledAddons)
    {
        // Own a separate handle: disposing the launch result must not invalidate the exit observer.
        var pid = game.Id;
        var observer = Process.GetProcessById(pid);
        _ = observer.Handle;
        var manager = new AddonManager();
        var session = new DalamudGameSession(observer, manager);
        if (!Active.TryAdd(pid, session))
        {
            observer.Dispose(); session.stopping.Dispose();
            throw new InvalidOperationException("Game session already tracked.");
        }
        try
        {
            manager.RunAddons(pid, enabledAddons.ToList(), session.stopping.Token);
        }
        catch (Exception ex)
        {
            session.Finish(pid, ex);
            session.Completion.GetAwaiter().GetResult(); // Propagate startup and rollback errors after all cleanup.
            throw;
        }
        _ = session.ObserveExitAsync(pid);
        return session;
    }
    private async Task ObserveExitAsync(int pid)
    {
        Exception? failure = null;
        using var exitCancellation = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
        Task exited = Task.CompletedTask;
        try
        {
            exited = process.WaitForExitAsync(exitCancellation.Token);
            var finished = await Task.WhenAny(exited, addons.Failure).ConfigureAwait(false);
            if (finished == addons.Failure) failure = await addons.Failure.ConfigureAwait(false);
            else await exited.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception ex) { failure = ex; }
        finally
        {
            // Remove the process observer even when a worker fails while the game keeps running.
            exitCancellation.Cancel();
            try { await exited.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { failure ??= ex; }
            Finish(pid, failure);
        }
    }
    private void Finish(int pid, Exception? failure)
    {
        try { addons.StopAddons(); }
        catch (Exception ex) { failure = failure is null ? ex : new AggregateException(failure, ex); }
        finally
        {
            process.Dispose();
            lock (stopLock) { stopped = true; stopping.Dispose(); }
            if (failure is null) completion.TrySetResult();
            else completion.TrySetException(failure);
            Active.TryRemove(pid, out _);
        }
    }
    public async Task StopAsync()
    {
        Task cancellation;
        lock (stopLock) { cancellation = stopped ? Task.CompletedTask : stopping.CancelAsync(); }
        Exception? cancellationFailure = null;
        try { await cancellation.ConfigureAwait(false); }
        catch (Exception ex) { cancellationFailure = ex; }
        try { await Completion.ConfigureAwait(false); }
        catch (Exception ex) when (cancellationFailure is not null) { throw new AggregateException(cancellationFailure, ex); }
        if (cancellationFailure is not null) throw cancellationFailure;
    }
    public static Task StopAllAsync() => Task.WhenAll(Active.Values.Select(session => session.StopAsync()));
}
