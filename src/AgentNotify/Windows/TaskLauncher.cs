namespace AgentNotify.Windows;

/// <summary>
/// Выбор команды для задач Планировщика. Основной exe — консольный (OutputType=Exe), поэтому
/// прямой запуск из Планировщика создаёт окно терминала. Задачи должны запускать оконно-невидимый
/// лаунчер AgentNotify.Launcher.exe рядом с основным exe: он стартует тот же AgentNotify.exe
/// с CreateNoWindow, сохраняя путь основного процесса, AUMID тоста и настройки.
/// </summary>
public static class TaskLauncher
{
    public const string FileName = "AgentNotify.Launcher.exe";

    /// <summary>Путь лаунчера рядом с основным exe или null, если его нет.</summary>
    public static string? Resolve(string? mainExePath)
    {
        if (string.IsNullOrWhiteSpace(mainExePath)) return null;
        string full;
        try
        {
            full = Path.GetFullPath(mainExePath);
        }
        catch
        {
            return null;
        }
        var directory = Path.GetDirectoryName(full);
        if (directory is null) return null;
        var candidate = Path.Combine(directory, FileName);
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>Команда задачи Планировщика: лаунчер, а при его отсутствии — основной exe (с предупреждением).</summary>
    public static string CommandFor(string? mainExePath) =>
        Resolve(mainExePath) ?? mainExePath ?? throw new InvalidOperationException("Не удалось определить путь exe.");

    /// <summary>Совпадает ли команда задачи с ожидаемым путём (для проверки и миграции).</summary>
    public static bool SameCommand(string? actual, string? expected)
    {
        if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(expected)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(actual.Trim()), Path.GetFullPath(expected.Trim()), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(actual.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
