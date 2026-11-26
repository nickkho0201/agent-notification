using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;

namespace AgentNotify.Windows;

public sealed record TaskRegistration(bool Ok, string? Error, string? Detail = null);

/// <summary>Абстракция Планировщика заданий: в тестах подменяется фейком, в продукте — schtasks.exe.</summary>
public interface ITaskRegistrar
{
    string Name { get; }
    TaskRegistration RegisterReminder(string taskName, DateTimeOffset dueInstant, string exePath, string arguments);
    TaskRegistration RegisterSweep(string taskName, string exePath, string arguments, DateTimeOffset nowInstant);
    TaskRegistration Delete(string taskName);
    bool Exists(string taskName);

    /// <summary>
    /// Фактическая команда (Action/Command) существующей задачи или null, если задача отсутствует
    /// либо прочитать её не удалось. Нужна, чтобы перевести уже поставленные задачи на лаунчер.
    /// </summary>
    string? GetActionCommand(string taskName);
}

public static class TaskNames
{
    /// <summary>
    /// Корень Task Scheduler общий для всех учётных записей Windows, поэтому имя задачи обязано
    /// включать хэш SID пользователя (и хранилища для тестов): иначе второй пользователь мог бы
    /// перезаписать чужую задачу через schtasks /Create /F.
    /// </summary>
    public static string Scope => ScopeFor(WindowsIdentity.GetCurrent().User!.Value, Core.AppPaths.Home);

    public static string ScopeFor(string userSid, string home)
    {
        var sidHash = ShortHash(userSid);
        var defaultHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentNotification");
        var homeSuffix = string.Equals(Path.GetFullPath(home).TrimEnd('\\'), defaultHome.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : "." + ShortHash(Path.GetFullPath(home).ToLowerInvariant());
        return "AgentNotify." + sidHash + homeSuffix;
    }

    private static string ShortHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8].ToLowerInvariant();

    public static string Sweep => Scope + ".Sweep";
    public static string Reminder(string id) => Scope + ".Reminder." + id;
}

/// <summary>Сборка XML для schtasks /Create /XML. Чистые функции, чтобы их можно было проверять тестами.</summary>
public static class TaskXml
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public static string BuildReminder(string taskName, string exePath, string arguments, DateTimeOffset dueInstant, string userId, string description, TimeZoneInfo? zone = null)
    {
        var doc = new XDocument(
            new XElement(Ns + "Task", new XAttribute("version", "1.2"),
                Registration(taskName, description),
                new XElement(Ns + "Triggers",
                    new XElement(Ns + "TimeTrigger",
                        new XElement(Ns + "StartBoundary", FormatBoundary(dueInstant, zone ?? TimeZoneInfo.Local)),
                        new XElement(Ns + "Enabled", "true"))),
                Principal(userId),
                Settings(executionTimeLimit: "PT5M", startWhenAvailable: true, allowWake: false),
                Action(exePath, arguments)));
        return doc.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>Периодический обход: при входе и каждые 5 минут. Догоняет сон и пропущенные сроки в пределах интервала.</summary>
    public static string BuildSweep(string taskName, string exePath, string arguments, DateTimeOffset nowInstant, string userId, string description, string interval = "PT5M", TimeZoneInfo? zone = null)
    {
        var doc = new XDocument(
            new XElement(Ns + "Task", new XAttribute("version", "1.2"),
                Registration(taskName, description),
                new XElement(Ns + "Triggers",
                    new XElement(Ns + "LogonTrigger",
                        new XElement(Ns + "Enabled", "true"),
                        new XElement(Ns + "UserId", userId)),
                    new XElement(Ns + "TimeTrigger",
                        new XElement(Ns + "StartBoundary", FormatBoundary(nowInstant, zone ?? TimeZoneInfo.Local)),
                        new XElement(Ns + "Enabled", "true"),
                        new XElement(Ns + "Repetition",
                            new XElement(Ns + "Interval", interval),
                            new XElement(Ns + "StopAtDurationEnd", "false")))),
                Principal(userId),
                Settings(executionTimeLimit: "PT10M", startWhenAvailable: true, allowWake: false),
                Action(exePath, arguments)));
        return doc.ToString(SaveOptions.DisableFormatting);
    }

    private static XElement Registration(string taskName, string description) =>
        new(Ns + "RegistrationInfo",
            new XElement(Ns + "URI", "\\" + taskName),
            new XElement(Ns + "Author", "AgentNotify"),
            new XElement(Ns + "Description", description));

    private static XElement Principal(string userId) =>
        new(Ns + "Principals",
            new XElement(Ns + "Principal", new XAttribute("id", "Author"),
                new XElement(Ns + "UserId", userId),
                new XElement(Ns + "LogonType", "InteractiveToken"),
                new XElement(Ns + "RunLevel", "LeastPrivilege")));

    private static XElement Settings(string executionTimeLimit, bool startWhenAvailable, bool allowWake) =>
        new(Ns + "Settings",
            new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
            new XElement(Ns + "DisallowStartIfOnBatteries", "false"),
            new XElement(Ns + "StopIfGoingOnBatteries", "false"),
            new XElement(Ns + "AllowHardTerminate", "true"),
            new XElement(Ns + "StartWhenAvailable", startWhenAvailable ? "true" : "false"),
            new XElement(Ns + "RunOnlyIfNetworkAvailable", "false"),
            new XElement(Ns + "IdleSettings",
                new XElement(Ns + "StopOnIdleEnd", "false"),
                new XElement(Ns + "RestartOnIdle", "false")),
            new XElement(Ns + "AllowStartOnDemand", "true"),
            new XElement(Ns + "Enabled", "true"),
            new XElement(Ns + "Hidden", "false"),
            new XElement(Ns + "RunOnlyIfIdle", "false"),
            new XElement(Ns + "WakeToRun", allowWake ? "true" : "false"),
            new XElement(Ns + "ExecutionTimeLimit", executionTimeLimit),
            new XElement(Ns + "Priority", "7"));

    private static XElement Action(string exePath, string arguments) =>
        new(Ns + "Actions", new XAttribute("Context", "Author"),
            new XElement(Ns + "Exec",
                new XElement(Ns + "Command", exePath),
                new XElement(Ns + "Arguments", arguments)));

    /// <summary>
    /// Граница запуска в локальном времени ПК, но не раньше due: дробные секунды округляются вверх.
    /// Смещение источника игнорируется осознанно — Task Scheduler трактует StartBoundary как время
    /// системы, поэтому instant переводится в зону машины (смена зоны/летнего времени учитывается здесь).
    /// </summary>
    public static string FormatBoundary(DateTimeOffset instant, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(instant.ToUniversalTime(), zone);
        var ticks = local.Ticks;
        var remainder = ticks % TimeSpan.TicksPerSecond;
        if (remainder != 0) local = local.AddTicks(TimeSpan.TicksPerSecond - remainder);
        // Несуществующее местное время (переход на летнее время) сдвигаем вперёд, но не назад.
        if (zone.IsInvalidTime(local.DateTime)) local = local.AddHours(1);
        return local.ToString("yyyy-MM-dd'T'HH:mm:ss");
    }
}

