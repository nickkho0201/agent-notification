using Microsoft.Toolkit.Uwp.Notifications;
using Windows.UI.Notifications;

namespace AgentNotify.Windows;

/// <summary>Результат показа. Accepted = Windows не отверг вызов; HistoryVerified = запись найдена в истории уведомлений.</summary>
public sealed record ToastSendResult(bool Accepted, bool HistoryVerified, string? Error, string? Warning);

public interface IToastPresenter
{
    string Name { get; }
    ToastSendResult Show(string reminderId, string text);
}

/// <summary>
/// Нативный Windows-toast без LLM и без очереди Windows: показ инициирует наш процесс (CLI/планировщик).
/// AUMID регистрирует ToastNotificationManagerCompat по стабильному пути exe.
/// </summary>
public sealed class WindowsToastPresenter : IToastPresenter
{
    public string Name => "windows-toast";

    public ToastSendResult Show(string reminderId, string text)
    {
        try
        {
            var notifier = ToastNotificationManagerCompat.CreateToastNotifier();
            var setting = notifier.Setting;
            if (setting != NotificationSetting.Enabled)
            {
                return new ToastSendResult(false, false,
                    $"Уведомления для AgentNotify отключены в параметрах Windows (Setting={setting}). Включите их в Параметры → Система → Уведомления.", null);
            }

            new ToastContentBuilder()
                .AddText("Напоминание")
                .AddText(text)
                .Show(toast =>
                {
                    toast.Tag = reminderId;
                    toast.Group = "agent-notification";
                });

            var verified = WaitForHistoryEntry(reminderId, TimeSpan.FromSeconds(2));
            return verified
                ? new ToastSendResult(true, true, null, null)
                : new ToastSendResult(true, false, null,
                    "Windows принял toast (исключения не было), но запись не найдена в истории уведомлений: история может быть отключена. Это не доказывает показ баннера.");
        }
        catch (Exception e)
        {
            return new ToastSendResult(false, false, "Показ toast не удался: " + e.Message, null);
        }
    }

    private static bool WaitForHistoryEntry(string reminderId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                var history = ToastNotificationManagerCompat.History.GetHistory();
                foreach (var item in history)
                {
                    if (string.Equals(item.Tag, reminderId, StringComparison.Ordinal))
                        return true;
                }
            }
            catch
            {
                return false;
            }

            if (DateTime.UtcNow >= deadline) return false;
            Thread.Sleep(250);
        }
    }
}
