using System.Runtime.InteropServices;
using Microsoft.Win32;
using WebLaunch.Bridge;

namespace handlerlaunch;

/// <summary>One user-requested native folder chooser, including in console mode.</summary>
internal sealed class WindowsGameFolderPicker : IGameFolderPicker
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<string?> SelectAsync(CancellationToken cancellationToken)
    {
        if (!await gate.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("A folder chooser is already open.");
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        uint threadId = 0;
        var thread = new Thread(() =>
        {
            try
            {
                Volatile.Write(ref threadId, GetCurrentThreadId());
                cancellationToken.ThrowIfCancellationRequested();
                var dialog = new OpenFolderDialog { Title = "WebLaunch — choose your game installation folder", Multiselect = false };
                var selected = dialog.ShowDialog() == true ? dialog.FolderName : null;
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult(selected);
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(cancellationToken); }
            catch (Exception ex) { completion.TrySetException(ex); }
            finally { gate.Release(); }
        }) { IsBackground = true, Name = "WebLaunch folder chooser" };
        thread.SetApartmentState(ApartmentState.STA);
        try { thread.Start(); }
        catch { gate.Release(); throw; }
        // A common dialog runs a native modal message loop. Close only windows on
        // this dedicated thread when the request expires or the desktop shuts down.
        var closer = new System.Threading.Timer(_ =>
        {
            var id = Volatile.Read(ref threadId);
            if (cancellationToken.IsCancellationRequested && !completion.Task.IsCompleted && id != 0)
                EnumThreadWindows(id, (window, _) => { PostMessage(window, 0x0010, 0, 0); return true; }, 0);
        }, null, 100, 100);
        _ = completion.Task.ContinueWith(_ => closer.Dispose(), TaskScheduler.Default);
        return await completion.Task.WaitAsync(cancellationToken);
    }
    private delegate bool EnumWindow(nint window, nint parameter);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint threadId, EnumWindow callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}
