using AgentNotify.Windows;

namespace AgentNotify.Core;

public sealed record ScheduleOutcome(Reminder Reminder, bool WindowsTaskRegistered, bool SweepTaskPresent, string? TaskError, IReadOnlyList<string> Warnings);

public sealed record CancelOutcome(bool Found, CancelStatus Status, Reminder? Reminder, bool WindowsTaskRemoved, IReadOnlyList<string> Warnings);

public sealed record SweepEntry(string Id, bool Accepted, bool HistoryVerified, string? Error, string? Warning);

/// <summary>
/// Итог точечного изменения. <see cref="SyncPending"/> = true означает, что новое значение уже
/// записано в SQLite, но точный триггер Планировщика ещё не подтверждён: это честный частичный
/// статус, а не ошибка и не ложный успех.
/// </summary>
public sealed record UpdateOutcome(
    bool Found,
    UpdateStatus Status,
    Reminder? Reminder,
    bool TextChanged,
    bool DueChanged,
    bool Changed,
    bool WindowsTaskRegistered,
    bool SyncPending,
    bool SweepTaskPresent,
    string? TaskError,
    IReadOnlyList<string> Warnings);

public sealed record SweepOutcome(int RecoveredStale, int Claimed, int Sent, int Failed, int RepairedTasks, IReadOnlyList<SweepEntry> Entries, int MigratedTasks = 0);

/// <summary>
/// Результат перевода уже поставленных задач Планировщика на оконно-невидимый лаунчер.
/// <c>Migrated</c> — будущие записи, перерегистрированные на лаунчер; <c>Retired</c> — снятые
/// просроченные legacy-триггеры (запись остаётся в очереди, доставит невидимый обход);
/// <c>Blocked</c> — просроченные legacy-триггеры, которые нельзя снимать, пока невидимый обход
/// не подтверждён; <c>Remaining</c> больше нуля, если за один проход лимит не позволил обработать
/// всю очередь: следующий обход продолжит с остатка.
/// </summary>
public sealed record MigrationOutcome(
    int Migrated,
    int AlreadyOk,
    int Failed,
    int Remaining,
    IReadOnlyList<string> Errors,
    int Retired = 0,
    int Blocked = 0)
{
    public static MigrationOutcome Empty { get; } = new(0, 0, 0, 0, Array.Empty<string>());

    public bool Ok => Failed == 0 && Remaining == 0 && Blocked == 0;
}

/// <summary>Общая логика CLI и MCP: одна очередь, одна проверка ввода, одинаковые исходы.</summary>
public sealed class ReminderService
{
    private static readonly TimeSpan StaleClaimAfter = TimeSpan.FromMinutes(10);

    private readonly ReminderStore _store;
    private readonly ITaskRegistrar _tasks;
    private readonly IToastPresenter _toast;
    private readonly Func<DateTimeOffset> _clock;
    private readonly string _exePath;
    private readonly string _taskCommand;
    private readonly ITaskSyncCoordinator _sync;

    /// <summary>Сколько задач Планировщика мигрируется за один проход (остальные — следующим обходом).</summary>
    public const int MigrationBatchLimit = 500;

    /// <summary>Размер страницы выборки при миграции: очередь читается постранично, без общего лимита 500.</summary>
    public const int MigrationPageSize = 100;

    /// <summary>Сколько записей восстанавливается за один проход обхода.</summary>
    public const int RepairBatchLimit = 20;

    /// <summary>Размер страницы приоритетной очереди «грязных» записей.</summary>
    public const int RepairSyncPageSize = 100;

    /// <summary>Ожидание guard'а в фоновом восстановлении: короткое, чтобы sweep не стоял по 15 с на запись.</summary>
    public static readonly TimeSpan RepairLockTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Общий бюджет фонового восстановления за один проход (проверяется перед каждой попыткой).</summary>
    public static readonly TimeSpan RepairBudget = TimeSpan.FromSeconds(10);

    public ReminderService(ReminderStore store, ITaskRegistrar tasks, IToastPresenter toast, Func<DateTimeOffset>? clock = null, string? exePath = null, string? launcherPath = null, ITaskSyncCoordinator? sync = null)
    {
        _store = store;
        _tasks = tasks;
        _toast = toast;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _exePath = exePath ?? Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось определить путь exe.");
        _taskCommand = launcherPath ?? TaskLauncher.Resolve(_exePath) ?? _exePath;
        _sync = sync ?? NamedMutexTaskSync.Instance;
    }

    public ReminderService(ReminderStore store) : this(store, new WindowsTaskScheduler(), new WindowsToastPresenter()) { }

    /// <summary>Команда, которой запускаются задачи Планировщика: лаунчер, если он опубликован рядом.</summary>
    public string TaskCommand => _taskCommand;

    /// <summary>true, если задачи запускаются через оконно-невидимый лаунчер.</summary>
    public bool UsesLauncher => !TaskLauncher.SameCommand(_taskCommand, _exePath);

    /// <summary>
    /// Предупреждение о том, что лаунчера рядом нет и задача запустит консольный exe напрямую.
    /// Пусто, если лаунчер на месте.
    /// </summary>
    public IReadOnlyList<string> LauncherWarnings()
    {
        if (UsesLauncher) return Array.Empty<string>();
        return new[] { $"Рядом с {_exePath} нет {TaskLauncher.FileName}: задача Планировщика запускает консольный exe напрямую и может показать окно терминала. Переустановите сборку, чтобы вернуть невидимый запуск." };
    }

    public static string WorkerArguments()
    {
        return "run-due --home " + Quote(AppPaths.Home);
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    public ScheduleOutcome Schedule(string? at, string? text)
    {
        var now = _clock();
        var due = ReminderInput.ParseDue(at, now);
        var normalized = ReminderInput.NormalizeText(text);
        var reminder = _store.Create(normalized, due, now);

        var warnings = new List<string>();
        var taskName = TaskNames.Reminder(reminder.Id);
        var registration = _tasks.RegisterReminder(taskName, reminder.DueUtc, _taskCommand, WorkerArguments());
        var sweepTaskPresent = SafeSweepExists();
        if (registration.Ok)
        {
            _store.SetTaskName(reminder.Id, taskName);
            reminder.TaskName = taskName;
        }
        else
        {
            var error = registration.Error ?? "неизвестная ошибка регистрации";
            _store.SetLastError(reminder.Id, error);
            reminder.LastError = error;
            // Наличие обхода проверяем фактически: без него обещать доставку нельзя.
            warnings.Add(sweepTaskPresent
                ? $"Точный триггер Windows не зарегистрирован: {error}. Запись сохранена в очереди; задача обхода найдена, но она сработает только при следующем входе/запуске обхода (не гарантированный точный срок, задержка обычно до ~5 минут после срока)."
                : $"Точный триггер Windows не зарегистрирован: {error}. Запись сохранена в очереди, но задача обхода НЕ найдена: доставка не гарантирована, пока обход не установлен (install) или недоступен.");
            Log.Write($"schedule {reminder.Id}: регистрация задачи не удалась: {error}; sweepPresent={sweepTaskPresent}");
        }
        warnings.AddRange(LauncherWarnings());

        return new ScheduleOutcome(reminder, registration.Ok, sweepTaskPresent, registration.Ok ? null : registration.Error, warnings);
    }
    private bool SafeSweepExists()
    {
        try
        {
            return _tasks.Exists(TaskNames.Sweep);
        }
        catch (Exception e)
        {
            Log.Write("schedule: не удалось проверить задачу обхода: " + e.Message);
            return false;
        }
    }

    public IReadOnlyList<Reminder> List(bool includeFinished = false) => _store.List(includeFinished);

    /// <summary>
    /// Поиск только по активным записям; критерии объединяются по AND. Часы берутся из того же
    /// источника, что и у доставки, поэтому overdue совпадает с тем, что уже забрал бы обход.
    /// </summary>
    public ReminderPage Search(ReminderSearchQuery query, int limit, ReminderCursor? after) =>
        _store.Search(query, limit, after, _clock());

    public (int Pending, int Sending, int Sent, int Cancelled) Counts() => _store.Counts();

    /// <summary>Фактическое наличие задачи обхода: без неё доставка просроченных не гарантирована.</summary>
    public bool SweepTaskPresent() => SafeSweepExists();

    public CancelOutcome Cancel(string? id, bool purge = false)
    {
        var validId = ReminderInput.ValidateId(id);
        var (status, reminder) = _store.Cancel(validId);
        if (status == CancelStatus.NotFound)
            return new CancelOutcome(false, status, null, false, Array.Empty<string>());
        if (status is CancelStatus.AlreadySent or CancelStatus.InProgress)
        {
            var reason = status == CancelStatus.AlreadySent
                ? "Напоминание уже доставлено, отмена невозможна."
                : "Доставка уже начата другим обработчиком, отмена невозможна.";
            return new CancelOutcome(true, status, reminder, false, new[] { reason });
        }

        var warnings = new List<string>();
        var taskName = reminder?.TaskName ?? TaskNames.Reminder(validId);
        var removed = _tasks.Delete(taskName);
        if (!removed.Ok)
            warnings.Add("Не удалось удалить задачу Windows: " + removed.Error);

        if (purge) _store.Purge(validId);
        reminder!.State = ReminderState.Cancelled;
        return new CancelOutcome(true, status, reminder, removed.Ok, warnings);
    }

    /// <summary>
    /// Точечное изменение активного напоминания: срок, текст или оба поля. ID, дата создания и
    /// непереданные поля сохраняются. При смене срока запись и маркер необходимой перерегистрации
    /// фиксируются одной транзакцией SQLite, а задача Планировщика перерегистрируется под прежним
    /// именем под общим guard задачи. Если Планировщик отказал или не ответил, возвращается честный
    /// частичный статус, а обход повторит синхронизацию. Текстовая правка (в том числе записи с
    /// незавершённой синхронизацией) задачу Планировщика не трогает: «грязный» маркер лечит обход.
    /// </summary>
    public UpdateOutcome Update(string? id, string? at, string? text)
    {
        var validId = ReminderInput.ValidateId(id);
        // Явно переданный пустой/пробельный at — ошибка, а не «поле не передано».
        if (at is not null && string.IsNullOrWhiteSpace(at))
            throw new ReminderValidationException("Параметр at передан пустым. Укажите ISO 8601 с явным смещением или не передавайте at.");
        var hasDue = at is not null;
        var hasText = text is not null;
        if (!hasDue && !hasText)
            throw new ReminderValidationException("Нужно передать at и/или text: изменять нечего.");

        var now = _clock();
        var due = hasDue ? ReminderInput.ParseDue(at, now) : (DateTimeOffset?)null;
        var normalized = hasText ? ReminderInput.NormalizeText(text) : null;

        var result = _store.Update(validId, normalized, due, now);
        var reminder = result.Reminder;
        if (result.Status == UpdateStatus.NotFound)
            return new UpdateOutcome(false, result.Status, null, false, false, false, false, false, false, null, Array.Empty<string>());
        if (result.Status == UpdateStatus.NotPending)
        {
            var reason = reminder?.State switch
            {
                ReminderState.Sent => "Напоминание уже доставлено, изменить его нельзя.",
                ReminderState.Cancelled => "Напоминание отменено, изменить его нельзя.",
                _ => "Доставка уже начата другим обработчиком, изменить напоминание нельзя."
            };
            return new UpdateOutcome(true, result.Status, reminder, false, false, false, false, false, false, null, new[] { reason });
        }

        var warnings = new List<string>();
        var taskName = TaskNames.Reminder(validId);
        var syncPending = reminder!.State == ReminderState.Pending && reminder.SyncDueUtc is not null;
        var registered = false;
        string? taskError = null;

        // Регистрация — только если этот вызов сам сдвинул срок. Текстовая правка не должна быть
        // побочным поводом для Планировщика; уже существующий маркер снимет обход.
        if (result.Status == UpdateStatus.Updated && result.DueChanged)
        {
            var sync = _sync.Acquire(taskName, TaskSyncLease.DefaultTimeout);
            if (sync.Status != TaskSyncStatus.Acquired)
            {
                if (sync.Status == TaskSyncStatus.Busy)
                {
                    warnings.Add("Другая операция сейчас меняет это задание Windows; перерегистрация не выполнялась. Изменение сохранено в очереди, обход повторит её.");
                    Log.Write($"update {validId}: guard задачи занят, перерегистрация отложена");
                }
                else
                {
                    // Fail-closed: без межпроцессной блокировки регистрировать нельзя, иначе можно
                    // затереть срок, подтверждённый другим процессом. Маркер остаётся «грязным».
                    warnings.Add("Межпроцессная блокировка задания Windows недоступна; перерегистрация не выполнялась. Изменение сохранено в очереди, обход повторит её.");
                    Log.Write($"update {validId}: межпроцессная блокировка недоступна, перерегистрация отложена");
                }
            }
            else
            {
                using var lease = sync.Lease;
                // Перечитываем внутри guard: пока ждали, запись могли изменить, отменить или отправить.
                var current = _store.Get(validId);
                if (current is null || current.State != ReminderState.Pending)
                {
                    warnings.Add("Запись перестала быть активной до перерегистрации; задача Windows не создавалась.");
                }
                else if (current.SyncDueUtc is null)
                {
                    // Актуальный срок уже подтверждён другой операцией под тем же guard.
                    registered = true;
                }
                else if (current.SyncDueUtc != current.DueUtc)
                {
                    warnings.Add("Состояние синхронизации записи изменилось; перерегистрация отложена до обхода.");
                }
                else
                {
                    TaskRegistration registration;
                    try
                    {
                        registration = _tasks.RegisterReminder(taskName, current.DueUtc, _taskCommand, WorkerArguments());
                    }
                    catch (Exception e)
                    {
                        registration = new TaskRegistration(false, "вызов Планировщика не удался: " + e.Message);
                    }

                    if (registration.Ok)
                    {
                        var completed = _store.CompleteSync(validId, current.DueUtc.ToUnixTimeMilliseconds(), taskName);
                        var after = _store.Get(validId);
                        if (completed)
                        {
                            registered = true;
                        }
                        else if (after is null || after.State != ReminderState.Pending)
                        {
                            // Отмена/доставка/удаление во время регистрации: задача-сирота не
                            // остаётся, маркер и состояние не перезаписываются.
                            var removal = _tasks.Delete(taskName);
                            warnings.Add("Запись перестала быть активной во время перерегистрации; созданная задача Windows " +
                                         (removal.Ok ? "удалена." : "не удалена: " + removal.Error));
                        }
                        else
                        {
                            // Срок изменился до подтверждения: маркер уже указывает на новый срок,
                            // обход зарегистрирует его. Успех старого срока не объявляется.
                            warnings.Add("Срок записи изменился до завершения перерегистрации; маркер сохранён, обход повторит синхронизацию.");
                        }
                    }
                    else
                    {
                        taskError = registration.Error ?? "неизвестная ошибка регистрации";
                        if (!_store.SetLastErrorIfDue(validId, current.DueUtc.ToUnixTimeMilliseconds(), taskError))
                            Log.Write($"update {validId}: ошибка регистрации не записана — срок уже изменён: {taskError}");
                        Log.Write($"update {validId}: перерегистрация триггера не удалась: {taskError}");
                    }
                }

                reminder = _store.Get(validId) ?? reminder;
                syncPending = reminder.State == ReminderState.Pending && reminder.SyncDueUtc is not null;
            }
        }

        var sweepPresent = SafeSweepExists();
        if (syncPending)
        {
            warnings.Add(sweepPresent
                ? $"Точный триггер Windows для нового срока не подтверждён: {taskError ?? "синхронизация не завершена"}. Изменение сохранено в очереди; задача обхода повторит перерегистрацию при следующем запуске (обычно до ~5 минут), до этого в Планировщике может оставаться прежний срок."
                : $"Точный триггер Windows для нового срока не подтверждён: {taskError ?? "синхронизация не завершена"}. Задача обхода НЕ найдена: пока обход не установлен (install), автоматического повтора перерегистрации не будет.");
        }

        // Фактическое наличие триггера, если мы его не регистрировали: не обещаем больше, чем есть.
        var windowsTaskRegistered = registered;
        if (!registered && !syncPending)
        {
            try
            {
                windowsTaskRegistered = _tasks.Exists(taskName);
            }
            catch (Exception e)
            {
                Log.Write($"update {validId}: не удалось проверить задачу: {e.Message}");
                windowsTaskRegistered = false;
            }
            if (!windowsTaskRegistered)
                warnings.Add("Точный триггер Windows не найден; обход попытается перерегистрировать его при следующем запуске.");
        }
        warnings.AddRange(LauncherWarnings());

        return new UpdateOutcome(
            true,
            result.Status,
            reminder,
            result.TextChanged,
            result.DueChanged,
            result.Status == UpdateStatus.Updated,
            windowsTaskRegistered,
            syncPending,
            sweepPresent,
            taskError,
            warnings);
    }

    public SweepOutcome RunDue()
    {        var now = _clock().ToUniversalTime();
        var recovered = _store.RecoverStaleClaims(now, StaleClaimAfter);

        // Сначала доставка просроченного: ремонт будущих триггеров не должен откладывать показ.
        var claimed = _store.ClaimDue(now);
        var entries = new List<SweepEntry>();
        var sent = 0;
        var failed = 0;
        foreach (var reminder in claimed)
        {
            var token = reminder.ClaimToken ?? string.Empty;
            var result = _toast.Show(reminder.Id, reminder.Text);
            if (result.Accepted)
            {
                var marked = _store.MarkSent(reminder.Id, token, _clock().ToUniversalTime());
                if (!marked) Log.Write($"run-due {reminder.Id}: отправлено, но claim уже не наш (возможна повторная попытка другим обработчиком)");
                sent++;

                // Одноразовая задача уже не нужна: без этого задачи копились бы в корне Планировщика.
                var cleanup = _tasks.Delete(TaskNames.Reminder(reminder.Id));
                if (cleanup.Ok) _store.SetTaskName(reminder.Id, null);
                else Log.Write($"run-due {reminder.Id}: не удалось удалить задачу после доставки: {cleanup.Error}");

                entries.Add(new SweepEntry(reminder.Id, true, result.HistoryVerified, null, result.Warning));
                Log.Write($"run-due {reminder.Id}: показано (historyVerified={result.HistoryVerified})");
            }
            else
            {
                _store.MarkFailed(reminder.Id, token, result.Error ?? "неизвестная ошибка показа");
                failed++;
                entries.Add(new SweepEntry(reminder.Id, false, false, result.Error, null));
                Log.Write($"run-due {reminder.Id}: ошибка показа: {result.Error}");
            }
        }

        // Ремонт/перерегистрация будущих задач — после доставки и без права уронить обход.
        var repaired = 0;
        try
        {
            repaired = RepairMissingTasks(_clock().ToUniversalTime());
        }
        catch (Exception e)
        {
            Log.Write("run-due: ремонт задач не удался: " + e.Message);
            entries.Add(new SweepEntry("", false, false, "ремонт задач: " + e.Message, null));
        }

        // Миграция на новую команду продолжается обходом, если установка не успела её закончить.
        var migrated = 0;
        try
        {
            if (!WorkerCommandApplied)
            {
                var migration = MigrateWorkerTasks();
                migrated = migration.Migrated;
                if (migration.Migrated > 0 || migration.Retired > 0 || migration.Failed > 0 || migration.Remaining > 0 || migration.Blocked > 0)
                    Log.Write($"run-due: миграция задач: переведено {migration.Migrated}, снято просроченных {migration.Retired}, ошибок {migration.Failed}, осталось {migration.Remaining}, заблокировано {migration.Blocked}");
                if (!migration.Ok)
                    entries.Add(new SweepEntry("", false, false,
                        $"миграция задач: ошибок {migration.Failed}, осталось {migration.Remaining}, заблокировано {migration.Blocked}" +
                        (migration.Errors.Count > 0 ? " (" + string.Join("; ", migration.Errors) + ")" : ""), null));
            }
        }
        catch (Exception e)
        {
            Log.Write("run-due: миграция задач не удалась: " + e.Message);
            entries.Add(new SweepEntry("", false, false, "миграция задач: " + e.Message, null));
        }

        return new SweepOutcome(recovered, claimed.Count, sent, failed, repaired, entries, migrated);
    }

    /// <summary>
    /// Отметка о том, что задачи уже переведены на текущую команду. Файл лежит рядом с базой,
    /// поэтому изолированные хранилища (тесты, staging) не делят отметку с рабочей установкой.
    /// </summary>
    public string WorkerCommandMarkerPath =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_store.DbPath)) ?? ".", "task-command.txt");

    /// <summary>true, если отметка совпадает с текущей командой: повторная миграция не нужна.</summary>
    public bool WorkerCommandApplied
    {
        get
        {
            try
            {
                return File.Exists(WorkerCommandMarkerPath)
                       && string.Equals(File.ReadAllText(WorkerCommandMarkerPath).Trim(), _taskCommand, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }

    private void MarkWorkerCommandApplied()
    {
        try
        {
            File.WriteAllText(WorkerCommandMarkerPath, _taskCommand);
        }
        catch (Exception e)
        {
            Log.Write("не удалось записать отметку миграции: " + e.Message);
        }
    }

    /// <summary>
    /// Восстанавливает будущие точные триггеры. Сначала приоритетная очередь записей с неснятым
    /// маркером синхронизации (они выбираются по сроку из всей очереди, а не из первых 500), затем
    /// прежний ограниченный fallback обычных записей без задачи. Guard задачи берётся коротким
    /// таймаутом, а весь проход ограничен бюджетом: обход не должен стоять по 15 с на запись.
    /// </summary>
    private int RepairMissingTasks(DateTimeOffset now)
    {
        var started = DateTimeOffset.UtcNow;
        var repaired = 0;

        // 1. «Грязные» pending, по сроку. Ключи (due, id) устойчивы: регистрация не меняет срок.
        long? afterDue = null;
        string? afterId = null;
        while (repaired < RepairBatchLimit && DateTimeOffset.UtcNow - started < RepairBudget)
        {
            var page = _store.PagePendingSync(afterDue, afterId, RepairSyncPageSize);
            if (page.Count == 0) break;
            foreach (var candidate in page)
            {
                if (repaired >= RepairBatchLimit || DateTimeOffset.UtcNow - started >= RepairBudget) break;
                afterDue = candidate.DueUtc.ToUnixTimeMilliseconds();
                afterId = candidate.Id;
                if (RepairOne(candidate.Id, now, syncOnly: true, out var fixedNow) && fixedNow) repaired++;
            }
            if (page.Count < RepairSyncPageSize) break;
        }

        // 2. Fallback: обычные будущие записи без задачи (прежнее осмысленное поведение).
        foreach (var listed in _store.List(includeFinished: false))
        {
            if (repaired >= RepairBatchLimit || DateTimeOffset.UtcNow - started >= RepairBudget) break;
            if (listed.State != ReminderState.Pending) continue;
            if (listed.DueUtc <= now) continue;
            if (listed.SyncDueUtc is not null) continue; // уже обработано приоритетной очередью
            if (RepairOne(listed.Id, now, syncOnly: false, out var fixedNow) && fixedNow) repaired++;
        }

        return repaired;
    }

    /// <summary>Одна попытка восстановления под guard задачи. false — занято/запись больше не подходит.</summary>
    private bool RepairOne(string id, DateTimeOffset now, bool syncOnly, out bool repaired)
    {
        repaired = false;
        var taskName = TaskNames.Reminder(id);
        var sync = _sync.Acquire(taskName, RepairLockTimeout);
        if (!sync.IsAcquired)
        {
            Log.Write($"run-due {id}: guard задачи {(sync.Status == TaskSyncStatus.Busy ? "занят" : "недоступен")}, восстановление отложено");
            return false;
        }
        using var lease = sync.Lease;

        // Перечитываем внутри guard: пока ждали, запись могли изменить, отправить или отменить.
        var reminder = _store.Get(id);
        if (reminder is null || reminder.State != ReminderState.Pending) return false;
        if (reminder.DueUtc <= now) return false;

        // Маркер sync_due требует перерегистрации даже при существующей задаче: она может
        // указывать на прежний срок. Иначе задача не считается актуальной.
        var needsSync = reminder.SyncDueUtc is not null;
        if (syncOnly && !needsSync) return false;
        if (!syncOnly && needsSync) return false;
        if (!needsSync && reminder.TaskName is not null && _tasks.Exists(taskName)) return false;

        var registration = _tasks.RegisterReminder(taskName, reminder.DueUtc, _taskCommand, WorkerArguments());
        if (!registration.Ok)
        {
            if (!_store.SetLastErrorIfDue(id, reminder.DueUtc.ToUnixTimeMilliseconds(), registration.Error))
                Log.Write($"run-due {id}: ошибка регистрации не записана — срок уже изменён");
            return true;
        }

        var after = _store.Get(id);
        if (after is null || after.State != ReminderState.Pending || after.DueUtc != reminder.DueUtc)
        {
            // Отмена/доставка/новая правка во время регистрации: не оставляем ни задачу, ни
            // метаданные на уже неактуальной записи.
            var removal = _tasks.Delete(taskName);
            Log.Write($"run-due {id}: запись изменилась во время восстановления; задача Windows " +
                      (removal.Ok ? "удалена" : "не удалена: " + removal.Error));
            return true;
        }

        if (needsSync)
        {
            if (!_store.CompleteSync(id, reminder.DueUtc.ToUnixTimeMilliseconds(), taskName))
            {
                // Срок изменился ещё раз, пока регистрировали: маркер оставлен, следующий
                // проход зарегистрирует уже актуальный срок. Успех старого срока не объявляем.
                Log.Write($"run-due {id}: маркер синхронизации не снят, срок успел измениться");
                return true;
            }
        }
        else
        {
            _store.SetTaskName(id, taskName);
        }

        repaired = true;
        Log.Write($"run-due {id}: точный триггер перерегистрирован");
        return true;
    }

    public int RepairTasks() => RepairMissingTasks(_clock().ToUniversalTime());

    /// <summary>
    /// Переводит уже поставленные точные триггеры на текущую команду (лаунчер). Задача
    /// перерегистрируется только если её Action/Command отличается: срок берётся из очереди,
    /// поэтому граница запуска и интерактивный контекст сохраняются, а записи не теряются.
    /// Лимит за проход защищает от лавины вызовов schtasks; остаток вернётся следующим обходом.
    /// </summary>
    public MigrationOutcome MigrateWorkerTasks(int limit = MigrationBatchLimit)
    {
        var now = _clock().ToUniversalTime();
        var migrated = 0;
        var alreadyOk = 0;
        var failed = 0;
        var remaining = 0;
        var retired = 0;
        var blocked = 0;
        var attempted = 0;
        var errors = new List<string>();

        // Обход считается рабочим и невидимым, только если его задача существует и запускает ту же
        // команду. Пока это не так, просроченные legacy-триггеры не снимаем: доставку было бы нечем
        // подстраховать.
        var sweepReady = TaskLauncher.SameCommand(SafeActionCommand(TaskNames.Sweep), _taskCommand);

        long? afterDue = null;
        string? afterId = null;

        while (true)
        {
            // Отдельная постраничная выборка pending: общий List ограничен 500 записями и включает
            // sending, из-за чего хвост очереди раньше оставался незамеченным.
            var page = _store.PagePending(afterDue, afterId, MigrationPageSize);
            if (page.Count == 0) break;

            var stop = false;
            foreach (var reminder in page)
            {
                var taskName = TaskNames.Reminder(reminder.Id);
                var current = SafeActionCommand(taskName);
                var needsWork = current is not null && !TaskLauncher.SameCommand(current, _taskCommand);

                if (needsWork && attempted >= limit)
                {
                    // Хвост не просмотрен. Остаток считаем от последней обработанной позиции,
                    // поэтому миграция не будет ошибочно объявлена завершённой.
                    remaining = _store.CountPending(afterDue, afterId);
                    stop = true;
                    break;
                }

                if (current is null)
                {
                    // Задачи нет: будущее восстановит RepairMissingTasks, просроченное доставит обход.
                }
                else if (!needsWork)
                {
                    alreadyOk++;
                }
                else
                {
                    // Те же изменения задания, что у update: общий guard по ID и перечитывание
                    // записи внутри, чтобы не пересечься с переносом срока в другом процессе.
                    var sync = _sync.Acquire(taskName, RepairLockTimeout);
                    if (!sync.IsAcquired)
                    {
                        failed++;
                        var reason = sync.Status == TaskSyncStatus.Busy ? "задание занято другой операцией" : "межпроцессная блокировка недоступна";
                        errors.Add($"{reminder.Id}: {reason}, миграция отложена");
                        Log.Write($"migrate {reminder.Id}: {reason}, миграция отложена");
                    }
                    else
                    {
                        using var lease = sync.Lease;
                        var fresh = _store.Get(reminder.Id);
                        if (fresh is null || fresh.State != ReminderState.Pending)
                        {
                            // Пока ждали guard, запись отменили или доставили: задачу не трогаем.
                        }
                        else if (fresh.DueUtc <= now)
                        {
                            if (!sweepReady)
                            {
                                // Снимать единственный триггер, пока невидимый обход не подтверждён, нельзя.
                                blocked++;
                                Log.Write($"migrate {fresh.Id}: просроченный legacy-триггер не снят, обход не подтверждён");
                            }
                            else
                            {
                                attempted++;
                                var removal = _tasks.Delete(taskName);
                                if (removal.Ok)
                                {
                                    retired++;
                                    _store.SetTaskName(fresh.Id, null);
                                    Log.Write($"migrate {fresh.Id}: просроченный legacy-триггер снят, запись остаётся в очереди для обхода");
                                }
                                else
                                {
                                    failed++;
                                    errors.Add($"{fresh.Id}: не удалось снять legacy-триггер: {removal.Error}");
                                    Log.Write($"migrate {fresh.Id}: не удалось снять legacy-триггер: {removal.Error}");
                                }
                            }
                        }
                        else
                        {
                            attempted++;
                            var registration = _tasks.RegisterReminder(taskName, fresh.DueUtc, _taskCommand, WorkerArguments());
                            if (registration.Ok)
                            {
                                migrated++;
                                if (fresh.SyncDueUtc is not null
                                    && !_store.CompleteSync(fresh.Id, fresh.DueUtc.ToUnixTimeMilliseconds(), taskName))
                                {
                                    Log.Write($"migrate {fresh.Id}: маркер синхронизации не снят, срок успел измениться");
                                }
                                else if (fresh.SyncDueUtc is null)
                                {
                                    _store.SetTaskName(fresh.Id, taskName);
                                }
                                Log.Write($"migrate {fresh.Id}: задача переведена на {_taskCommand} (было {current})");
                            }
                            else
                            {
                                failed++;
                                _store.SetLastErrorIfDue(fresh.Id, fresh.DueUtc.ToUnixTimeMilliseconds(), registration.Error);
                                errors.Add($"{fresh.Id}: {registration.Error}");
                                Log.Write($"migrate {fresh.Id}: не удалось перевести задачу: {registration.Error}");
                            }
                        }
                    }
                }

                afterDue = reminder.DueUtc.ToUnixTimeMilliseconds();
                afterId = reminder.Id;
            }

            if (stop) break;
            if (page.Count < MigrationPageSize) break;
        }

        var outcome = new MigrationOutcome(migrated, alreadyOk, failed, remaining, errors, retired, blocked);
        // Отметка пишется только когда переводить больше нечего: следующий обход продолжит с остатка.
        if (outcome.Ok) MarkWorkerCommandApplied();
        return outcome;
    }

    private string? SafeActionCommand(string taskName)
    {
        try
        {
            return _tasks.GetActionCommand(taskName);
        }
        catch (Exception e)
        {
            Log.Write($"не удалось прочитать команду задачи {taskName}: {e.Message}");
            return null;
        }
    }
}
