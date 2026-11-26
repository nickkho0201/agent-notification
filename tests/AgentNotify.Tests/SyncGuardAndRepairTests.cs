using AgentNotify.Core;
using AgentNotify.Windows;
using Xunit;

namespace AgentNotify.Tests;

/// <summary>
/// Guard задач и фондовое восстановление: fail-closed без межпроцессной блокировки, приоритет
/// «грязных» записей за пределами первых 500 и отсутствие блокирующего ожидания в обходе.
/// </summary>
public class SyncGuardAndRepairTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Due1Utc = new(2026, 11, 27, 6, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Due2Utc = new(2026, 11, 27, 8, 45, 0, TimeSpan.Zero);
    private const string Due1 = "2026-11-27T09:30:00+03:00";
    private const string Due2 = "2026-11-27T11:45:00+03:00";

    private static ReminderService Service(TempDb db, FakeRegistrar registrar, ITaskSyncCoordinator sync) =>
        new(db.Store, registrar, new FakePresenter(), () => Now, @"C:\fake\AgentNotify.exe", launcherPath: null, sync: sync);

    private sealed class StubSync : ITaskSyncCoordinator
    {
        public TaskSyncStatus Status { get; set; } = TaskSyncStatus.Busy;
        public int Calls { get; private set; }

        public TaskSyncResult Acquire(string taskName, TimeSpan timeout)
        {
            Calls++;
            return new TaskSyncResult(Status, null);
        }
    }

    [Fact]
    public void Update_UnavailableCrossProcessLock_DefersRegistrationAndKeepsDirty()
    {
        using var db = new TempDb();
        var registrar = new FakeRegistrar();
        // Изолированный seam: у этой координации «именованный Mutex недоступен», глобальная среда не меняется.
        var service = Service(db, registrar, new NamedMutexTaskSync(_ => null));
        var id = service.Schedule(Due1, "текст").Reminder.Id;
        var calls = registrar.RegisterCalls;

        var result = service.Update(id, Due2, null);

        Assert.True(result.Changed);
        Assert.True(result.SyncPending);
        Assert.False(result.WindowsTaskRegistered);
        Assert.Contains("недоступна", string.Join(" ", result.Warnings));
        Assert.Equal(calls, registrar.RegisterCalls);
        var stored = db.Store.Get(id)!;
        Assert.Equal(Due2Utc, stored.DueUtc);
        Assert.Equal(Due2Utc, stored.SyncDueUtc);

        // Фоновая попытка тоже fail-closed: задачи не трогаются, маркер сохраняется.
        Assert.Equal(0, service.RepairTasks());
        Assert.Equal(calls, registrar.RegisterCalls);
        Assert.Equal(Due2Utc, db.Store.Get(id)!.SyncDueUtc);
    }

    [Fact]
    public void Repair_BusyGuardSkipsImmediatelyAndKeepsDirty()
    {
        using var db = new TempDb();
        var registrar = new FakeRegistrar();
        var sync = new StubSync { Status = TaskSyncStatus.Busy };
        var service = Service(db, registrar, sync);
        var id = service.Schedule(Due1, "текст").Reminder.Id;
        registrar.FailRegistration = true;
        Assert.True(service.Update(id, Due2, null).SyncPending);
        registrar.FailRegistration = false;
        var calls = registrar.RegisterCalls;

        var started = DateTimeOffset.UtcNow;
        var repaired = service.RepairTasks();
        var elapsed = DateTimeOffset.UtcNow - started;

        Assert.Equal(0, repaired);
        Assert.True(elapsed < TimeSpan.FromSeconds(5), "обход не должен ждать занятый guard");
        Assert.Equal(calls, registrar.RegisterCalls);
        Assert.Equal(Due2Utc, db.Store.Get(id)!.SyncDueUtc);
    }

    /// <summary>
    /// «Грязная» запись стоит по сроку после 500 обычных: старый обход по List(500) её никогда бы не
    /// увидел. Приоритетная очередь по sync_due_ms должна синхронизировать её первой.
    /// </summary>
    [Fact]
    public void Repair_SyncsDirtyRecordBeyondFirst500OrdinaryRecords()
    {
        using var db = new TempDb();
        BulkReminders.InsertPending(db.Store, 600, Now.AddMinutes(1), TimeSpan.FromMinutes(1));
        var dirty = db.Store.Create("грязная", Now.AddMinutes(700), Now);
        var target = Now.AddMinutes(1000);
        Assert.Equal(UpdateStatus.Updated, db.Store.Update(dirty.Id, null, target, Now).Status);

        // Предпосылка теста: обычный List (LIMIT 500) до «грязной» записи не доходит.
        Assert.DoesNotContain(db.Store.List(includeFinished: false), r => r.Id == dirty.Id);

        var registrar = new FakeRegistrar();
        var service = Service(db, registrar, NamedMutexTaskSync.Instance);

        var repaired = service.RepairTasks();

        Assert.True(repaired >= 1);
        Assert.Contains(registrar.Details, d => d.TaskName == TaskNames.Reminder(dirty.Id) && d.DueLocal == target);
        var stored = db.Store.Get(dirty.Id)!;
        Assert.Null(stored.SyncDueUtc);
        Assert.Equal(target, stored.DueUtc);
    }

    [Fact]
    public void Repair_CancelDuringRegistrationRemovesTaskAndLeavesNoMetadata()
    {
        using var db = new TempDb();
        var registrar = new FakeRegistrar();
        var service = Service(db, registrar, NamedMutexTaskSync.Instance);
        var id = service.Schedule(Due1, "текст").Reminder.Id;
        registrar.FailRegistration = true;
        Assert.True(service.Update(id, Due2, null).SyncPending);
        registrar.FailRegistration = false;
        var calls = registrar.RegisterCalls;
        registrar.AfterRegister = _ => service.Cancel(id);

        var repaired = service.RepairTasks();

        Assert.Equal(0, repaired);
        Assert.True(registrar.RegisterCalls > calls);
        Assert.False(registrar.Exists(TaskNames.Reminder(id)));
        var stored = db.Store.Get(id)!;
        Assert.Equal(ReminderState.Cancelled, stored.State);
        Assert.Null(stored.TaskName);
        Assert.Null(stored.SyncDueUtc);
        Assert.Null(stored.LastError);
    }
}
