using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace SubtitleMaster.Services;

public class NotificationService
{
    private static NotificationService? _instance;
    public static NotificationService Instance => _instance ??= new NotificationService();

    private bool _registered;

    public void Initialize()
    {
        try
        {
            if (AppNotificationManager.IsSupported() && !_registered)
            {
                AppNotificationManager.Default.Register();
                _registered = true;
            }
        }
        catch { }
    }

    public void ShowCompletionToast(string title, string message)
    {
        Task.Run(() =>
        {
            try
            {
                if (AppNotificationManager.IsSupported())
                {
                    if (!_registered)
                    {
                        try
                        {
                            AppNotificationManager.Default.Register();
                            _registered = true;
                        }
                        catch { }
                    }

                    var appNotification = new AppNotificationBuilder()
                        .AddText(title)
                        .AddText(message)
                        .BuildNotification();

                    AppNotificationManager.Default.Show(appNotification);
                    return;
                }
            }
            catch { }

            // Pure C# WinRT Toast Notification fallback (zero processes, zero black console window flashing)
            try
            {
                var template = Windows.UI.Notifications.ToastNotificationManager.GetTemplateContent(Windows.UI.Notifications.ToastTemplateType.ToastText02);
                var textNodes = template.GetElementsByTagName("text");
                textNodes.Item(0).AppendChild(template.CreateTextNode(title));
                textNodes.Item(1).AppendChild(template.CreateTextNode(message));
                var toast = new Windows.UI.Notifications.ToastNotification(template);
                Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier("Subtitle Master").Show(toast);
            }
            catch { }
        });
    }
}
