using System.IO;
using System.Runtime.InteropServices;
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
            var stage = "validate-registration";
            try
            {
                if (!File.Exists(NotificationRegistration.Shortcut))
                { DesktopDiagnostics.Write(DiagnosticEvent.NotificationUnavailable, requestId, detail: "start-menu-shortcut-missing"); return; }

                stage = "create-notifier";
                var notifier = ToastNotificationManager.CreateToastNotifier(NotificationRegistration.AppId);
                stage = "read-notification-setting";
                try
                {
                    var setting = notifier.Setting;
                    if (setting != NotificationSetting.Enabled)
                    {
                        DesktopDiagnostics.Write(DiagnosticEvent.NotificationUnavailable, requestId, detail: "notification-setting-" + setting);
                        return;
                    }
                }
                catch (COMException ex) when (ex.HResult == unchecked((int)0x80070490))
                {
                    // Some Windows builds return ELEMENT_NOT_FOUND while querying the setting for
                    // a valid Start-menu AUMID. Do not silently disable notifications on that query
                    // alone: attempt Show and let the toast API provide the definitive result.
                    DesktopDiagnostics.Write(
                        DiagnosticEvent.NotificationUnavailable,
                        requestId,
                        detail: "notification-setting-query-unavailable-hr-80070490");
                }

                stage = "build-toast";
                var xml = new XmlDocument();
                var desktopMode = mode == "gui" ? "gui" : "console";
                var progress = safe.Completed ? "" : "<progress title='Launch progress' value='indeterminate' status='In progress'/>";
                var safeMessage = System.Security.SecurityElement.Escape(safe.Message) ?? "";
                xml.LoadXml($"<toast activationType='protocol' launch='HandleWebRequest:connect?v=2&amp;mode={desktopMode}'><visual><binding template='ToastGeneric'><text>WebLaunch</text><text>{safeMessage}</text>{progress}</binding></visual><audio silent='true'/></toast>");
                var toast = new ToastNotification(xml)
                {
                    Tag = requestId[..16], Group = "launch", SuppressPopup = mode == "quiet" || (!first && !safe.Completed),
                    ExpirationTime = DateTimeOffset.Now.AddMinutes(safe.Completed ? 5 : 30)
                };
                toast.Failed += (_, _) => DesktopDiagnostics.Write(DiagnosticEvent.NotificationFailed, requestId, detail: "toast-failed-event");
                stage = "show-toast";
                notifier.Show(toast);
                DesktopDiagnostics.Write(DiagnosticEvent.NotificationShown, requestId, safe);
            }
            catch (Exception ex)
            {
                var hresult = unchecked((uint)ex.HResult).ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
                DesktopDiagnostics.Write(DiagnosticEvent.NotificationFailed, requestId, detail: "toast-" + stage + "-exception-" + ex.GetType().Name + "-hr-" + hresult);
            }
        }
    }
}
