using System.Text;

namespace AgentNotify.Core;

/// <summary>Расположение данных приложения. AGENTNOTIFY_HOME нужен только для тестов и изолированных проверок.</summary>
public static class AppPaths
{
    public const string HomeEnvVar = "AGENTNOTIFY_HOME";

    public static string Home
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable(HomeEnvVar);
            if (!string.IsNullOrWhiteSpace(overridden))
                return Path.GetFullPath(overridden);
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentNotification");
        }
    }

    public static string DbPath => Path.Combine(Home, "reminders.db");

    public static string LogDirectory => Path.Combine(Home, "logs");

    public static string LogPath => Path.Combine(LogDirectory, "worker.log");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(LogDirectory);
    }
}

/// <summary>Небольшой файловый журнал: stdout MCP-сервера должен оставаться чистым JSON-RPC.</summary>
public static class Log
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            AppPaths.EnsureDirectories();
            lock (Gate)
            {
                var path = AppPaths.LogPath;
                RotateIfNeeded(path);
                File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-ddTHH:mm:sszzz} pid={Environment.ProcessId} {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch
        {
            // журнал не должен ломать доставку
        }
    }

    private static void RotateIfNeeded(string path)
    {
        var info = new FileInfo(path);
        if (info.Exists && info.Length > 2 * 1024 * 1024)
        {
            var backup = path + ".1";
            if (File.Exists(backup)) File.Delete(backup);
            File.Move(path, backup);
        }
    }
}
