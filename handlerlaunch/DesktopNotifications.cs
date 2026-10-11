using System.IO;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using WebLaunch.Core;

namespace handlerlaunch;

internal sealed class DesktopNotifications(string mode)
{
    private readonly object gate = new();
    private readonly Dictionary<string, string> phases = [];
    public void Report(string requestId, LaunchStatus status)
    {
        var safe = LaunchPresentation.Normalize(status);
        lock (gate)
        {
            if (phases.TryGetValue(requestId, out var previous) && previous == safe.State) return;
            var first = !phases.ContainsKey(requestId);
            phases[requestId] = safe.State;
            if (phases.Count > 64) phases.Remove(phases.Keys.First());
            try
            {
                if (!File.Exists(NotificationRegistration.Shortcut))
                { DesktopDiagnostics.Write(DiagnosticEvent.NotificationUnavailable, requestId); return; }
                var notifier = ToastNotificationManager.CreateToastNotifier(NotificationRegistration.AppId);
                if (notifier.Setting != NotificationSetting.Enabled)
                { DesktopDiagnostics.Write(DiagnosticEvent.NotificationUnavailable, requestId); return; }
                var xml = new XmlDocument();
                var desktopMode = mode == "gui" ? "gui" : "console";
                var progress = safe.Completed ? "" : "<progress title='Launch progress' value='indeterminate' status='In progress'/>";
                xml.LoadXml($"<toast activationType='protocol' launch='HandleWebRequest:connect?v=2&amp;mode={desktopMode}'><visual><binding template='ToastGeneric'><text>WebLaunch</text><text>{safe.Message}</text>{progress}</binding></visual><audio silent='true'/></toast>");
                var toast = new ToastNotification(xml)
                {
                    Tag = requestId[..16], Group = "launch", SuppressPopup = mode == "quiet" || (!first && !safe.Completed),
                    ExpirationTime = DateTimeOffset.Now.AddMinutes(safe.Completed ? 5 : 30)
                };
                toast.Failed += (_, _) => DesktopDiagnostics.Write(DiagnosticEvent.NotificationFailed, requestId);
                notifier.Show(toast);
                DesktopDiagnostics.Write(DiagnosticEvent.NotificationShown, requestId, safe);
            }
            catch { DesktopDiagnostics.Write(DiagnosticEvent.NotificationFailed, requestId); }
        }
    }
}
