using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace AgentNotify.Core;

/// <summary>Результат точечного изменения: состояние после вызова и какие поля реально изменились.</summary>
public sealed record ReminderUpdateResult(UpdateStatus Status, Reminder? Reminder, bool TextChanged, bool DueChanged);

/// <summary>
/// Постоянная очередь напоминаний в SQLite. Все операции короткоживущие: одно соединение на вызов,
/// WAL и busy_timeout, запись через BEGIN IMMEDIATE, чтобы конкурирующие обработчики не забирали одну запись дважды.
/// </summary>
public sealed class ReminderStore
{
    public string DbPath { get; }

    public ReminderStore(string dbPath) => DbPath = dbPath;

    public static ReminderStore OpenDefault()
    {
        AppPaths.EnsureDirectories();
        var store = new ReminderStore(AppPaths.DbPath);
        store.Initialize();
        return store;
    }

    private SqliteConnection Connect()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // WAL уже даёт согласованное чтение между соединениями; shared-cache SQLite не
            // рекомендует и под нагрузкой отдаёт SQLITE_LOCKED вместо ожидания busy_timeout.
            Cache = SqliteCacheMode.Private,
            Pooling = false
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=10000; PRAGMA synchronous=FULL;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public void Initialize()
    {
        AppPaths.EnsureDirectories();

        // Несколько процессов (MCP, обход, установка) могут одновременно открыть базу прежней
        // версии. DDL выполняется в одной транзакции BEGIN IMMEDIATE, поэтому проверка колонок и
        // ALTER не расходятся: второй процесс ждёт (busy_timeout) и видит уже добавленные колонки.
        // Повтор на SQLITE_BUSY/LOCKED — страховка на случай, если блокировку не удалось взять.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                InitializeOnce();
                return;
            }
            catch (SqliteException e) when (attempt < 3 && (e.SqliteErrorCode == 5 || e.SqliteErrorCode == 6))
            {
                Thread.Sleep(50 * attempt);
            }
        }
    }

    private void InitializeOnce()
    {
        using var connection = Connect();
        using var transaction = connection.BeginTransaction(deferred: false);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS reminders (
                    id TEXT PRIMARY KEY,
                    text TEXT NOT NULL,
                    due_utc_ms INTEGER NOT NULL,
                    offset_minutes INTEGER NOT NULL,
                    created_utc_ms INTEGER NOT NULL,
                    state TEXT NOT NULL,
                    attempts INTEGER NOT NULL DEFAULT 0,
                    claim_token TEXT NULL,
                    claimed_utc_ms INTEGER NULL,
                    sent_utc_ms INTEGER NULL,
                    last_error TEXT NULL,
                    task_name TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_reminders_due ON reminders(state, due_utc_ms);
                """;
            command.ExecuteNonQuery();
        }

        // Аддитивная миграция: база предыдущей версии (TASK-3) открывается как есть, очередь
        // не требует пустоты, старые данные не переписываются и не удаляются.
        EnsureColumn(connection, transaction, "reminders", "updated_utc_ms", "INTEGER NULL");
        EnsureColumn(connection, transaction, "reminders", "sync_due_ms", "INTEGER NULL");
        transaction.Commit();
    }

    private static void EnsureColumn(SqliteConnection connection, SqliteTransaction transaction, string table, string column, string definition)
    {
        using var check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $name";
        check.Parameters.AddWithValue("$name", column);
        if (Convert.ToInt32(check.ExecuteScalar()) > 0) return;

        using var alter = connection.CreateCommand();
        alter.Transaction = transaction;
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        try
        {
            alter.ExecuteNonQuery();
        }
        catch (SqliteException e) when (e.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
        {
            // Колонку успел добавить другой процесс между проверкой и ALTER — это не ошибка.
        }
    }

    public Reminder Create(string text, DateTimeOffset dueUtc, DateTimeOffset now)
    {
        var reminder = new Reminder
        {
            Id = "rem_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(),
            Text = text,
            DueUtc = dueUtc.ToUniversalTime(),
            Offset = dueUtc.Offset,
            CreatedUtc = now.ToUniversalTime(),
            State = ReminderState.Pending
        };

        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO reminders (id, text, due_utc_ms, offset_minutes, created_utc_ms, state, attempts)
            VALUES ($id, $text, $due, $offset, $created, 'pending', 0);
            """;
        command.Parameters.AddWithValue("$id", reminder.Id);
        command.Parameters.AddWithValue("$text", reminder.Text);
        command.Parameters.AddWithValue("$due", reminder.DueUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$offset", (int)reminder.Offset.TotalMinutes);
        command.Parameters.AddWithValue("$created", reminder.CreatedUtc.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
        return reminder;
    }

    public IReadOnlyList<Reminder> List(bool includeFinished)
    {
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = includeFinished
            ? "SELECT * FROM reminders ORDER BY due_utc_ms ASC LIMIT 500"
            : "SELECT * FROM reminders WHERE state IN ('pending','sending') ORDER BY due_utc_ms ASC LIMIT 500";
        using var reader = command.ExecuteReader();
        var result = new List<Reminder>();
        while (reader.Read()) result.Add(Read(reader));
        return result;
    }

    /// <summary>
    /// Поиск активных записей (<c>pending</c>/<c>sending</c>) по необязательным критериям.
    /// Сортировка устойчивая — (due_utc_ms, id), поэтому курсор не теряет и не дублирует записи
    /// при переходе на следующую страницу. Текстовая подстрока сравнивается в C#:
    /// <c>StringComparison.OrdinalIgnoreCase</c> корректно работает с Unicode, тогда как SQLite
    /// <c>lower()</c> — только с ASCII. Только чтение: ни состояния, ни попытки, ни задачи не меняются.
    /// </summary>
    public ReminderPage Search(ReminderSearchQuery query, int limit, ReminderCursor? after, DateTimeOffset nowUtc)
    {
        var nowMs = nowUtc.ToUniversalTime().ToUnixTimeMilliseconds();
        var items = new List<Reminder>();
        var truncated = false;

        // Страница и total читаются в одной read-транзакции (deferred, без write-lock): обход
        // доставки или отмена между двумя запросами не дадут рассогласованный total/nextCursor.
        using var connection = Connect();
        using var transaction = connection.BeginTransaction(deferred: true);
        try
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT * FROM reminders WHERE "
                    + BuildWhere(query, nowMs, after)
                    + " ORDER BY due_utc_ms ASC, id ASC";
                AddParameters(command, query, nowMs, after);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var reminder = Read(reader);
                    if (!query.MatchesText(reminder.Text)) continue;
                    if (items.Count < limit)
                    {
                        items.Add(reminder);
                        continue;
                    }
                    // Совпадение сверх страницы: выборка усечена, остаток доступен по nextCursor.
                    truncated = true;
                    break;
                }
            }

            var total = CountSearch(connection, transaction, query, nowMs);
            transaction.Commit();

            // total не переопределяет уже установленный truncated: даже при расхождении
            // nextCursor остаётся доступен, и совпадения за страницей не теряются.
            var next = truncated && items.Count > 0
                ? new ReminderCursor(items[^1].DueUtc.ToUnixTimeMilliseconds(), items[^1].Id)
                : null;
            return new ReminderPage(items, total, truncated, next);
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static string BuildWhere(ReminderSearchQuery query, long nowMs, ReminderCursor? after)
    {
        var clauses = new List<string> { "state IN ('pending','sending')" };
        if (query.Id is not null) clauses.Add("id = $id");
        if (query.FromUtc is not null) clauses.Add("due_utc_ms >= $from");
        if (query.ToUtc is not null) clauses.Add("due_utc_ms < $to");
        if (query.Overdue) clauses.Add("due_utc_ms <= $now");
        if (after is not null) clauses.Add("(due_utc_ms > $afterDue OR (due_utc_ms = $afterDue AND id > $afterId))");
        return string.Join(" AND ", clauses);
    }

    private static void AddParameters(SqliteCommand command, ReminderSearchQuery query, long nowMs, ReminderCursor? after)
    {
        if (query.Id is not null) command.Parameters.AddWithValue("$id", query.Id);
        if (query.FromUtc is not null) command.Parameters.AddWithValue("$from", query.FromUtc.Value.ToUnixTimeMilliseconds());
        if (query.ToUtc is not null) command.Parameters.AddWithValue("$to", query.ToUtc.Value.ToUnixTimeMilliseconds());
        if (query.Overdue) command.Parameters.AddWithValue("$now", nowMs);
        if (after is not null)
        {
            command.Parameters.AddWithValue("$afterDue", after.DueMs);
            command.Parameters.AddWithValue("$afterId", after.Id);
        }
    }

    private int CountSearch(SqliteConnection connection, SqliteTransaction transaction, ReminderSearchQuery query, long nowMs)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (query.Text is null)
        {
            command.CommandText = "SELECT COUNT(*) FROM reminders WHERE " + BuildWhere(query, nowMs, null);
            AddParameters(command, query, nowMs, null);
            return Convert.ToInt32(command.ExecuteScalar());
        }

        // Текстовая подстрока сравнивается в C# (Unicode), поэтому считаем потоком без материализации.
        command.CommandText = "SELECT * FROM reminders WHERE " + BuildWhere(query, nowMs, null);
        AddParameters(command, query, nowMs, null);
        using var reader = command.ExecuteReader();
        var count = 0;
        while (reader.Read())
            if (query.MatchesText(Read(reader).Text)) count++;
        return count;
    }

    /// <summary>
    /// Страница записей <c>pending</c> для миграции задач Планировщика: без общего лимита 500 у
    /// <see cref="List"/>, включая просроченные (их точные триггеры могли остаться консольными),
    /// но без <c>sending</c> (такую запись уже доставляет другой обработчик).
    /// Ключи сортировки (due_utc_ms, id) образуют устойчивый keyset-курсор для пагинации.
    /// </summary>
    public IReadOnlyList<Reminder> PagePending(long? afterDueMs, string? afterId, int pageSize)
    {
        using var connection = Connect();
        using var command = connection.CreateCommand();
        var after = afterDueMs is null
            ? string.Empty
            : " AND (due_utc_ms > $afterDue OR (due_utc_ms = $afterDue AND id > $afterId))";
        command.CommandText = "SELECT * FROM reminders WHERE state = 'pending'" + after + " ORDER BY due_utc_ms ASC, id ASC LIMIT $limit";
        if (afterDueMs is not null)
        {
            command.Parameters.AddWithValue("$afterDue", afterDueMs.Value);
            command.Parameters.AddWithValue("$afterId", afterId ?? string.Empty);
        }
        command.Parameters.AddWithValue("$limit", pageSize);
        using var reader = command.ExecuteReader();
        var result = new List<Reminder>();
        while (reader.Read()) result.Add(Read(reader));
        return result;
    }

    /// <summary>Сколько записей <c>pending</c> осталось после курсора (для честного остатка при лимите).</summary>
    public int CountPending(long? afterDueMs, string? afterId)
    {
        using var connection = Connect();
        using var command = connection.CreateCommand();
        var after = afterDueMs is null
            ? string.Empty
            : " AND (due_utc_ms > $afterDue OR (due_utc_ms = $afterDue AND id > $afterId))";
        command.CommandText = "SELECT COUNT(*) FROM reminders WHERE state = 'pending'" + after;
        if (afterDueMs is not null)
        {
            command.Parameters.AddWithValue("$afterDue", afterDueMs.Value);
            command.Parameters.AddWithValue("$afterId", afterId ?? string.Empty);
        }
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>Страница записей <c>pending</c> с неснятым маркером синхронизации, по сроку и id.</summary>
    public IReadOnlyList<Reminder> PagePendingSync(long? afterDueMs, string? afterId, int pageSize)
    {
        using var connection = Connect();
        using var command = connection.CreateCommand();
        var after = afterDueMs is null
            ? string.Empty
            : " AND (due_utc_ms > $afterDue OR (due_utc_ms = $afterDue AND id > $afterId))";
        command.CommandText = "SELECT * FROM reminders WHERE state = 'pending' AND sync_due_ms IS NOT NULL"
            + after + " ORDER BY due_utc_ms ASC, id ASC LIMIT $limit";
        if (afterDueMs is not null)
        {
            command.Parameters.AddWithValue("$afterDue", afterDueMs.Value);
            command.Parameters.AddWithValue("$afterId", afterId ?? string.Empty);
        }
        command.Parameters.AddWithValue("$limit", pageSize);
        using var reader = command.ExecuteReader();
        var result = new List<Reminder>();
        while (reader.Read()) result.Add(Read(reader));
        return result;
    }

    public Reminder? Get(string id)
    {
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM reminders WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    /// <summary>
    /// Атомарная отмена: под BEGIN IMMEDIATE состояние проверяется и меняется в одной транзакции,
    /// поэтому нельзя объявить отмену уже доставленного или доставляемого напоминания.
    /// </summary>
    public (CancelStatus Status, Reminder? Reminder) Cancel(string id)
    {
        using var connection = Connect();
        using var transaction = connection.BeginTransaction(deferred: false);
        var existing = GetIn(connection, transaction, id);
        if (existing is null)
        {
            transaction.Rollback();
            return (CancelStatus.NotFound, null);
        }
        if (existing.State == ReminderState.Sent)
        {
            transaction.Rollback();
            return (CancelStatus.AlreadySent, existing);
        }
        if (existing.State == ReminderState.Sending)
        {
            transaction.Rollback();
            return (CancelStatus.InProgress, existing);
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE reminders SET state = 'cancelled', task_name = NULL, sync_due_ms = NULL, last_error = NULL WHERE id = $id AND state = 'pending'";
        command.Parameters.AddWithValue("$id", id);
        if (command.ExecuteNonQuery() != 1)
        {
            transaction.Rollback();
            return (CancelStatus.InProgress, existing);
        }
        transaction.Commit();
        return (CancelStatus.Cancelled, existing);
    }

    public bool Purge(string id)
    {
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM reminders WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Точечное изменение активной записи. Проверка состояния и запись идут в одной транзакции
    /// <c>BEGIN IMMEDIATE</c>: конкурентный <see cref="ClaimDue"/> или <see cref="Cancel"/> либо
    /// успевает раньше (тогда апдейт честно отклоняется), либо видит уже новые данные. Поэтому
    /// нельзя изменить запись, которую в этот момент доставляют или уже доставили.
    /// </summary>
    /// <param name="text">Новый текст или null, если текст менять не нужно.</param>
    /// <param name="dueUtc">Новый срок или null, если срок менять не нужно.</param>
    /// <returns>
    /// Состояние после вызова и фактические изменения. При смене срока в той же транзакции
    /// выставляется <c>sync_due_ms</c>: маркер требуемой перерегистрации точного триггера.
    /// </returns>
    public ReminderUpdateResult Update(string id, string? text, DateTimeOffset? dueUtc, DateTimeOffset now)
    {
        using var connection = Connect();
        using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            var existing = GetIn(connection, transaction, id);
            if (existing is null)
            {
                transaction.Rollback();
                return new ReminderUpdateResult(UpdateStatus.NotFound, null, false, false);
            }

            // Черновик новых значений: непереданные поля остаются прежними.
            var newText = text ?? existing.Text;
            var newDue = dueUtc?.ToUniversalTime() ?? existing.DueUtc;
            var newOffset = dueUtc is null ? existing.Offset : dueUtc.Value.Offset;
            var dueChanged = newDue != existing.DueUtc;
            var textChanged = !string.Equals(newText, existing.Text, StringComparison.Ordinal);

            if (existing.State != ReminderState.Pending)
            {
                transaction.Rollback();
                return new ReminderUpdateResult(UpdateStatus.NotPending, existing, false, false);
            }

            if (!dueChanged && !textChanged)
            {
                transaction.Rollback();
                return new ReminderUpdateResult(UpdateStatus.NoChange, existing, false, false);
            }

            // Маркер синхронизации: новый срок требует перерегистрации; прежний незавершённый
            // маркер сохраняем, чтобы отложенная перерегистрация не потерялась из-за текстовой правки.
            var syncDueMs = dueChanged
                ? (long?)newDue.ToUnixTimeMilliseconds()
                : existing.SyncDueUtc?.ToUnixTimeMilliseconds();

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE reminders
                   SET text = $text, due_utc_ms = $due, offset_minutes = $offset,
                       updated_utc_ms = $now, sync_due_ms = $sync
                 WHERE id = $id AND state = 'pending'
                """;
            command.Parameters.AddWithValue("$text", newText);
            command.Parameters.AddWithValue("$due", newDue.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$offset", (int)newOffset.TotalMinutes);
            command.Parameters.AddWithValue("$now", now.ToUniversalTime().ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$sync", (object?)syncDueMs ?? DBNull.Value);
            command.Parameters.AddWithValue("$id", id);
            if (command.ExecuteNonQuery() != 1)
            {
                // Состояние успело измениться между чтением и записью — не перезаписываем.
                transaction.Rollback();
                return new ReminderUpdateResult(UpdateStatus.NotPending, Get(id), false, false);
            }

            var updated = GetIn(connection, transaction, id) ?? existing;
            transaction.Commit();
            return new ReminderUpdateResult(UpdateStatus.Updated, updated, textChanged, dueChanged);
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Подтверждает, что триггер для <paramref name="registeredDueMs"/> действительно
    /// зарегистрирован. Маркер снимается условно: только если запись всё ещё pending и её срок и
    /// маркер совпадают с зарегистрированным. Конкурентная правка срока, отмена или claim
    /// оставляют маркер/состояние как есть, поэтому ложного «синхронизировано» не бывает.
    /// </summary>
    public bool CompleteSync(string id, long registeredDueMs, string taskName)
    {
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reminders
               SET sync_due_ms = NULL, task_name = $task, last_error = NULL
             WHERE id = $id AND state = 'pending'
               AND due_utc_ms = $due AND sync_due_ms = $due
            """;
        command.Parameters.AddWithValue("$task", taskName);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$due", registeredDueMs);
        return command.ExecuteNonQuery() == 1;
    }

    public void SetTaskName(string id, string? taskName)
    {
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE reminders SET task_name = $task WHERE id = $id";
        command.Parameters.AddWithValue("$task", (object?)taskName ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void SetLastError(string id, string? error)
    {
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE reminders SET last_error = $error WHERE id = $id";
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Записывает ошибку регистрации только если срок записи всё ещё тот, для которого
    /// регистрировали. Поздний отказ старого срока не затирает более новую успешную правку.
    /// </summary>
    public bool SetLastErrorIfDue(string id, long dueMs, string? error)
    {
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reminders SET last_error = $error
             WHERE id = $id AND due_utc_ms = $due AND state = 'pending'
            """;
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$due", dueMs);
        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>
    /// Возвращает «зависшие» отправки в pending. Нужен после падения процесса между claim и результатом отправки.
    /// Возможна повторная доставка — см. README, раздел о at-least-once.
    /// </summary>
    public int RecoverStaleClaims(DateTimeOffset now, TimeSpan staleAfter)
    {
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reminders
               SET state = 'pending', claim_token = NULL, claimed_utc_ms = NULL,
                   last_error = COALESCE(last_error || '; ', '') || 'прерванная отправка возвращена в очередь'
             WHERE state = 'sending' AND claimed_utc_ms IS NOT NULL AND claimed_utc_ms < $cutoff
            """;
        command.Parameters.AddWithValue("$cutoff", now.Subtract(staleAfter).ToUnixTimeMilliseconds());
        return command.ExecuteNonQuery();
    }

    /// <summary>Атомарно забирает просроченные записи. Возвращает только те, на которые получен claim.</summary>
    public IReadOnlyList<Reminder> ClaimDue(DateTimeOffset now, int limit = 50)
    {
        var claimed = new List<Reminder>();
        using var connection = Connect();
        using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            var ids = new List<string>();
            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = "SELECT id FROM reminders WHERE state = 'pending' AND due_utc_ms <= $now ORDER BY due_utc_ms ASC LIMIT $limit";
                select.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
                select.Parameters.AddWithValue("$limit", limit);
                using var reader = select.ExecuteReader();
                while (reader.Read()) ids.Add(reader.GetString(0));
            }

            foreach (var id in ids)
            {
                var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE reminders
                       SET state = 'sending', claim_token = $token, claimed_utc_ms = $now, attempts = attempts + 1
                     WHERE id = $id AND state = 'pending'
                    """;
                update.Parameters.AddWithValue("$token", token);
                update.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
                update.Parameters.AddWithValue("$id", id);
                if (update.ExecuteNonQuery() == 1)
                {
                    var reminder = GetIn(connection, transaction, id);
                    if (reminder is not null) claimed.Add(reminder);
                }
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
        return claimed;
    }

    public bool MarkSent(string id, string claimToken, DateTimeOffset now)
    {
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reminders SET state = 'sent', sent_utc_ms = $now, last_error = NULL, claim_token = NULL
             WHERE id = $id AND state = 'sending' AND claim_token = $token
            """;
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$token", claimToken);
        return command.ExecuteNonQuery() == 1;
    }

    public bool MarkFailed(string id, string claimToken, string error)
    {
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reminders SET state = 'pending', claim_token = NULL, claimed_utc_ms = NULL, last_error = $error
             WHERE id = $id AND state = 'sending' AND claim_token = $token
            """;
        command.Parameters.AddWithValue("$error", error);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$token", claimToken);
        return command.ExecuteNonQuery() == 1;
    }

    public (int Pending, int Sending, int Sent, int Cancelled) Counts()
    {
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT state, COUNT(*) FROM reminders GROUP BY state";
        using var reader = command.ExecuteReader();
        int pending = 0, sending = 0, sent = 0, cancelled = 0;
        while (reader.Read())
        {
            var count = reader.GetInt32(1);
            switch (reader.GetString(0))
            {
                case "pending": pending = count; break;
                case "sending": sending = count; break;
                case "sent": sent = count; break;
                case "cancelled": cancelled = count; break;
            }
        }
        return (pending, sending, sent, cancelled);
    }

    private static Reminder? GetIn(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM reminders WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private static Reminder Read(SqliteDataReader reader)
    {
        var reminder = new Reminder
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            Text = reader.GetString(reader.GetOrdinal("text")),
            DueUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal("due_utc_ms"))),
            Offset = TimeSpan.FromMinutes(reader.GetInt32(reader.GetOrdinal("offset_minutes"))),
            CreatedUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal("created_utc_ms"))),
            State = ReminderStateNames.FromDb(reader.GetString(reader.GetOrdinal("state"))),
            Attempts = reader.GetInt32(reader.GetOrdinal("attempts"))
        };
        reminder.ClaimToken = reader.IsDBNull(reader.GetOrdinal("claim_token")) ? null : reader.GetString(reader.GetOrdinal("claim_token"));
        if (!reader.IsDBNull(reader.GetOrdinal("claimed_utc_ms")))
            reminder.ClaimedUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal("claimed_utc_ms")));
        if (!reader.IsDBNull(reader.GetOrdinal("sent_utc_ms")))
            reminder.SentUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal("sent_utc_ms")));
        reminder.LastError = reader.IsDBNull(reader.GetOrdinal("last_error")) ? null : reader.GetString(reader.GetOrdinal("last_error"));
        reminder.TaskName = reader.IsDBNull(reader.GetOrdinal("task_name")) ? null : reader.GetString(reader.GetOrdinal("task_name"));
        if (!reader.IsDBNull(reader.GetOrdinal("updated_utc_ms")))
            reminder.UpdatedUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal("updated_utc_ms")));
        if (!reader.IsDBNull(reader.GetOrdinal("sync_due_ms")))
            reminder.SyncDueUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal("sync_due_ms")));
        return reminder;
    }
}
