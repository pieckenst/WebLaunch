using System;
using System.Collections.Generic;
using System.Threading;
using Serilog;

namespace XIVLauncher.Common.Addon
{
    public class AddonManager
    {
        private sealed class RunningAddon(IAddon addon)
        {
            public IAddon Addon { get; } = addon;
            public Thread? Thread { get; set; }
            public CancellationTokenSource? Cancellation { get; set; }
        }
        private List<RunningAddon>? _runningAddons;
        private TaskCompletionSource<Exception> failure = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsRunning { get; private set; }
        public Task<Exception> Failure => failure.Task;

        public void RunAddons(int gamePid, List<IAddon> addonEntries, CancellationToken cancellationToken = default)
        {
            if (_runningAddons != null)
                throw new Exception("Addons still running?");

            failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _runningAddons = new List<RunningAddon>();
            // The session serializes startup and cleanup; cancellation can arrive during either.
            foreach (var addonEntry in addonEntries)
            {
                CheckCancellation();
                var running = new RunningAddon(addonEntry);
                _runningAddons.Add(running); // Track partial setup for startup rollback, exactly once.
                addonEntry.Setup(gamePid);
                CheckCancellation();

                if (addonEntry is IPersistentAddon persistentAddon)
                {
                    Log.Information("Starting PersistentAddon {0}", persistentAddon.Name);
                    var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    running.Cancellation = cancellation;
                    var addonThread = new Thread(() =>
                    {
                        try
                        {
                            cancellation.Token.ThrowIfCancellationRequested();
                            persistentAddon.DoWork(cancellation.Token);
                        }
                        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                        catch (Exception ex) { failure.TrySetResult(ex); }
                    });
                    CheckCancellation();
                    addonThread.Start();
                    running.Thread = addonThread;
                }

                CheckCancellation();
                if (addonEntry is IRunnableAddon runnableAddon)
                {
                    Log.Information("Starting RunnableAddon {0}", runnableAddon.Name);
                    runnableAddon.Run();
                }
            }
            CheckCancellation();
            IsRunning = true;

            void CheckCancellation()
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Failure.IsCompletedSuccessfully)
                    throw new InvalidOperationException("Persistent addon failed.", Failure.Result);
            }
        }

        public void StopAddons()
        {
            Log.Information("Stopping addons...");
            var running = _runningAddons;
            _runningAddons = null;
            IsRunning = false;
            if (running is null) return;

            var errors = new List<Exception>();
            // Signal every worker before joining any of them, even if a callback fails.
            foreach (var addon in running)
                Cleanup(() => addon.Cancellation?.Cancel());
            foreach (var addon in running)
            {
                Cleanup(() => addon.Thread?.Join());
                if (addon.Addon is INotifyAddonAfterClose notifiedAddon)
                    Cleanup(notifiedAddon.GameClosed);
                Cleanup(() => addon.Cancellation?.Dispose());
            }
            if (errors.Count > 0) throw new AggregateException("Addon cleanup failed.", errors);

            void Cleanup(Action action)
            {
                try { action(); }
                catch (Exception ex) { errors.Add(ex); }
            }
        }
    }
}
