using AgentNotify.Core;
using AgentNotify.Windows;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentNotify.Tests;

public class ReminderUpdateTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Due1Utc = new(2026, 11, 27, 6, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Due2Utc = new(2026, 11, 27, 8, 45, 0, TimeSpan.Zero);
    private const string Due1 = "2026-11-27T09:30:00+03:00";
    private const string Due2 = "2026-11-27T11:45:00+03:00";

    private static ReminderService Service(TempDb db, FakeRegistrar registrar, FakePresenter presenter) =>
        new(db.Store, registrar, presenter, () => Now, exePath: @"C:\fake\AgentNotify.exe");

    private static (ReminderService Service, FakeRegistrar Registrar, string Id) Scheduled(TempDb db, string text = "исходный текст")
    {
        var registrar = new FakeRegistrar();
        var service = Service(db, registrar, new FakePresenter());
        var outcome = service.Schedule(Due1, text);
        return (service, registrar, outcome.Reminder.Id);
    }

    [Fact]
    public void Update_TextOnly_KeepsDueAndDoesNotReRegisterTrigger()
    {
        using var db = new TempDb();
        var (service, registrar, id) = Scheduled(db);
        var before = db.Store.Get(id)!;

        var result = service.Update(id, null, "новый текст <&>");

        Assert.Equal(UpdateStatus.Updated, result.Status);
        Assert.True(result.Changed);
        Assert.True(result.TextChanged);
        Assert.False(result.DueChanged);
        Assert.True(result.WindowsTaskRegistered);
        Assert.False(result.SyncPending);
        Assert.Single(registrar.Registered);

        var after = db.Store.Get(id)!;
        Assert.Equal(before.DueUtc, after.DueUtc);
        Assert.Equal(before.Offset, after.Offset);
        Assert.Equal("новый текст <&>", after.Text);
        Assert.Null(after.SyncDueUtc);
        Assert.Equal(before.Attempts, after.Attempts);
    }

    [Fact]
    public void Update_DueOnly_ReRegistersSameTaskAndPreservesEverythingElse()
    {
        using var db = new TempDb();
        var (service, registrar, id) = Scheduled(db);
        var before = db.Store.Get(id)!;

        var result = service.Update(id, Due2, null);

        Assert.Equal(UpdateStatus.Updated, result.Status);
        Assert.True(result.DueChanged);
        Assert.False(result.TextChanged);
        Assert.True(result.WindowsTaskRegistered);
        Assert.False(result.SyncPending);
        Assert.Equal(2, registrar.Registered.Count);
        Assert.All(registrar.Registered, name => Assert.Equal(TaskNames.Reminder(id), name));
        Assert.Equal(Due2Utc, registrar.Details[^1].DueLocal);

        var after = db.Store.Get(id)!;
        Assert.Equal(id, after.Id);
        Assert.Equal(Due2Utc, after.DueUtc);
        Assert.Equal(TimeSpan.FromHours(3), after.Offset);
        Assert.Equal("исходный текст", after.Text);
        Assert.Equal(before.CreatedUtc, after.CreatedUtc);
        Assert.Null(after.SyncDueUtc);
        Assert.Equal(TaskNames.Reminder(id), after.TaskName);
    }

    [Fact]
    public void Update_TextAndDueTogether()
    {
        using var db = new TempDb();
        var (service, registrar, id) = Scheduled(db);

        var result = service.Update(id, Due2, "оба поля");

        Assert.True(result.TextChanged);
        Assert.True(result.DueChanged);
        Assert.True(result.WindowsTaskRegistered);
        var after = db.Store.Get(id)!;
        Assert.Equal("оба поля", after.Text);
        Assert.Equal(Due2Utc, after.DueUtc);
        Assert.Single(registrar.Registered.Distinct());
    }

    [Fact]
    public void Update_NoChange_IsIdempotentAndTouchesNothing()
    {
        using var db = new TempDb();
        var (service, registrar, id) = Scheduled(db);
        var before = db.Store.Get(id)!;

        var result = service.Update(id, Due1, "исходный текст");

        Assert.Equal(UpdateStatus.NoChange, result.Status);
        Assert.False(result.Changed);
        Assert.False(result.SyncPending);
        Assert.Single(registrar.Registered);
        var after = db.Store.Get(id)!;
        Assert.Null(after.UpdatedUtc);
        Assert.Equal(before.Text, after.Text);
        Assert.Equal(before.DueUtc, after.DueUtc);
    }

    [Fact]
    public void Update_RejectsBadInputWithoutChangingData()
    {
        using var db = new TempDb();
        var (service, _, id) = Scheduled(db);
        var before = db.Store.Get(id)!;

        Assert.Throws<ReminderValidationException>(() => service.Update(id, null, null));
        Assert.Throws<ReminderValidationException>(() => service.Update(id, "2026-11-27T09:30:00", null));
        Assert.Throws<ReminderValidationException>(() => service.Update(id, "2020-01-01T00:00:00+00:00", null));
        Assert.Throws<ReminderValidationException>(() => service.Update(id, null, "   "));
        Assert.Throws<ReminderValidationException>(() => service.Update(id, null, "плохая \ud800 пара"));
        Assert.Throws<ReminderValidationException>(() => service.Update("не-id", null, "текст"));

        var missing = service.Update("rem_0000000000000000", null, "текст");
        Assert.False(missing.Found);
        Assert.Equal(UpdateStatus.NotFound, missing.Status);

        var after = db.Store.Get(id)!;
        Assert.Equal(before.Text, after.Text);
        Assert.Equal(before.DueUtc, after.DueUtc);
        Assert.Null(after.SyncDueUtc);
    }

    [Fact]
    public void Update_RefusesSendingSentAndCancelled()
    {
        using var db = new TempDb();
        var registrar = new FakeRegistrar();
        var service = Service(db, registrar, new FakePresenter());

        // sending: запись забрана на доставку.
        var sending = db.Store.Create("доставляется", Now.AddMinutes(-1), Now.AddHours(-1));
        var claimed = db.Store.ClaimDue(Now);
        Assert.Single(claimed);
        var sendingResult = service.Update(sending.Id, Due2, "нельзя");
        Assert.Equal(UpdateStatus.NotPending, sendingResult.Status);
        Assert.False(sendingResult.Changed);
        Assert.Contains("начата", string.Join(" ", sendingResult.Warnings));

        // sent: уже доставлена.
        var sent = db.Store.Create("доставлено", Now.AddMinutes(-1), Now.AddHours(-1));
        var sentClaim = db.Store.ClaimDue(Now).Single();
        Assert.True(db.Store.MarkSent(sent.Id, sentClaim.ClaimToken!, Now));
        var sentResult = service.Update(sent.Id, null, "нельзя");
        Assert.Equal(UpdateStatus.NotPending, sentResult.Status);
        Assert.Contains("доставлено", string.Join(" ", sentResult.Warnings));

        // cancelled.
        var (scheduledService, _, id) = Scheduled(db);
        scheduledService.Cancel(id);
        var cancelledResult = scheduledService.Update(id, Due2, "нельзя");
        Assert.Equal(UpdateStatus.NotPending, cancelledResult.Status);
        Assert.Contains("отменено", string.Join(" ", cancelledResult.Warnings));

        Assert.Equal("доставляется", db.Store.Get(sending.Id)!.Text);
        Assert.Equal("доставлено", db.Store.Get(sent.Id)!.Text);
        Assert.Equal("исходный текст", db.Store.Get(id)!.Text);
    }

    [Fact]
    public void Update_RegistrationFailureKeepsChangeAndMarkerThenRepairCompletes()
    {
        using var db = new TempDb();
        var (service, registrar, id) = Scheduled(db);
        registrar.FailRegistration = true;

        var result = service.Update(id, Due2, null);

        Assert.True(result.Changed);
        Assert.False(result.WindowsTaskRegistered);
        Assert.True(result.SyncPending);
        Assert.Contains("не подтверждён", string.Join(" ", result.Warnings));
        var stored = db.Store.Get(id)!;
        Assert.Equal(Due2Utc, stored.DueUtc);
        Assert.Equal(Due2Utc, stored.SyncDueUtc);
        Assert.Contains("регистратор", stored.LastError);

        // Повтор обхода: заново регистрирует тот же срок под прежним именем, даже если старое
        // задание ещё существует.
        registrar.FailRegistration = false;
        Assert.True(registrar.Exists(TaskNames.Reminder(id)));
        var repaired = service.RepairTasks();

        Assert.Equal(1, repaired);
        Assert.Equal(2, registrar.Registered.Count);
        Assert.Equal(Due2Utc, registrar.Details[^1].DueLocal);
        var after = db.Store.Get(id)!;
        Assert.Null(after.SyncDueUtc);
        Assert.Null(after.LastError);
    }

    [Fact]
    public void Update_TextChangeDoesNotRegisterAndLeavesDirtyForSweep()
    {
        using var db = new TempDb();
        var (service, registrar, id) = Scheduled(db);
        registrar.FailRegistration = true;
        var failed = service.Update(id, Due2, null);
        Assert.True(failed.SyncPending);
        var callsAfterFailure = registrar.RegisterCalls;

        registrar.FailRegistration = false;
        var result = service.Update(id, null, "правка текста");

        Assert.True(result.Changed);
        Assert.True(result.TextChanged);
        Assert.False(result.DueChanged);
        Assert.False(result.WindowsTaskRegistered); // срок не подтверждён
        Assert.True(result.SyncPending);
        Assert.Equal(callsAfterFailure, registrar.RegisterCalls); // побочной регистрации нет

        // «Грязный» маркер лечит обход.
        Assert.Equal(1, service.RepairTasks());
        Assert.Equal(Due2Utc, registrar.Details[^1].DueLocal);
        Assert.Null(db.Store.Get(id)!.SyncDueUtc);
    }

    [Fact]
    public void Update_DueChangeCannotBeDeliveredEarlyByOldSchedule()
    {
        using var db = new TempDb();
        var (service, _, id) = Scheduled(db);

        var result = service.Update(id, "2026-11-26T10:00:00+00:00", null);

        Assert.True(result.DueChanged);
        Assert.Empty(db.Store.ClaimDue(Now));
        Assert.Equal(id, db.Store.ClaimDue(new DateTimeOffset(2026, 11, 26, 10, 0, 1, TimeSpan.Zero)).Single().Id);
    }

    [Fact]
    public void Update_CancelDuringRegistrationDoesNotOverwriteAndRemovesOrphanTask()
    {
        using var db = new TempDb();
        var (service, registrar, id) = Scheduled(db);
        registrar.AfterRegister = _ => service.Cancel(id);

        var result = service.Update(id, Due2, null);

        Assert.True(result.Changed);
        Assert.False(result.SyncPending);
        Assert.False(result.WindowsTaskRegistered);
        Assert.Contains("перестала быть активной", string.Join(" ", result.Warnings));
        Assert.False(registrar.Exists(TaskNames.Reminder(id)));
        Assert.Equal(ReminderState.Cancelled, db.Store.Get(id)!.State);
    }

    [Fact]
    public void CompleteSync_RequiresMatchingVersionAndPending()
    {
        using var db = new TempDb();
        var (_, _, id) = Scheduled(db);
        var store = db.Store;

        var update = store.Update(id, null, Due2Utc, Now);
        Assert.Equal(UpdateStatus.Updated, update.Status);
        Assert.Equal(Due2Utc, store.Get(id)!.SyncDueUtc);

        Assert.False(store.CompleteSync(id, Due1Utc.ToUnixTimeMilliseconds(), TaskNames.Reminder(id)));
        Assert.Equal(Due2Utc, store.Get(id)!.SyncDueUtc);

        Assert.True(store.CompleteSync(id, Due2Utc.ToUnixTimeMilliseconds(), TaskNames.Reminder(id)));
        Assert.Null(store.Get(id)!.SyncDueUtc);
        Assert.Equal(TaskNames.Reminder(id), store.Get(id)!.TaskName);
    }

    [Fact]
    public void Update_PreservesUnicodeText()
    {
        using var db = new TempDb();
        var (service, _, id) = Scheduled(db);
        const string text = "напомнить про 🧊 лёд, <тег> & \"кавычки\"\nвторая строка";

        var result = service.Update(id, null, text);

        Assert.True(result.Changed);
        Assert.Equal(text, db.Store.Get(id)!.Text);
        Assert.Equal(text, result.Reminder!.Text);
    }

    [Fact]
    public void Update_ConcurrentDueEditKeepsNewerMarkerForRetry()
    {
        using var db = new TempDb();
        var (service, registrar, id) = Scheduled(db);
        var due3 = Due2Utc.AddHours(1);
        // Пока Планировщик регистрирует срок A, пользователь переносит запись на срок B.
        registrar.AfterRegister = _ => db.Store.Update(id, null, due3, Now);

        var result = service.Update(id, Due2, null);

        Assert.True(result.Changed);
        Assert.True(result.SyncPending);
        Assert.Contains("изменился до завершения", string.Join(" ", result.Warnings));
        var stored = db.Store.Get(id)!;
        Assert.Equal(due3, stored.DueUtc);
        Assert.Equal(due3, stored.SyncDueUtc);

        // Следующий проход перерегистрирует уже актуальный срок.
        registrar.AfterRegister = null;
        Assert.Equal(1, service.RepairTasks());
        Assert.Equal(due3, registrar.Details[^1].DueLocal);
        Assert.Null(db.Store.Get(id)!.SyncDueUtc);
    }

    /// <summary>
    /// Детерминированный A/B: регистрация старого срока A ещё не завершилась, а в базу уже записан
    /// новый срок B с маркером. Позднее завершение A не должно ни объявить успех, ни стереть
    /// маркер B; обход затем регистрирует B.
    /// </summary>
    [Fact]
    public async Task Update_LateOldDueRegistrationCannotEraseNewerDirtyMarker()
    {
        using var db = new TempDb();
        var (service, registrar, id) = Scheduled(db);
        var due3 = Due2Utc.AddHours(1);
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        registrar.BeforeRegister = _ =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        };

        var stale = Task.Run(() => service.Update(id, Due2, null));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));

        // «Другой процесс» записывает новый срок B, пока A всё ещё регистрируется.
        var external = db.Store.Update(id, null, due3, Now);
        Assert.Equal(UpdateStatus.Updated, external.Status);
        Assert.Equal(due3, db.Store.Get(id)!.SyncDueUtc);

        registrar.BeforeRegister = null;
        release.Set();
        var outcome = await stale;

        Assert.True(outcome.Changed);
        Assert.False(outcome.WindowsTaskRegistered);
        Assert.True(outcome.SyncPending);
        var stored = db.Store.Get(id)!;
        Assert.Equal(due3, stored.DueUtc);
        Assert.Equal(due3, stored.SyncDueUtc); // маркер нового срока не затёрт
        Assert.Equal(Due2Utc, registrar.Details[^1].DueLocal); // последняя регистрация — старый A

        // Обход регистрирует уже новый срок, повторной «старой» регистрации нет.
        Assert.Equal(1, service.RepairTasks());
        Assert.Equal(due3, registrar.Details[^1].DueLocal);
        Assert.Null(db.Store.Get(id)!.SyncDueUtc);
        Assert.All(registrar.Registered, name => Assert.Equal(TaskNames.Reminder(id), name));
    }

    /// <summary>
    /// Две конкурирующие правки одной записи не должны регистрировать задачу одновременно; итоговое
    /// задание обязано совпадать с очередью, а маркер — быть снят.
    /// </summary>
    [Fact]
    public async Task ConcurrentUpdates_SerializeTaskRegistrationPerReminder()
    {
        using var db = new TempDb();
        var registrar = new FakeRegistrar();
        var serviceA = Service(db, registrar, new FakePresenter());
        var serviceB = Service(db, registrar, new FakePresenter());
        var id = serviceA.Schedule(Due1, "сериализация").Reminder.Id;
        var baselineCalls = registrar.RegisterCalls;
        var due3 = Due2Utc.AddHours(2);

        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var calls = 0;
        registrar.BeforeRegister = _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        };

        var first = Task.Run(() => serviceA.Update(id, Due2, null));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        var second = Task.Run(() => serviceB.Update(id, due3.ToString("yyyy-MM-ddTHH:mm:sszzz"), null));
        Thread.Sleep(300);
        Assert.Equal(baselineCalls + 1, registrar.RegisterCalls); // вторая регистрация ждёт guard

        release.Set();
        await Task.WhenAll(first, second);

        Assert.Equal(1, registrar.MaxConcurrentRegister);
        var stored = db.Store.Get(id)!;
        Assert.Null(stored.SyncDueUtc);
        Assert.Equal(stored.DueUtc, registrar.Details[^1].DueLocal);
    }

    [Fact]
    public void Repair_SkipsRecordCancelledAfterFailedSync()
    {
        using var db = new TempDb();
        var (service, registrar, id) = Scheduled(db);
        registrar.FailRegistration = true;
        Assert.True(service.Update(id, Due2, null).SyncPending);
        Assert.Equal(CancelStatus.Cancelled, service.Cancel(id).Status);
        var calls = registrar.RegisterCalls;
        registrar.FailRegistration = false;

        Assert.Equal(0, service.RepairTasks());
        Assert.Equal(calls, registrar.RegisterCalls);
        Assert.Equal(ReminderState.Cancelled, db.Store.Get(id)!.State);
    }

    [Fact]
    public void Update_ExplicitEmptyAtIsRejectedEvenWithText()
    {
        using var db = new TempDb();
        var (service, _, id) = Scheduled(db);
        var before = db.Store.Get(id)!;

        Assert.Throws<ReminderValidationException>(() => service.Update(id, "", "новый текст"));
        Assert.Throws<ReminderValidationException>(() => service.Update(id, "   ", "новый текст"));
        Assert.Throws<ReminderValidationException>(() => service.Update(id, null, ""));

        var after = db.Store.Get(id)!;
        Assert.Equal(before.Text, after.Text);
        Assert.Equal(before.DueUtc, after.DueUtc);
    }

    [Fact]
    public void SetLastErrorIfDue_DoesNotClobberNewerEdit()
    {
        using var db = new TempDb();
        var (_, _, id) = Scheduled(db);
        var store = db.Store;
        var due3 = Due2Utc.AddHours(1);

        Assert.Equal(UpdateStatus.Updated, store.Update(id, null, Due2Utc, Now).Status);
        Assert.True(store.SetLastErrorIfDue(id, Due2Utc.ToUnixTimeMilliseconds(), "ошибка срока A"));
        Assert.Equal("ошибка срока A", store.Get(id)!.LastError);

        Assert.Equal(UpdateStatus.Updated, store.Update(id, null, due3, Now).Status);
        Assert.False(store.SetLastErrorIfDue(id, Due2Utc.ToUnixTimeMilliseconds(), "поздняя ошибка срока A"));
        Assert.Equal("ошибка срока A", store.Get(id)!.LastError);

        Assert.True(store.CompleteSync(id, due3.ToUnixTimeMilliseconds(), TaskNames.Reminder(id)));
        Assert.Null(store.Get(id)!.LastError);
    }

    /// <summary>
    /// Несколько процессов открывают базу версии TASK-3 одновременно: аддитивная миграция не должна
    /// упасть на duplicate column и должна оставить ровно две новые колонки.
    /// </summary>
    [Fact]
    public void Initialize_ConcurrentOpenOfLegacyDatabaseAddsColumnsOnce()
    {
        var directory = NewTempDirectory();
        try
        {
            var path = Path.Combine(directory, "reminders.db");
            CreateLegacyDatabase(path);
            var stores = Enumerable.Range(0, 4).Select(_ => new ReminderStore(path)).ToArray();
            var barrier = new Barrier(stores.Length);
            var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
            var threads = stores.Select(store => new Thread(() =>
            {
                try
                {
                    barrier.SignalAndWait();
                    store.Initialize();
                }
                catch (Exception e)
                {
                    errors.Add(e);
                }
            })).ToArray();
            foreach (var thread in threads) thread.Start();
            foreach (var thread in threads) Assert.True(thread.Join(TimeSpan.FromSeconds(30)));

            Assert.Empty(errors);
            foreach (var store in stores)
            {
                var reminder = store.Get("rem_0123456789abcdef");
                Assert.NotNull(reminder);
                Assert.Equal("старый текст", reminder!.Text);
            }

            using var check = new SqliteConnection("Data Source=" + path + ";Pooling=False");
            check.Open();
            using var pragma = check.CreateCommand();
            pragma.CommandText = "SELECT COUNT(*) FROM pragma_table_info('reminders') WHERE name IN ('updated_utc_ms','sync_due_ms')";
            Assert.Equal(2, Convert.ToInt32(pragma.ExecuteScalar()));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); } catch { /* тестовый каталог */ }
        }
    }

    private static string NewTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agentnotify-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>Создаёт изолированную базу ровно в схеме версии TASK-3 (без новых колонок).</summary>
    private static void CreateLegacyDatabase(string path)
    {
        using var connection = new SqliteConnection("Data Source=" + path + ";Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE reminders (
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
            CREATE INDEX ix_reminders_due ON reminders(state, due_utc_ms);
            INSERT INTO reminders (id, text, due_utc_ms, offset_minutes, created_utc_ms, state, attempts)
            VALUES ('rem_0123456789abcdef', 'старый текст', 1795761000000, 180, 1795750000000, 'pending', 0);
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Обратная совместимость: база, созданная версией TASK-3 (без updated_utc_ms/sync_due_ms),
    /// открывается новым бинарником, очередь не требуется пустой, старые строки читаются как есть,
    /// а изменение срока работает уже с новой схемой.
    /// </summary>
    [Fact]
    public void LegacyTask3Schema_OpensAndUpdatesWithoutDataLoss()
    {
        var directory = NewTempDirectory();
        try
        {
            var path = Path.Combine(directory, "reminders.db");
            const string id = "rem_0123456789abcdef";
            CreateLegacyDatabase(path);

            var store = new ReminderStore(path);
            store.Initialize();

            var before = store.Get(id)!;
            Assert.Equal("старый текст", before.Text);
            Assert.Equal(TimeSpan.FromHours(3), before.Offset);
            Assert.Null(before.SyncDueUtc);
            Assert.Single(store.List(includeFinished: false));

            var result = store.Update(id, "новый текст", Due2Utc, Now);
            Assert.Equal(UpdateStatus.Updated, result.Status);

            var after = store.Get(id)!;
            Assert.Equal("новый текст", after.Text);
            Assert.Equal(Due2Utc, after.DueUtc);
            Assert.Equal(Due2Utc, after.SyncDueUtc);
            Assert.NotNull(after.UpdatedUtc);

            using var check = new SqliteConnection("Data Source=" + path + ";Pooling=False");
            check.Open();
            using var pragma = check.CreateCommand();
            pragma.CommandText = "SELECT COUNT(*) FROM pragma_table_info('reminders') WHERE name IN ('updated_utc_ms','sync_due_ms')";
            Assert.Equal(2, Convert.ToInt32(pragma.ExecuteScalar()));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); } catch { /* тестовый каталог */ }
        }
    }
}
