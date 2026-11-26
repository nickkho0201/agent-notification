namespace AgentNotify.Launcher;

/// <summary>
/// Точка входа лаунчера. Подсистема GUI (WinExe): Планировщик заданий не создаёт консольное окно,
/// а дочерний AgentNotify.exe запускается с CreateNoWindow. Код выхода дочернего процесса
/// возвращается как код выхода лаунчера.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        return LauncherRunner.Run(args, Environment.ProcessPath);
    }
}
