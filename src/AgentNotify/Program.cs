using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentNotify.Core;
using AgentNotify.Mcp;
using AgentNotify.Windows;

namespace AgentNotify;

public static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        try
        {
            ApplyHome(args);
            var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
            return command switch
            {
                "context" => ContextCommand(args),
                "schedule" => ScheduleCommand(args),
                "list" => ListCommand(args),
                "update" => UpdateCommand(args),
                "cancel" => CancelCommand(args),
                "purge" => PurgeCommand(args),
                "run-due" => RunDueCommand(args),
                "install" => InstallCommand(args),
                "uninstall" => UninstallCommand(args),
                "status" => StatusCommand(args),
                "repair" => RepairCommand(args),
                "test-toast" => TestToastCommand(args),
                "test-clear-history" => TestClearHistoryCommand(args),
                "serve-mcp" => ServeMcpCommand(args),
                "help" or "--help" or "-h" => Help(),
                "version" or "--version" => Version(),
                _ => Usage($"Неизвестная команда: {command}")
            };
        }
        catch (ReminderValidationException e)
        {
            Console.Error.WriteLine("Ошибка ввода: " + e.Message);
            return 2;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Ошибка: " + e.Message);
            Log.Write("fatal: " + e);
            return 1;
        }
    }

    private static void ApplyHome(string[] args)
    {
        for (var i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == "--home")
                Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, Path.GetFullPath(args[i + 1]));
        }
    }

    private static int Help()
    {
        Console.WriteLine("""
            AgentNotify — разовые напоминания Windows из OpenCode (без LLM при доставке).

            Команды:
              context [--json]                     локальное время, смещение, часовой пояс, DST
              schedule --at <ISO8601 с offset> --text <текст> [--json]
                                                  создать напоминание и зарегистрировать точный триггер Windows
              list [--all] [--json] [--text <фрагмент>] [--from <ISO с offset>] [--to <ISO с offset>]
                   [--overdue] [--id <rem_id>] [--limit <1..500>] [--cursor <nextCursor>]
                                                  очередь (активные; --all включает историю). Поиск только читает
                                                  и объединяет критерии по AND; --from включительно, --to исключительно
              update <id> [--at <ISO с offset>] [--text <текст>] [--json]
                                                  изменить срок и/или текст активной записи, сохранив id; нужна
                                                  хотя бы одна из опций. Код 3 — изменение сохранено, но точный
                                                  триггер Windows ещё не подтверждён (syncPending)
              cancel <id> [--purge] [--json]       отменить и удалить задачу Windows (--purge удаляет запись)
              purge <id> [--json]                  удалить из истории доставленную/отменённую запись
              run-due [--json]                     обход очереди: доставить просроченные, восстановить задачи
              install [--json]                     зарегистрировать периодический обход (вход + каждые 5 минут)
              uninstall [--purge-data] [--json]    удалить задачи проекта (--purge-data удаляет базу)
              status [--json]                      состояние очереди и задач Windows
              repair [--json]                      перерегистрировать отсутствующие точные триггеры
              test-toast --text <текст>            показать пробный toast (ручная проверка)
              test-clear-history                   очистить историю уведомлений AgentNotify
              serve-mcp                            MCP-сервер stdio
              help | version

            Данные: %LOCALAPPDATA%\AgentNotification (переопределяется AGENTNOTIFY_HOME или --home).
            Время принимается только с явным смещением: 2026-11-27T09:30:00+03:00.
            """);
        return 0;
    }

    private static int Version()
    {
        Console.WriteLine("AgentNotify 1.0.0");
        return 0;
    }

    private static int Usage(string? message)
    {
        if (message is not null) Console.Error.WriteLine(message);
        Console.Error.WriteLine("Справка: AgentNotify help");
        return 2;
    }

    private static Dictionary<string, string?> Options(string[] args, int start = 1, HashSet<string>? flags = null)
    {
        flags ??= FlagNames;
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = start; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                result["$" + result.Count] = token;
                continue;
            }
            var name = token[2..];
            if (flags.Contains(name))
            {
                result[name] = "true";
            }
            else
            {
                if (i + 1 >= args.Length) throw new ReminderValidationException($"Опция --{name} требует значение.");
                result[name] = args[++i];
            }
        }
        return result;
    }

    private static readonly HashSet<string> FlagNames = new(StringComparer.Ordinal)
    {
        "json", "all", "purge", "purge-data", "quiet", "help", "overdue"
    };

    private static string? Value(Dictionary<string, string?> options, string key) =>
        options.TryGetValue(key, out var value) ? value : null;

    private static bool Has(Dictionary<string, string?> options, string key) => options.ContainsKey(key);

    private static string Require(Dictionary<string, string?> options, string key)
    {
        var value = Value(options, key);
        if (string.IsNullOrWhiteSpace(value))
            throw new ReminderValidationException($"Нужна опция --{key}.");
        return value;
    }

    private static ReminderService Service() => new(ReminderStore.OpenDefault());

    private static object ContextObject(TimeContext context) => new
    {
        nowLocal = context.NowLocal,
        nowUtc = context.NowUtc,
        offset = context.Offset,
        timeZoneWindows = context.WindowsTimeZoneId,
        timeZoneIana = context.IanaTimeZoneId,
        isDaylightSavingTime = context.IsDaylightSavingTime,
        timeZoneDisplayName = context.TimeZoneDisplayName
    };

    private static object CountsObject((int Pending, int Sending, int Sent, int Cancelled) counts) => new
    {
        pending = counts.Pending,
        sending = counts.Sending,
        sent = counts.Sent,
        cancelled = counts.Cancelled
    };

    private static int ContextCommand(string[] args)
    {
        var options = Options(args);
        var context = TimeContext.Current();
        if (Has(options, "json"))
        {
            PrintJson(ContextObject(context));
            return 0;
        }
        Console.WriteLine($"Сейчас:       {context.NowLocal} (UTC {context.NowUtc})");
        Console.WriteLine($"Смещение:     {context.Offset}");
        Console.WriteLine($"Часовой пояс: {context.WindowsTimeZoneId}" + (context.IanaTimeZoneId is null ? "" : $" / {context.IanaTimeZoneId}"));
        Console.WriteLine($"Летнее время: {(context.IsDaylightSavingTime ? "да" : "нет")}");
        return 0;
    }

    private static int ScheduleCommand(string[] args)
    {
        var options = Options(args);
        var at = Require(options, "at");
        var text = Require(options, "text");
        var outcome = Service().Schedule(at, text);
        if (Has(options, "json"))
        {
            PrintJson(new
            {
                id = outcome.Reminder.Id,
                text = outcome.Reminder.Text,
                dueLocal = outcome.Reminder.DueLocalIso,
                dueUtc = outcome.Reminder.DueUtcIso,
                offset = TimeContext.FormatOffset(outcome.Reminder.Offset),
                timeContext = ContextObject(TimeContext.Current()),
                windowsTaskRegistered = outcome.WindowsTaskRegistered,
                sweepTaskRegistered = outcome.SweepTaskPresent,
                windowsTaskName = outcome.Reminder.TaskName,
                warnings = outcome.Warnings
            });
            return outcome.WindowsTaskRegistered ? 0 : 3;
        }

        Console.WriteLine($"Создано напоминание {outcome.Reminder.Id}");
        Console.WriteLine($"Время: {outcome.Reminder.DueLocalIso} (UTC {outcome.Reminder.DueUtcIso})");
        Console.WriteLine($"Текст: {outcome.Reminder.Text}");
        Console.WriteLine(outcome.WindowsTaskRegistered
            ? $"Точный триггер Windows: {outcome.Reminder.TaskName}"
            : (outcome.SweepTaskPresent ? "Точный триггер Windows НЕ зарегистрирован; задача обхода есть, доставка возможна только при её запуске, не по точному сроку." : "Точный триггер Windows НЕ зарегистрирован, задача обхода не найдена: доставка не гарантирована."));
        foreach (var warning in outcome.Warnings) Console.WriteLine("Внимание: " + warning);
        return outcome.WindowsTaskRegistered ? 0 : 3;
    }

    private static int ListCommand(string[] args)
    {
        var options = Options(args);
        var hasFilters = Has(options, "text") || Has(options, "from") || Has(options, "to")
                         || Has(options, "id") || Has(options, "limit") || Has(options, "cursor")
                         || Has(options, "overdue");
        if (Has(options, "all") && hasFilters)
            throw new ReminderValidationException("Опция --all несовместима с поиском: поиск показывает только активные pending/sending.");

        var service = Service();
        if (Has(options, "all"))
        {
            var history = service.List(includeFinished: true);
            if (Has(options, "json"))
            {
                PrintJson(new
                {
                    timeContext = ContextObject(TimeContext.Current()),
                    reminders = history.Select(r => r.ToPublicObject()),
                    counts = CountsObject(service.Counts())
                });
                return 0;
            }
            if (history.Count == 0)
            {
                Console.WriteLine("Очередь пуста.");
                return 0;
            }
            foreach (var reminder in history)
                Console.WriteLine($"{reminder.DueLocalIso}  {reminder.Id}  [{reminder.State.ToDb()}]  {FirstLine(reminder.Text)}");
            return 0;
        }

        var limit = ReminderSearchInput.ParseLimit(Value(options, "limit"));
        var query = ReminderSearchInput.Create(
            Value(options, "text"),
            Value(options, "from"),
            Value(options, "to"),
            Has(options, "overdue"),
            Value(options, "id"),
            limit,
            Value(options, "cursor"),
            out var after);
        var page = service.Search(query, limit, after);
        var nextCursor = page.Next is null ? null : ReminderCursor.Encode(page.Next.DueMs, page.Next.Id);

        if (Has(options, "json"))
        {
            PrintJson(new
            {
                timeContext = ContextObject(TimeContext.Current()),
                sweepTaskRegistered = service.SweepTaskPresent(),
                reminders = page.Reminders.Select(r => r.ToPublicObject()),
                counts = CountsObject(service.Counts()),
                returned = page.Returned,
                total = page.Total,
                truncated = page.Truncated,
                nextCursor,
                appliedFilters = new
                {
                    text = query.Text,
                    from = query.FromUtc?.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'"),
                    to = query.ToUtc?.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'"),
                    overdue = query.Overdue,
                    id = query.Id,
                    limit
                }
            });
            return 0;
        }

        if (page.Reminders.Count == 0)
        {
            Console.WriteLine("Среди активных напоминаний ничего не найдено.");
            return 0;
        }
        foreach (var reminder in page.Reminders)
            Console.WriteLine($"{reminder.DueLocalIso}  {reminder.Id}  [{reminder.State.ToDb()}]  {FirstLine(reminder.Text)}");
        if (page.Truncated)
            Console.WriteLine($"Показано {page.Returned} из {page.Total}; остаток: list --cursor {nextCursor}");
        return 0;
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n')[0];
        return line.Length > 80 ? line[..80] + "…" : line;
    }

    private static int UpdateCommand(string[] args)
    {
        var options = Options(args);
        var id = Value(options, "$0") ?? throw new ReminderValidationException("Нужен id напоминания.");
        if (!Has(options, "at") && !Has(options, "text"))
            throw new ReminderValidationException("Нужна хотя бы одна опция изменения: --at и/или --text.");

        var outcome = Service().Update(id, Value(options, "at"), Value(options, "text"));
        if (!outcome.Found)
        {
            Console.Error.WriteLine("Напоминание не найдено: " + id);
            return 1;
        }

        var notUpdatable = outcome.Status == UpdateStatus.NotPending;
        var reminder = outcome.Reminder;
        if (Has(options, "json"))
        {
            PrintJson(new
            {
                id = reminder?.Id ?? id,
                changed = outcome.Changed,
                textChanged = outcome.TextChanged,
                dueChanged = outcome.DueChanged,
                status = notUpdatable ? "not_updatable" : outcome.Changed ? "updated" : "unchanged",
                state = reminder?.State.ToDb(),
                text = reminder?.Text,
                dueLocal = reminder?.DueLocalIso,
                dueCurrentZoneLocal = reminder?.DueCurrentZoneLocalIso,
                dueUtc = reminder?.DueUtcIso,
                windowsTaskRegistered = outcome.WindowsTaskRegistered,
                syncPending = outcome.SyncPending,
                sweepTaskRegistered = outcome.SweepTaskPresent,
                windowsTaskName = reminder?.TaskName,
                warnings = outcome.Warnings
            });
            return notUpdatable ? 1 : outcome.SyncPending ? 3 : 0;
        }

        if (notUpdatable)
        {
            foreach (var warning in outcome.Warnings) Console.Error.WriteLine(warning);
            return 1;
        }
        Console.WriteLine(outcome.Changed
            ? $"Изменено {reminder!.Id}; срок: {reminder.DueLocalIso}, текст сохранён."
            : $"Значения уже совпадают; {reminder!.Id} не изменялось.");
        if (reminder is not null) Console.WriteLine($"Время: {reminder.DueLocalIso} (UTC {reminder.DueUtcIso})");
        Console.WriteLine(outcome.WindowsTaskRegistered
            ? "Точный триггер Windows актуален."
            : outcome.SyncPending
                ? "Точный триггер Windows ещё не подтверждён; изменение сохранено в очереди, обход повторит перерегистрацию."
                : "Точный триггер Windows не найден; обход перерегистрирует его при следующем запуске.");
        foreach (var warning in outcome.Warnings) Console.WriteLine("Внимание: " + warning);
        return outcome.SyncPending ? 3 : 0;
    }

    private static int CancelCommand(string[] args)
    {        var options = Options(args);
        var id = Value(options, "$0") ?? throw new ReminderValidationException("Нужен id напоминания.");
        var outcome = Service().Cancel(id, Has(options, "purge"));
        if (!outcome.Found)
        {
            Console.Error.WriteLine("Напоминание не найдено: " + id);
            return 1;
        }
        var cancelled = outcome.Status == CancelStatus.Cancelled;
        if (Has(options, "json"))
        {
            PrintJson(new
            {
                id,
                cancelled,
                status = outcome.Status == CancelStatus.Cancelled ? "cancelled" : outcome.Status == CancelStatus.AlreadySent ? "already_sent" : "in_progress",
                purged = cancelled && Has(options, "purge"),
                windowsTaskRemoved = outcome.WindowsTaskRemoved,
                warnings = outcome.Warnings
            });
            return cancelled ? 0 : 1;
        }
        if (!cancelled)
        {
            foreach (var warning in outcome.Warnings) Console.Error.WriteLine(warning);
            return 1;
        }
        Console.WriteLine($"Отменено {id}; задача Windows {(outcome.WindowsTaskRemoved ? "удалена или отсутствовала" : "не удалена")}.");
        foreach (var warning in outcome.Warnings) Console.WriteLine("Внимание: " + warning);
        return 0;
    }

    /// <summary>Удаление записи из истории (sent/cancelled). Активную запись сначала отменяют.</summary>
    private static int PurgeCommand(string[] args)
    {
        var options = Options(args);
        var id = Value(options, "$0") ?? throw new ReminderValidationException("Нужен id напоминания.");
        var validId = ReminderInput.ValidateId(id);
        var store = ReminderStore.OpenDefault();
        var reminder = store.Get(validId);
        if (reminder is null)
        {
            Console.Error.WriteLine("Напоминание не найдено: " + validId);
            return 1;
        }
        if (reminder.State is ReminderState.Pending or ReminderState.Sending)
        {
            Console.Error.WriteLine("Запись ещё активна: сначала отмените её (cancel), затем purge.");
            return 1;
        }
        var registrar = new WindowsTaskScheduler();
        var removedTask = registrar.Delete(reminder.TaskName ?? TaskNames.Reminder(validId));
        var deleted = store.Purge(validId);
        if (Has(options, "json")) PrintJson(new { id = validId, purged = deleted, windowsTaskRemoved = removedTask.Ok });
        else Console.WriteLine($"Запись {validId} удалена из истории; задача Windows {(removedTask.Ok ? "удалена или отсутствовала" : "не удалена")}.");
        return deleted ? 0 : 1;
    }

    private static int RunDueCommand(string[] args)    {
        var options = Options(args);
        var outcome = Service().RunDue();
        var summary = new
        {
            recoveredStale = outcome.RecoveredStale,
            repairedTasks = outcome.RepairedTasks,
            migratedTasks = outcome.MigratedTasks,
            claimed = outcome.Claimed,
            sent = outcome.Sent,
            failed = outcome.Failed,
            entries = outcome.Entries.Select(e => new { id = e.Id, accepted = e.Accepted, historyVerified = e.HistoryVerified, error = e.Error, warning = e.Warning })
        };
        if (Has(options, "json")) PrintJson(summary);
        else if (!Has(options, "quiet")) Console.WriteLine($"Обход: забрано {outcome.Claimed}, отправлено {outcome.Sent}, ошибок {outcome.Failed}, восстановлено claim {outcome.RecoveredStale}, перерегистрировано задач {outcome.RepairedTasks}, переведено на лаунчер {outcome.MigratedTasks}.");
        return outcome.Failed == 0 ? 0 : 4;
    }

    private static int InstallCommand(string[] args)
    {
        var options = Options(args);
        var store = ReminderStore.OpenDefault();
        var registrar = new WindowsTaskScheduler();
        var exe = Environment.ProcessPath!;
        var service = new ReminderService(store, registrar, new WindowsToastPresenter());
        var command = service.TaskCommand;
        var sweep = registrar.RegisterSweep(TaskNames.Sweep, command, ReminderService.WorkerArguments(), DateTimeOffset.Now);
        var migration = sweep.Ok ? service.MigrateWorkerTasks() : MigrationOutcome.Empty;
        var repaired = sweep.Ok ? service.RepairTasks() : 0;
        var warnings = service.LauncherWarnings().ToList();
        if (!sweep.Ok && sweep.Error is not null) warnings.Add("Обход: " + sweep.Error);
        warnings.AddRange(migration.Errors);
        if (migration.Blocked > 0) warnings.Add($"Просроченных legacy-триггеров не снято: {migration.Blocked}. Задача обхода не подтверждена как невидимая, повтор после успешного install.");
        if (Has(options, "json"))
        {
            PrintJson(new
            {
                sweepTask = TaskNames.Sweep,
                sweepRegistered = sweep.Ok,
                sweepCommand = command,
                usesLauncher = service.UsesLauncher,
                error = sweep.Error,
                migrated = migration.Migrated,
                retiredOverdueTriggers = migration.Retired,
                blockedOverdueTriggers = migration.Blocked,
                migrationAlreadyOk = migration.AlreadyOk,
                migrationFailed = migration.Failed,
                migrationRemaining = migration.Remaining,
                migrationErrors = migration.Errors,
                repairedTasks = repaired,
                launcherPresent = service.UsesLauncher,
                home = AppPaths.Home,
                exe,
                warnings
            });
        }
        else
        {
            Console.WriteLine($"Обход: {TaskNames.Sweep} — {(sweep.Ok ? "зарегистрирован (при входе и каждые 5 минут)" : "ошибка: " + sweep.Error)}");
            Console.WriteLine($"Команда задач: {command} ({(service.UsesLauncher ? "оконно-невидимый лаунчер" : "консольный exe напрямую")})");
            Console.WriteLine($"Переведено будущих задач на лаунчер: {migration.Migrated}, снято просроченных legacy-триггеров: {migration.Retired}, уже было: {migration.AlreadyOk}, ошибок: {migration.Failed}, осталось на следующий проход: {migration.Remaining}, заблокировано (обход не подтверждён): {migration.Blocked}");
            Console.WriteLine($"Перерегистрировано отсутствующих точных триггеров: {repaired}");
            Console.WriteLine($"Данные: {AppPaths.Home}");
            Console.WriteLine($"Exe: {exe}");
            foreach (var warning in warnings) Console.WriteLine("Внимание: " + warning);
        }
        return sweep.Ok && migration.Ok ? 0 : 1;
    }

    private static int UninstallCommand(string[] args)
    {
        var options = Options(args);
        var store = ReminderStore.OpenDefault();
        var registrar = new WindowsTaskScheduler();
        var warnings = new List<string>();
        var sweep = registrar.Delete(TaskNames.Sweep);
        if (!sweep.Ok) warnings.Add("Обход: " + sweep.Error);
        var removed = 0;
        foreach (var reminder in store.List(includeFinished: true))
        {
            var name = reminder.TaskName ?? TaskNames.Reminder(reminder.Id);
            var result = registrar.Delete(name);
            if (!result.Ok) warnings.Add($"Задача {name}: {result.Error}");
            else removed++;
        }
        var dataDeleted = false;
        if (Has(options, "purge-data"))
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var path = AppPaths.DbPath + suffix;
                if (File.Exists(path)) File.Delete(path);
            }
            dataDeleted = true;
        }
        if (Has(options, "json"))
            PrintJson(new { sweepTaskRemoved = sweep.Ok, reminderTasksProcessed = removed, dataDeleted, warnings });
        else
        {
            Console.WriteLine($"Обход удалён: {sweep.Ok}. Обработано задач напоминаний: {removed}." + (dataDeleted ? " База удалена." : ""));
            foreach (var warning in warnings) Console.WriteLine("Внимание: " + warning);
        }
        return warnings.Count == 0 ? 0 : 1;
    }

    private static int StatusCommand(string[] args)
    {
        var options = Options(args);
        var store = ReminderStore.OpenDefault();
        var counts = store.Counts();
        var pending = store.List(includeFinished: false);
        var registrar = new WindowsTaskScheduler();
        var sweepExists = registrar.Exists(TaskNames.Sweep);
        var launcher = TaskLauncher.Resolve(Environment.ProcessPath);
        var sweepCommand = sweepExists ? registrar.GetActionCommand(TaskNames.Sweep) : null;
        var sweepUsesLauncher = launcher is not null && TaskLauncher.SameCommand(sweepCommand, launcher);
        var missingTasks = pending.Where(r => r.DueUtc > DateTimeOffset.UtcNow && (r.TaskName is null || !registrar.Exists(r.TaskName))).Select(r => r.Id).ToArray();
        var context = TimeContext.Current();
        if (Has(options, "json"))
        {
            PrintJson(new
            {
                home = AppPaths.Home,
                db = AppPaths.DbPath,
                nowLocal = context.NowLocal,
                timeZoneWindows = context.WindowsTimeZoneId,
                timeZoneIana = context.IanaTimeZoneId,
                counts = CountsObject(counts),                sweepTask = TaskNames.Sweep,
                sweepTaskExists = sweepExists,
                sweepTaskInfo = sweepExists ? WindowsTaskScheduler.LastRunInfo(TaskNames.Sweep) : null,
                sweepCommand,
                sweepUsesLauncher,
                launcherPresent = launcher is not null,
                launcherPath = launcher,
                pendingWithoutTask = missingTasks
            });
            return missingTasks.Length == 0 && sweepExists ? 0 : 3;
        }
        Console.WriteLine($"Сейчас: {context.NowLocal} ({context.WindowsTimeZoneId})");
        Console.WriteLine($"Данные: {AppPaths.DbPath}");
        Console.WriteLine($"Очередь: pending {counts.Pending}, sending {counts.Sending}, sent {counts.Sent}, cancelled {counts.Cancelled}");
        Console.WriteLine($"Обход {TaskNames.Sweep}: {(sweepExists ? "есть" : "НЕТ")}" + (sweepExists ? $" [{WindowsTaskScheduler.LastRunInfo(TaskNames.Sweep)}]" : ""));
        Console.WriteLine($"Запуск задач: {(launcher is null ? "лаунчер не найден (консольный exe напрямую)" : "лаунчер " + launcher)}" + (sweepExists ? (sweepUsesLauncher ? "; обход через лаунчер" : "; обход ещё напрямую, выполните repair или install") : ""));
        if (missingTasks.Length > 0) Console.WriteLine("Без точного триггера: " + string.Join(", ", missingTasks));
        return missingTasks.Length == 0 && sweepExists ? 0 : 3;
    }

    private static int RepairCommand(string[] args)
    {
        var options = Options(args);
        var service = Service();
        var migration = service.MigrateWorkerTasks();
        var repaired = service.RepairTasks();
        if (Has(options, "json"))
            PrintJson(new
            {
                migrated = migration.Migrated,
                retiredOverdueTriggers = migration.Retired,
                blockedOverdueTriggers = migration.Blocked,
                alreadyOk = migration.AlreadyOk,
                failed = migration.Failed,
                remaining = migration.Remaining,
                errors = migration.Errors,
                repaired,
                command = service.TaskCommand,
                usesLauncher = service.UsesLauncher
            });
        else
        {
            Console.WriteLine($"Команда задач: {service.TaskCommand} ({(service.UsesLauncher ? "лаунчер" : "консольный exe")})");
            Console.WriteLine($"Переведено будущих задач на лаунчер: {migration.Migrated}, снято просроченных legacy-триггеров: {migration.Retired}, уже было: {migration.AlreadyOk}, ошибок: {migration.Failed}, осталось: {migration.Remaining}, заблокировано: {migration.Blocked}");
            Console.WriteLine($"Перерегистрировано отсутствующих точных триггеров: {repaired}");
            if (migration.Blocked > 0) Console.WriteLine("Внимание: просроченные legacy-триггеры не сняты, потому что задача обхода не подтверждена как невидимая (запустите install).");
            foreach (var error in migration.Errors) Console.WriteLine("Внимание: " + error);
        }
        return migration.Ok ? 0 : 1;
    }

    private static int TestToastCommand(string[] args)
    {
        var options = Options(args);
        var text = Require(options, "text");
        var id = "test_" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var result = new WindowsToastPresenter().Show(id, text);
        if (Has(options, "json"))
        {
            PrintJson(new { accepted = result.Accepted, historyVerified = result.HistoryVerified, error = result.Error, warning = result.Warning, tag = id });
            return result.Accepted ? 0 : 1;
        }
        Console.WriteLine(result.Accepted ? "Windows принял toast." : "Ошибка: " + result.Error);
        if (result.Warning is not null) Console.WriteLine("Внимание: " + result.Warning);
        return result.Accepted ? 0 : 1;
    }

    private static int TestClearHistoryCommand(string[] args)
    {
        Microsoft.Toolkit.Uwp.Notifications.ToastNotificationManagerCompat.History.Clear();
        Console.WriteLine("История уведомлений AgentNotify очищена.");
        return 0;
    }

    private static int ServeMcpCommand(string[] args)
    {
        var service = Service();
        Log.Write("mcp: сервер запущен");
        new McpServer(service).Run(Console.OpenStandardInput(), Console.OpenStandardOutput());
        Log.Write("mcp: stdin закрыт, сервер завершился");
        return 0;
    }

    private static void PrintJson(object value) => Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
}


