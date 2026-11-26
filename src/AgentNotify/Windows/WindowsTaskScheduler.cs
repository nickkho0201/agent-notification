using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using AgentNotify.Core;

namespace AgentNotify.Windows;

/// <summary>
/// Реальная регистрация задач Планировщика Windows через schtasks.exe. Задачи per-user:
/// интерактивный токен, минимальные права, без повышения. Никаких чужих задач не трогает.
/// </summary>
public sealed class WindowsTaskScheduler : ITaskRegistrar
{
    public string Name => "windows-scheduler";

    public static string CurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new InvalidOperationException("Не удалось определить SID пользователя.");
    }

    public TaskRegistration RegisterReminder(string taskName, DateTimeOffset dueInstant, string exePath, string arguments)
    {
        var xml = TaskXml.BuildReminder(taskName, exePath, arguments, dueInstant, CurrentUserSid(),
            "Разовое напоминание AgentNotify. Создано автоматически.");
        return CreateFromXml(taskName, xml);
    }

    public TaskRegistration RegisterSweep(string taskName, string exePath, string arguments, DateTimeOffset nowInstant)
    {
        var xml = TaskXml.BuildSweep(taskName, exePath, arguments, nowInstant, CurrentUserSid(),
            "Обход очереди AgentNotify: при входе и каждые 5 минут, догоняет пропущенные сроки.");
        return CreateFromXml(taskName, xml);
    }

    public TaskRegistration Delete(string taskName)
    {
        if (!Exists(taskName))
            return new TaskRegistration(true, null, "задача отсутствовала");
        var result = Run("schtasks.exe", ["/Delete", "/TN", taskName, "/F"]);
        if (result.ExitCode == 0)
            return new TaskRegistration(true, null);
        return new TaskRegistration(false, $"schtasks /Delete вернул {result.ExitCode}: {Trim(result.Output)}");
    }

    public bool Exists(string taskName)
    {
        var result = Run("schtasks.exe", ["/Query", "/TN", taskName]);
        return result.ExitCode == 0;
    }

    /// <summary>
    /// Читает Command задачи (нужно для миграции старых задач). XML читается байтами и декодируется
    /// с проверкой: schtasks пишет в кодовой странице консоли, а путь может содержать не-ASCII символы.
    /// </summary>
    public string? GetActionCommand(string taskName)
    {
        try
        {
            var (exitCode, bytes) = RunCaptureBytes("schtasks.exe", ["/Query", "/TN", taskName, "/XML"]);
            if (exitCode != 0 || bytes.Length == 0) return null;
            var xml = DecodeXml(bytes);
            var doc = System.Xml.Linq.XDocument.Parse(xml);
            System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            return doc.Descendants(ns + "Command").FirstOrDefault()?.Value;
        }
        catch (Exception e)
        {
            Log.Write($"не удалось прочитать команду задачи {taskName}: {e.Message}");
            return null;
        }
    }

    /// <summary>UTF-8, UTF-16 по BOM или (fallback) кодовая страница консоли.</summary>
    internal static string DecodeXml(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage).GetString(bytes);
        }
    }

    private static (int ExitCode, byte[] Output) RunCaptureBytes(string fileName, string[] arguments)
    {
        var info = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Не удалось запустить " + fileName);
        using var buffer = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(buffer);
        if (!process.WaitForExit(30000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* уже завершился */ }
            return (-1, Array.Empty<byte>());
        }
        return (process.ExitCode, buffer.ToArray());
    }

    public static string? LastRunInfo(string taskName)
    {
        var result = Run("schtasks.exe", ["/Query", "/TN", taskName, "/FO", "LIST", "/V"]);
        if (result.ExitCode != 0) return null;
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        var wanted = new[] { "Last Run Time", "Last Result", "Next Run Time", "Status", "Состояние", "Время последнего запуска", "Результат последнего запуска", "Время следующего запуска" };
        var selected = lines.Where(l => wanted.Any(w => l.StartsWith(w, StringComparison.OrdinalIgnoreCase)));
        return string.Join("; ", selected);
    }

    private static TaskRegistration CreateFromXml(string taskName, string xml)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "agentnotify-task-" + Guid.NewGuid().ToString("n") + ".xml");
        try
        {
            File.WriteAllText(tempFile, xml, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
            var result = Run("schtasks.exe", ["/Create", "/TN", taskName, "/XML", tempFile, "/F"]);
            if (result.ExitCode == 0)
                return new TaskRegistration(true, null);
            return new TaskRegistration(false, $"schtasks /Create вернул {result.ExitCode}: {Trim(result.Output)}");
        }
        catch (Exception e)
        {
            return new TaskRegistration(false, "Не удалось вызвать schtasks: " + e.Message);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { /* временный файл */ }
        }
    }

    private static (int ExitCode, string Output) Run(string fileName, string[] arguments)
    {
        var info = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
            StandardErrorEncoding = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage)
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Не удалось запустить " + fileName);
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* уже завершился */ }
            return (-1, "таймаут вызова " + fileName);
        }
        return (process.ExitCode, output);
    }

    private static string Trim(string value)
    {
        var single = string.Join(" ", value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()));
        return single.Length > 400 ? single[..400] : single;
    }
}

