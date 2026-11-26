using System.Diagnostics;
using System.Text;

namespace AgentNotify.Launcher;

/// <summary>
/// Запускает основной консольный процесс AgentNotify.exe без окна и передаёт его код выхода.
/// Никакой другой работы лаунчер не выполняет.
/// </summary>
public static class LauncherRunner
{
    public const string TargetFileName = "AgentNotify.exe";

    /// <summary>Некорректный вызов: контракт лаунчера нарушен.</summary>
    public const int ExitBadArguments = 2;
    /// <summary>Рядом с лаунчером нет AgentNotify.exe.</summary>
    public const int ExitMissingTarget = 5;
    /// <summary>Дочерний процесс не завершился за отведённое время и был остановлен.</summary>
    public const int ExitTimeout = 6;
    /// <summary>Не удалось запустить дочерний процесс.</summary>
    public const int ExitLaunchFailed = 7;

    /// <summary>Чуть меньше лимита задачи Планировщика (PT10M), чтобы успеть вернуть код выхода.</summary>
    public static readonly TimeSpan ChildTimeout = TimeSpan.FromMinutes(9);

    /// <summary>Путь к AgentNotify.exe рядом с лаунчером. Аргументы командной строки его не задают.</summary>
    public static string? ResolveTarget(string? launcherPath)
    {
        if (string.IsNullOrWhiteSpace(launcherPath)) return null;
        string full;
        try
        {
            full = Path.GetFullPath(launcherPath);
        }
        catch
        {
            return null;
        }
        var directory = Path.GetDirectoryName(full);
        if (directory is null) return null;
        var candidate = Path.Combine(directory, TargetFileName);
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// Описание запуска дочернего процесса: без окна, без оболочки, аргументы через ArgumentList
    /// (пробелы, кавычки и Unicode передаются как есть), вывод перенаправлен, чтобы ребеноку
    /// не требовалась консоль.
    /// </summary>
    public static ProcessStartInfo BuildStartInfo(string targetExePath, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(targetExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(targetExePath)) ?? Environment.CurrentDirectory
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    /// <summary>Точка входа лаунчера: разбор, запуск дочернего процесса, код выхода.</summary>
    public static int Run(IReadOnlyList<string> args, string? launcherPath)
    {
        if (!LauncherArgs.TryParse(args, out var invocation, out var error))
        {
            LauncherLog.Write("некорректный вызов: " + error);
            return ExitBadArguments;
        }

        var target = ResolveTarget(launcherPath);
        if (target is null)
        {
            LauncherLog.Write($"{TargetFileName} не найден рядом с лаунчером ({launcherPath ?? "путь неизвестен"})", invocation!.Home);
            return ExitMissingTarget;
        }

        var home = invocation!.Home;
        try
        {
            var info = BuildStartInfo(target, invocation.ChildArguments());
            using var process = Process.Start(info);
            if (process is null)
            {
                LauncherLog.Write("Process.Start вернул null для " + target, home);
                return ExitLaunchFailed;
            }

            // Дренаж каналов: дочерний процесс не должен блокироваться на заполненном буфере.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit((int)ChildTimeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* уже завершился */ }
                LauncherLog.Write($"дочерний процесс не завершился за {ChildTimeout.TotalMinutes:0} мин и был остановлен", home);
                return ExitTimeout;
            }

            try { Task.WaitAll(new Task[] { stdout, stderr }, TimeSpan.FromSeconds(5)); } catch { /* вывод не важен */ }
            return process.ExitCode;
        }
        catch (Exception e)
        {
            LauncherLog.Write("не удалось запустить дочерний процесс: " + e.Message, home);
            return ExitLaunchFailed;
        }
    }
}

/// <summary>
/// Диагностика лаунчера: только ошибочные случаи в тот же журнал, что использует AgentNotify.
/// Окно сообщения не показывается никогда — лаунчер обязан оставаться невидимым.
/// </summary>
internal static class LauncherLog
{
    private const string HomeEnvVar = "AGENTNOTIFY_HOME";

    public static void Write(string message, string? homeOverride = null)
    {
        try
        {
            var home = homeOverride;
            if (string.IsNullOrWhiteSpace(home)) home = Environment.GetEnvironmentVariable(HomeEnvVar);
            if (string.IsNullOrWhiteSpace(home))
                home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentNotification");
            var directory = Path.Combine(home, "logs");
            Directory.CreateDirectory(directory);
            var line = $"{DateTimeOffset.Now:yyyy-MM-ddTHH:mm:sszzz} launcher pid={Environment.ProcessId} {message}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(directory, "worker.log"), line, Encoding.UTF8);
        }
        catch
        {
            // журнал не должен влиять на запуск
        }
    }
}
