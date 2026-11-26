using AgentNotify.Core;
using AgentNotify.Windows;

namespace AgentNotify.Tests;

public sealed class TempDb : IDisposable
{
    public string Directory { get; }
    public ReminderStore Store { get; }

    public TempDb()
    {
        Directory = Path.Combine(Path.GetTempPath(), "agentnotify-tests", Guid.NewGuid().ToString("n"));
        System.IO.Directory.CreateDirectory(Directory);
        Store = new ReminderStore(Path.Combine(Directory, "reminders.db"));
        Store.Initialize();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch
        {
            // тестовый каталог
        }
    }
}

public sealed class FakeRegistrar : ITaskRegistrar
{
    public string Name => "fake";
    public bool FailRegistration { get; set; }
    public bool FailDelete { get; set; }
    public List<string> Registered { get; } = new();
    public HashSet<string> Existing { get; } = new(StringComparer.Ordinal);
    public List<string> Deleted { get; } = new();
    public List<(string TaskName, DateTimeOffset DueLocal, string Arguments)> Details { get; } = new();

    /// <summary>Текущий Action/Command каждой задачи (для проверки миграции на лаунчер).</summary>
    public Dictionary<string, string> Actions { get; } = new(StringComparer.Ordinal);

    /// <summary>Имитация действия, случившегося во время вызова Планировщика (например, отмены).</summary>
    public Action<string>? AfterRegister { get; set; }

    /// <summary>Имитация медленного вызова Планировщика: выполняется до фактической регистрации.</summary>
    public Action<string>? BeforeRegister { get; set; }

    private readonly object _gate = new();
    private int _concurrent;
    private int _registerCalls;
    private int _maxConcurrent;

    public int RegisterCalls => Volatile.Read(ref _registerCalls);
    public int MaxConcurrentRegister => Volatile.Read(ref _maxConcurrent);

    public TaskRegistration RegisterReminder(string taskName, DateTimeOffset dueLocal, string exePath, string arguments)
    {
        var active = Interlocked.Increment(ref _concurrent);
        Interlocked.Increment(ref _registerCalls);
        lock (_gate)
        {
            if (active > _maxConcurrent) Volatile.Write(ref _maxConcurrent, active);
        }
        try
        {
            BeforeRegister?.Invoke(taskName);
            if (FailRegistration) return new TaskRegistration(false, "отказ тестового регистратора");
            lock (_gate)
            {
                Registered.Add(taskName);
                Details.Add((taskName, dueLocal, arguments));
                Existing.Add(taskName);
                Actions[taskName] = exePath;
            }
            AfterRegister?.Invoke(taskName);
            return new TaskRegistration(true, null);
        }
        finally
        {
            Interlocked.Decrement(ref _concurrent);
        }
    }

    public TaskRegistration RegisterSweep(string taskName, string exePath, string arguments, DateTimeOffset nowLocal)
    {
        if (FailRegistration) return new TaskRegistration(false, "отказ тестового регистратора");
        Registered.Add(taskName);
        Existing.Add(taskName);
        Actions[taskName] = exePath;
        return new TaskRegistration(true, null);
    }

    public TaskRegistration Delete(string taskName)
    {
        if (FailDelete) return new TaskRegistration(false, "отказ удаления");
        Deleted.Add(taskName);
        Existing.Remove(taskName);
        Actions.Remove(taskName);
        return new TaskRegistration(true, null);
    }

    public bool Exists(string taskName) => Existing.Contains(taskName);

    public string? GetActionCommand(string taskName) =>
        Existing.Contains(taskName) && Actions.TryGetValue(taskName, out var command) ? command : null;

    /// <summary>Имитация задачи, поставленной старой сборкой: Command указывает на консольный exe.</summary>
    public void SimulateLegacyCommand(string taskName, string exePath)
    {
        Existing.Add(taskName);
        Actions[taskName] = exePath;
    }
}

/// <summary>
/// Массовая вставка pending-записей напрямую в SQLite: нужна, чтобы проверить миграцию на объёме
/// больше общего лимита List (500) без тысяч реальных задач Планировщика.
/// </summary>
public static class BulkReminders
{
    public static List<string> InsertPending(ReminderStore store, int count, DateTimeOffset firstDue, TimeSpan step)
    {
        var ids = new List<string>(count);
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + store.DbPath + ";Pooling=False");
        connection.Open();
        using var transaction = connection.BeginTransaction();
        for (var i = 0; i < count; i++)
        {
            var id = "rem_" + i.ToString("x16");
            ids.Add(id);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO reminders (id, text, due_utc_ms, offset_minutes, created_utc_ms, state, attempts)
                VALUES ($id, $text, $due, 0, $created, 'pending', 0)
                """;
            var due = firstDue.AddTicks(step.Ticks * i);
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$text", "массовая запись " + i);
            command.Parameters.AddWithValue("$due", due.ToUniversalTime().ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$created", DateTimeOffset.UnixEpoch.ToUnixTimeMilliseconds());
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        return ids;
    }
}

public sealed class FakePresenter : IToastPresenter{
    public string Name => "fake-toast";
    public bool Fail { get; set; }
    public bool ReportHistory { get; set; } = true;
    public List<(string Id, string Text)> Shown { get; } = new();

    public ToastSendResult Show(string reminderId, string text)
    {
        if (Fail) return new ToastSendResult(false, false, "тестовый отказ показа", null);
        Shown.Add((reminderId, text));
        return new ToastSendResult(true, ReportHistory, null, ReportHistory ? null : "история недоступна (тест)");
    }
}
