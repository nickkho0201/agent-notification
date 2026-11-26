using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Text;

namespace AgentNotify.Core;

/// <summary>Результат захвата guard задачи: взято, занято другим владельцем или недоступна межпроцессная блокировка.</summary>
public enum TaskSyncStatus
{
    Acquired,
    Busy,

    /// <summary>Именованный Mutex недоступен: работать «только на Monitor» небезопасно (fail-closed).</summary>
    Unavailable
}

public readonly struct TaskSyncResult
{
    public TaskSyncResult(TaskSyncStatus status, IDisposable? lease)
    {
        Status = status;
        Lease = lease;
    }

    public TaskSyncStatus Status { get; }
    public IDisposable? Lease { get; }
    public bool IsAcquired => Status == TaskSyncStatus.Acquired;
}

/// <summary>Источник guard'а задач. В продукте — именованный Mutex, в тестах можно подставить изолированную реализацию.</summary>
public interface ITaskSyncCoordinator
{
    TaskSyncResult Acquire(string taskName, TimeSpan timeout);
}

/// <summary>
/// Сериализует операции с задачей Планировщика одной записи: регистрацию при переносе срока,
/// восстановление обходом и миграцию. Внутри процесса — Monitor, между процессами одной сессии
/// Windows — именованный Mutex: MCP-сервер, обход и установка могут менять одну и ту же задачу.
///
/// Если именованный Mutex недоступен, guard работает fail-closed: захват возвращает
/// <see cref="TaskSyncStatus.Unavailable"/>, и вызывающий обязан отложить регистрацию/удаление,
/// сохранив маркер «грязной» записи. Одного Monitor для этого недостаточно — он не защищает от
/// другого процесса, который может затереть только что подтверждённый срок.
/// </summary>
public sealed class NamedMutexTaskSync : ITaskSyncCoordinator
{
    public static readonly NamedMutexTaskSync Instance = new();

    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.Ordinal);
    private readonly Func<string, Mutex?> _createMutex;

    /// <param name="createMutex">
    /// Фабрика именованного Mutex. null — изолированная проверка «Mutex недоступен» без глобальных
    /// изменений среды; по умолчанию создаётся настоящий Mutex.
    /// </param>
    public NamedMutexTaskSync(Func<string, Mutex?>? createMutex = null)
    {
        _createMutex = createMutex ?? (name => new Mutex(initiallyOwned: false, name));
    }

    public TaskSyncResult Acquire(string taskName, TimeSpan timeout)
    {
        var gate = Gates.GetOrAdd(taskName, _ => new object());
        if (!Monitor.TryEnter(gate, timeout)) return new TaskSyncResult(TaskSyncStatus.Busy, null);

        Mutex? mutex = null;
        try
        {
            try
            {
                mutex = _createMutex(NameFor(taskName));
            }
            catch (Exception e)
            {
                Log.Write($"task-sync: именованный Mutex недоступен ({e.Message}); операция отложена (fail-closed)");
                Monitor.Exit(gate);
                return new TaskSyncResult(TaskSyncStatus.Unavailable, null);
            }

            if (mutex is null)
            {
                Log.Write("task-sync: именованный Mutex недоступен; операция отложена (fail-closed)");
                Monitor.Exit(gate);
                return new TaskSyncResult(TaskSyncStatus.Unavailable, null);
            }

            bool acquired;
            try
            {
                acquired = mutex.WaitOne(timeout);
            }
            catch (AbandonedMutexException)
            {
                // Владелец упал, не освободив Mutex: блокировка наша, данные проверяются заново.
                acquired = true;
            }
            catch (Exception e)
            {
                Log.Write($"task-sync: ожидание Mutex не удалось ({e.Message}); операция отложена (fail-closed)");
                mutex.Dispose();
                Monitor.Exit(gate);
                return new TaskSyncResult(TaskSyncStatus.Unavailable, null);
            }

            if (!acquired)
            {
                mutex.Dispose();
                Monitor.Exit(gate);
                return new TaskSyncResult(TaskSyncStatus.Busy, null);
            }

            return new TaskSyncResult(TaskSyncStatus.Acquired, new TaskSyncLease(gate, mutex));
        }
        catch
        {
            mutex?.Dispose();
            Monitor.Exit(gate);
            throw;
        }
    }

    private static string NameFor(string taskName)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(taskName)))[..16].ToLowerInvariant();
        return "AgentNotify.Sync." + hash;
    }
}

/// <summary>Владелец guard'а: освобождает именованный Mutex и внутрипроцессный Monitor.</summary>
public sealed class TaskSyncLease : IDisposable
{
    /// <summary>Сколько ждать занятый guard в интерактивном вызове. Регистрация задачи ограничена 30 с.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private readonly object _gate;
    private readonly Mutex? _mutex;
    private bool _disposed;

    internal TaskSyncLease(object gate, Mutex? mutex)
    {
        _gate = gate;
        _mutex = mutex;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_mutex is not null)
            {
                _mutex.ReleaseMutex();
                _mutex.Dispose();
            }
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }
}
