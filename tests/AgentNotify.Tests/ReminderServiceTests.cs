using AgentNotify.Core;
using AgentNotify.Windows;
using Xunit;

namespace AgentNotify.Tests;

public class ReminderServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);

    private static ReminderService Service(TempDb db, FakeRegistrar registrar, FakePresenter presenter, Func<DateTimeOffset>? clock = null) =>
        new(db.Store, registrar, presenter, clock ?? (() => Now), exePath: @"C:\fake\AgentNotify.exe");

    [Fact]
    public void Schedule_RegistersExactTriggerAndStoresData()
    {
        using var db = new TempDb();
        var registrar = new FakeRegistrar();
        var presenter = new FakePresenter();
        var service = Service(db, registrar, presenter);

        var outcome = service.Schedule("2026-11-27T09:30:00+03:00", "Позвонить <в> & \"сервис\"\nстрока 2");

        Assert.True(outcome.WindowsTaskRegistered);
        Assert.Equal(TaskNames.Reminder(outcome.Reminder.Id), outcome.Reminder.TaskName);
        Assert.Equal(new DateTimeOffset(2026, 11, 27, 6, 30, 0, TimeSpan.Zero), outcome.Reminder.DueUtc);
        Assert.Equal(TimeSpan.FromHours(3), outcome.Reminder.Offset);
        Assert.Contains("run-due", registrar.Details.Single().Arguments);
        Assert.Contains("\nстрока 2", db.Store.Get(outcome.Reminder.Id)!.Text);
    }

    [Fact]
    public void Schedule_RegistrationFailureKeepsRecord()
    {
        using var db = new TempDb();
        var registrar = new FakeRegistrar { FailRegistration = true };
        var service = Service(db, registrar, new FakePresenter());

        var outcome = service.Schedule("2026-11-27T09:30:00+03:00", "текст");

        Assert.False(outcome.WindowsTaskRegistered);
        Assert.False(outcome.SweepTaskPresent);
        Assert.Contains("НЕ найдена", string.Join(" ", outcome.Warnings));
        var stored = db.Store.Get(outcome.Reminder.Id)!;
        Assert.Equal(ReminderState.Pending, stored.State);
        Assert.Contains("регистратор", stored.LastError);
        Assert.Null(stored.TaskName);
    }

    [Fact]
    public void Schedule_RegistrationFailureWithSweepReportsItsPresenceConservatively()
    {
        using var db = new TempDb();
        var registrar = new FakeRegistrar();
        registrar.RegisterSweep(TaskNames.Sweep, @"C:\fake\AgentNotify.exe", "run-due", Now);
        registrar.FailRegistration = true;
        var service = Service(db, registrar, new FakePresenter());

        var outcome = service.Schedule("2026-11-27T09:30:00+03:00", "текст");

        Assert.False(outcome.WindowsTaskRegistered);
        Assert.True(outcome.SweepTaskPresent);
        var warning = string.Join(" ", outcome.Warnings);
        Assert.Contains("не гарантированный точный срок", warning);
        Assert.DoesNotContain("будет доставлена", warning);
    }

    [Fact]
    public void Cancel_RemovesTaskAndMarksCancelled()
    {
        using var db = new TempDb();
        var registrar = new FakeRegistrar();
        var service = Service(db, registrar, new FakePresenter());
        var created = service.Schedule("2026-11-27T09:30:00+03:00", "текст").Reminder;

        var outcome = service.Cancel(created.Id);

        Assert.True(outcome.Found);
        Assert.Equal(CancelStatus.Cancelled, outcome.Status);
        Assert.True(outcome.WindowsTaskRemoved);
        Assert.Contains(TaskNames.Reminder(created.Id), registrar.Deleted);
        Assert.Equal(ReminderState.Cancelled, db.Store.Get(created.Id)!.State);
    }

    [Fact]
    public void Cancel_UnknownIdNotFound()
    {
        using var db = new TempDb();
        var service = Service(db, new FakeRegistrar(), new FakePresenter());
        Assert.False(service.Cancel("rem_0000000000000000").Found);
    }

    [Fact]
    public void RunDue_SendsOverdueAndMarksSent()
    {
        using var db = new TempDb();
        var registrar = new FakeRegistrar();
        var presenter = new FakePresenter();
        var clock = Now;
        var service = Service(db, registrar, presenter, () => clock);
        var created = service.Schedule("2026-11-26T09:01:00+00:00", "пора").Reminder;

        clock = Now.AddMinutes(2);
        var outcome = service.RunDue();

        Assert.Equal(1, outcome.Claimed);
        Assert.Equal(1, outcome.Sent);
        Assert.Equal(0, outcome.Failed);
        Assert.Equal(created.Id, presenter.Shown.Single().Id);
        Assert.Equal(ReminderState.Sent, db.Store.Get(created.Id)!.State);

        var again = service.RunDue();
        Assert.Equal(0, again.Claimed);
        Assert.Single(presenter.Shown);
    }

    [Fact]
    public void RunDue_SendFailureKeepsPendingForRetry()
    {
        using var db = new TempDb();
        var presenter = new FakePresenter { Fail = true };
        var clock = Now;
        var service = Service(db, new FakeRegistrar(), presenter, () => clock);
        var created = service.Schedule("2026-11-26T09:01:00+00:00", "сбой").Reminder;
        clock = Now.AddMinutes(2);

        var outcome = service.RunDue();
        Assert.Equal(0, outcome.Sent);
        Assert.Equal(1, outcome.Failed);
        var stored = db.Store.Get(created.Id)!;
        Assert.Equal(ReminderState.Pending, stored.State);
        Assert.Contains("тестовый отказ", stored.LastError);

        presenter.Fail = false;
        clock = Now.AddMinutes(7);
        var retry = service.RunDue();
        Assert.Equal(1, retry.Sent);
        Assert.Equal(ReminderState.Sent, db.Store.Get(created.Id)!.State);
        Assert.Equal(2, db.Store.Get(created.Id)!.Attempts);
    }

    [Fact]
    public void RunDue_RepairsMissingTaskForFutureReminder()
    {
        using var db = new TempDb();
        var registrar = new FakeRegistrar();
        var service = Service(db, registrar, new FakePresenter());
        var created = service.Schedule("2026-11-27T09:30:00+03:00", "перерегистрировать").Reminder;
        registrar.Existing.Clear(); // имитация потерянной задачи Windows

        var outcome = service.RunDue();

        Assert.Equal(1, outcome.RepairedTasks);
        Assert.True(registrar.Exists(TaskNames.Reminder(created.Id)));
        Assert.Equal(ReminderState.Pending, db.Store.Get(created.Id)!.State);
    }

    [Fact]
    public void RunDue_CleansUpDeliveredTask()
    {
        using var db = new TempDb();
        var registrar = new FakeRegistrar();
        var clock = Now;
        var service = Service(db, registrar, new FakePresenter(), () => clock);
        var created = service.Schedule("2026-11-26T09:01:00+00:00", "убрать задачу").Reminder;
        Assert.True(registrar.Exists(TaskNames.Reminder(created.Id)));

        clock = Now.AddMinutes(2);
        var outcome = service.RunDue();

        Assert.Equal(1, outcome.Sent);
        Assert.False(registrar.Exists(TaskNames.Reminder(created.Id)));
        Assert.Null(db.Store.Get(created.Id)!.TaskName);
    }

    [Fact]
    public void RunDue_DeliversBeforeRepairingBrokenFutureTasks()
    {
        using var db = new TempDb();
        var registrar = new FakeRegistrar();
        var clock = Now;
        var service = Service(db, registrar, new FakePresenter(), () => clock);
        service.Schedule("2026-11-26T09:01:00+00:00", "доставить");
        service.Schedule("2026-11-27T09:01:00+00:00", "будущее");
        registrar.Existing.Clear();
        registrar.FailRegistration = true; // ремонт будущих задач падает

        clock = Now.AddMinutes(2);
        var outcome = service.RunDue();

        Assert.Equal(1, outcome.Sent);
        Assert.Equal(0, outcome.Failed);
        Assert.Equal(0, outcome.RepairedTasks);
    }

    [Fact]
    public void Cancel_AfterClaim_ReportsNotCancelled()
    {
        using var db = new TempDb();
        var registrar = new FakeRegistrar();
        var clock = Now;
        var service = Service(db, registrar, new FakePresenter(), () => clock);
        var created = service.Schedule("2026-11-26T09:01:00+00:00", "уже доставляют").Reminder;
        clock = Now.AddMinutes(2);
        db.Store.ClaimDue(clock); // гонка: воркер уже забрал запись

        var outcome = service.Cancel(created.Id);

        Assert.True(outcome.Found);
        Assert.Equal(CancelStatus.InProgress, outcome.Status);
        Assert.Contains("Доставка уже начата", string.Join(" ", outcome.Warnings));
        Assert.Empty(registrar.Deleted);
        Assert.Equal(ReminderState.Sending, db.Store.Get(created.Id)!.State);
    }

    [Fact]
    public void Cancel_AfterDelivery_ReportsNotCancelled()
    {
        using var db = new TempDb();
        var registrar = new FakeRegistrar();
        var presenter = new FakePresenter();
        var clock = Now;
        var service = Service(db, registrar, presenter, () => clock);
        var created = service.Schedule("2026-11-26T09:01:00+00:00", "уже доставлено").Reminder;
        clock = Now.AddMinutes(2);
        service.RunDue();

        var outcome = service.Cancel(created.Id);

        Assert.Equal(CancelStatus.AlreadySent, outcome.Status);
        Assert.Contains("уже доставлено", string.Join(" ", outcome.Warnings));
        Assert.Equal(ReminderState.Sent, db.Store.Get(created.Id)!.State);
        Assert.Single(presenter.Shown);
    }

    [Fact]
    public void Concurrency_TwoServicesDeliverOnce()
    {
        using var db = new TempDb();
        var clock = Now;
        var presenter = new FakePresenter();
        var s1 = Service(db, new FakeRegistrar(), presenter, () => clock);
        var s2 = Service(db, new FakeRegistrar(), presenter, () => clock);
        s1.Schedule("2026-11-26T09:01:00+00:00", "одна доставка").ToString();
        clock = Now.AddMinutes(2);

        Parallel.Invoke(() => s1.RunDue(), () => s2.RunDue());

        Assert.Single(presenter.Shown);
    }

    [Fact]
    public void RunDue_RecoversStaleClaim()
    {
        using var db = new TempDb();
        var presenter = new FakePresenter();
        var clock = Now;
        var service = Service(db, new FakeRegistrar(), presenter, () => clock);
        var created = service.Schedule("2026-11-26T09:01:00+00:00", "зависло").Reminder;
        clock = Now.AddMinutes(2);
        db.Store.ClaimDue(clock); // обработчик «упал» после claim

        clock = Now.AddMinutes(30);
        var outcome = service.RunDue();
        Assert.Equal(1, outcome.RecoveredStale);
        Assert.Equal(1, outcome.Sent);
        Assert.Equal(ReminderState.Sent, db.Store.Get(created.Id)!.State);
    }
}



