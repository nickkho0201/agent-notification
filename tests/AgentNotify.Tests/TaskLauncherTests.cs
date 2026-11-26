using AgentNotify.Core;
using AgentNotify.Windows;
using Xunit;

namespace AgentNotify.Tests;

/// <summary>
/// Задачи Планировщика должны запускаться через оконно-невидимый лаунчер, а уже поставленные
/// задачи — переводиться на него без потери срока.
/// </summary>
public class TaskLauncherTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);
    private const string Home = @"C:\Users\Иван Петров\AppData\Local\AgentNotification";

    [Fact]
    public void Resolve_FindsLauncherNextToMainExe()
    {
        var directory = TestFiles.NewDirectory();
        try
        {
            var launcher = Path.Combine(directory, TaskLauncher.FileName);
            File.WriteAllText(launcher, "stub");
            File.WriteAllText(Path.Combine(directory, "AgentNotify.exe"), "stub");

            Assert.Equal(launcher, TaskLauncher.Resolve(Path.Combine(directory, "AgentNotify.exe")));
            Assert.Equal(launcher, TaskLauncher.CommandFor(Path.Combine(directory, "AgentNotify.exe")));
        }
        finally
        {
            TestFiles.Delete(directory);
        }
    }

    [Fact]
    public void Resolve_WithoutLauncherFallsBackToMainExe()
    {
        var directory = TestFiles.NewDirectory();
        try
        {
            var exe = Path.Combine(directory, "AgentNotify.exe");
            File.WriteAllText(exe, "stub");

            Assert.Null(TaskLauncher.Resolve(exe));
            Assert.Equal(exe, TaskLauncher.CommandFor(exe));
        }
        finally
        {
            TestFiles.Delete(directory);
        }
    }

    [Fact]
    public void SameCommand_ComparesFullPathsCaseInsensitively()
    {
        Assert.True(TaskLauncher.SameCommand(@"C:\App\AgentNotify.Launcher.exe", @"c:\app\agentnotify.launcher.EXE"));
        Assert.False(TaskLauncher.SameCommand(@"C:\App\AgentNotify.exe", @"C:\App\AgentNotify.Launcher.exe"));
        Assert.False(TaskLauncher.SameCommand(null, @"C:\App\AgentNotify.Launcher.exe"));
    }

    [Fact]
    public void Schedule_RegistersTaskThroughLauncher_NeverThroughConsoleExe()
    {
        var directory = TestFiles.NewDirectory();
        try
        {
            var exe = Path.Combine(directory, "AgentNotify.exe");
            var launcher = Path.Combine(directory, TaskLauncher.FileName);
            File.WriteAllText(exe, "stub");
            File.WriteAllText(launcher, "stub");

            using var db = new TempDb();
            var registrar = new FakeRegistrar();
            var service = new ReminderService(db.Store, registrar, new FakePresenter(), () => Now, exePath: exe);

            var outcome = service.Schedule("2026-11-27T09:30:00+03:00", "через лаунчер");

            Assert.True(outcome.WindowsTaskRegistered);
            Assert.True(service.UsesLauncher);
            Assert.Equal(launcher, service.TaskCommand);
            Assert.Equal(launcher, registrar.Actions[outcome.Reminder.TaskName!]);
            Assert.Empty(service.LauncherWarnings());
            Assert.All(registrar.Actions.Values, command => Assert.NotEqual(exe, command));
        }
        finally
        {
            TestFiles.Delete(directory);
        }
    }

    [Fact]
    public void Schedule_WithoutLauncherWarnsInsteadOfSilentlyShowingConsole()
    {
        var directory = TestFiles.NewDirectory();
        try
        {
            var exe = Path.Combine(directory, "AgentNotify.exe");
            File.WriteAllText(exe, "stub");

            using var db = new TempDb();
            var service = new ReminderService(db.Store, new FakeRegistrar(), new FakePresenter(), () => Now, exePath: exe);

            var outcome = service.Schedule("2026-11-27T09:30:00+03:00", "без лаунчера");

            Assert.False(service.UsesLauncher);
            Assert.Contains("окно терминала", string.Join(" ", outcome.Warnings));
        }
        finally
        {
            TestFiles.Delete(directory);
        }
    }

    [Fact]
    public void Migrate_RepointsLegacyTaskToLauncherKeepingDueAndArguments()
    {
        var directory = TestFiles.NewDirectory();
        try
        {
            var exe = Path.Combine(directory, "AgentNotify.exe");
            var launcher = Path.Combine(directory, TaskLauncher.FileName);
            File.WriteAllText(exe, "stub");
            File.WriteAllText(launcher, "stub");

            using var db = new TempDb();
            var registrar = new FakeRegistrar();
            var service = new ReminderService(db.Store, registrar, new FakePresenter(), () => Now, exePath: exe);
            var created = service.Schedule("2026-11-27T09:30:00+03:00", "мигрировать").Reminder;

            // Задача поставлена прежней сборкой: Command — консольный exe.
            registrar.SimulateLegacyCommand(created.TaskName!, exe);
            var dueBefore = db.Store.Get(created.Id)!.DueUtc;

            var migration = service.MigrateWorkerTasks();

            Assert.Equal(1, migration.Migrated);
            Assert.Equal(0, migration.Failed);
            Assert.Equal(0, migration.AlreadyOk);
            Assert.Equal(0, migration.Remaining);
            Assert.True(migration.Ok);
            Assert.Equal(launcher, registrar.Actions[created.TaskName!]);
            Assert.Equal(dueBefore, db.Store.Get(created.Id)!.DueUtc);
            Assert.Equal(dueBefore, registrar.Details.Last().DueLocal);
            Assert.Contains("run-due", registrar.Details.Last().Arguments);
            Assert.Equal(ReminderState.Pending, db.Store.Get(created.Id)!.State);
        }
        finally
        {
            TestFiles.Delete(directory);
        }
    }

    [Fact]
    public void Migrate_SkipsTasksAlreadyUsingLauncher()
    {
        var directory = TestFiles.NewDirectory();
        try
        {
            var exe = Path.Combine(directory, "AgentNotify.exe");
            File.WriteAllText(exe, "stub");
            File.WriteAllText(Path.Combine(directory, TaskLauncher.FileName), "stub");

            using var db = new TempDb();
            var registrar = new FakeRegistrar();
            var service = new ReminderService(db.Store, registrar, new FakePresenter(), () => Now, exePath: exe);
            service.Schedule("2026-11-27T09:30:00+03:00", "уже мигрирована");

            var first = service.MigrateWorkerTasks();
            var second = service.MigrateWorkerTasks();

            Assert.Equal(0, first.Migrated);
            Assert.Equal(1, first.AlreadyOk);
            Assert.Equal(0, second.Migrated);
            Assert.Equal(1, second.AlreadyOk);
        }
        finally
        {
            TestFiles.Delete(directory);
        }
    }

    [Fact]
    public void Migrate_ReportsFailureExplicitlyAndKeepsRecord()
    {
        var directory = TestFiles.NewDirectory();
        try
        {
            var exe = Path.Combine(directory, "AgentNotify.exe");
            File.WriteAllText(exe, "stub");
            File.WriteAllText(Path.Combine(directory, TaskLauncher.FileName), "stub");

            using var db = new TempDb();
            var registrar = new FakeRegistrar();
            var service = new ReminderService(db.Store, registrar, new FakePresenter(), () => Now, exePath: exe);
            var created = service.Schedule("2026-11-27T09:30:00+03:00", "сбой миграции").Reminder;
            registrar.SimulateLegacyCommand(created.TaskName!, exe);
            registrar.FailRegistration = true;

            var migration = service.MigrateWorkerTasks();

            Assert.Equal(0, migration.Migrated);
            Assert.Equal(1, migration.Failed);
            Assert.False(migration.Ok);
            Assert.Single(migration.Errors);
            Assert.Contains("регистратор", migration.Errors[0]);
            Assert.Equal(ReminderState.Pending, db.Store.Get(created.Id)!.State);
            Assert.Equal(exe, registrar.Actions[created.TaskName!]);
        }
        finally
        {
            TestFiles.Delete(directory);
        }
    }

    [Fact]
    public void Migrate_RespectsBatchLimitAndReportsRemaining()
    {
        var directory = TestFiles.NewDirectory();
        try
        {
            var exe = Path.Combine(directory, "AgentNotify.exe");
            File.WriteAllText(exe, "stub");
            File.WriteAllText(Path.Combine(directory, TaskLauncher.FileName), "stub");

            using var db = new TempDb();
            var registrar = new FakeRegistrar();
            var service = new ReminderService(db.Store, registrar, new FakePresenter(), () => Now, exePath: exe);
            var ids = new List<string>();
            for (var i = 1; i <= 3; i++)
                ids.Add(service.Schedule($"2026-11-27T{9 + i:00}:30:00+03:00", "задача " + i).Reminder.Id);
            foreach (var id in ids) registrar.SimulateLegacyCommand(TaskNames.Reminder(id), exe);

            var first = service.MigrateWorkerTasks(limit: 2);
            var second = service.MigrateWorkerTasks(limit: 2);

            Assert.Equal(2, first.Migrated);
            Assert.Equal(1, first.Remaining);
            Assert.False(first.Ok);
            Assert.Equal(1, second.Migrated);
            Assert.Equal(0, second.Remaining);
            Assert.True(second.Ok);
            Assert.All(ids, id => Assert.EndsWith(TaskLauncher.FileName, registrar.Actions[TaskNames.Reminder(id)]));
        }
        finally
        {
            TestFiles.Delete(directory);
        }
    }

    [Fact]
    public void Migrate_IgnoresMissingTasksAndPastDueReminders()
    {
        var directory = TestFiles.NewDirectory();
        try
        {
            var exe = Path.Combine(directory, "AgentNotify.exe");
            File.WriteAllText(exe, "stub");
            File.WriteAllText(Path.Combine(directory, TaskLauncher.FileName), "stub");

            using var db = new TempDb();
            var registrar = new FakeRegistrar();
            var clock = Now;
            var service = new ReminderService(db.Store, registrar, new FakePresenter(), () => clock, exePath: exe);
            var past = service.Schedule("2026-11-26T09:01:00+00:00", "просрочено").Reminder;
            service.Schedule("2026-11-27T09:30:00+03:00", "без задачи").ToString();
            registrar.SimulateLegacyCommand(TaskNames.Reminder(past.Id), exe); // задача просроченного напоминания
            registrar.Existing.Clear(); // у будущего напоминания задачи нет: его восстановит repair

            var migration = service.MigrateWorkerTasks();

            Assert.Equal(0, migration.Migrated);
            Assert.Equal(0, migration.Failed);
            Assert.Equal(0, migration.Remaining);
            Assert.True(migration.Ok);
        }
        finally
        {
            TestFiles.Delete(directory);
        }
    }

    [Fact]
    public void RunDue_MigratesLegacyTaskAndThenSkipsByMarker()
    {
        var directory = TestFiles.NewDirectory();
        try
        {
            var exe = Path.Combine(directory, "AgentNotify.exe");
            var launcher = Path.Combine(directory, TaskLauncher.FileName);
            File.WriteAllText(exe, "stub");
            File.WriteAllText(launcher, "stub");

            using var db = new TempDb();
            var registrar = new FakeRegistrar();
            var service = new ReminderService(db.Store, registrar, new FakePresenter(), () => Now, exePath: exe);
            var created = service.Schedule("2026-11-27T09:30:00+03:00", "обход переведёт").Reminder;
            registrar.SimulateLegacyCommand(created.TaskName!, exe);
            Assert.False(service.WorkerCommandApplied);

            var sweep = service.RunDue();

            Assert.Equal(1, sweep.MigratedTasks);
            Assert.Equal(launcher, registrar.Actions[created.TaskName!]);
            Assert.True(service.WorkerCommandApplied);
            Assert.Contains(launcher, File.ReadAllText(service.WorkerCommandMarkerPath));

            var next = service.RunDue();
            Assert.Equal(0, next.MigratedTasks);
        }
        finally
        {
            TestFiles.Delete(directory);
        }
    }

    [Fact]
    public void RunDue_MigratesWhenLauncherAppearsAfterInstall()
    {
        var directory = TestFiles.NewDirectory();
        try
        {
            var exe = Path.Combine(directory, "AgentNotify.exe");
            File.WriteAllText(exe, "stub");

            using var db = new TempDb();
            var registrar = new FakeRegistrar();
            var withoutLauncher = new ReminderService(db.Store, registrar, new FakePresenter(), () => Now, exePath: exe);
            var created = withoutLauncher.Schedule("2026-11-27T09:30:00+03:00", "лаунчер появится позже").Reminder;
            withoutLauncher.RunDue(); // отметка записана для консольного exe
            Assert.Contains(exe, File.ReadAllText(withoutLauncher.WorkerCommandMarkerPath));

            var launcher = Path.Combine(directory, TaskLauncher.FileName);
            File.WriteAllText(launcher, "stub");
            var service = new ReminderService(db.Store, registrar, new FakePresenter(), () => Now, exePath: exe);

            var sweep = service.RunDue();

            Assert.Equal(1, sweep.MigratedTasks);
            Assert.Equal(launcher, registrar.Actions[created.TaskName!]);
        }
        finally
        {
            TestFiles.Delete(directory);
        }
    }

    [Fact]
    public void Migrate_DoesNotMarkAppliedWhileWorkRemains()
    {
        var directory = TestFiles.NewDirectory();
        try
        {
            var exe = Path.Combine(directory, "AgentNotify.exe");
            File.WriteAllText(exe, "stub");
            File.WriteAllText(Path.Combine(directory, TaskLauncher.FileName), "stub");

            using var db = new TempDb();
            var registrar = new FakeRegistrar();
            var service = new ReminderService(db.Store, registrar, new FakePresenter(), () => Now, exePath: exe);
            var ids = new List<string>();
            for (var i = 1; i <= 2; i++)
                ids.Add(service.Schedule($"2026-11-27T{9 + i:00}:30:00+03:00", "задача " + i).Reminder.Id);
            foreach (var id in ids) registrar.SimulateLegacyCommand(TaskNames.Reminder(id), exe);

            var partial = service.MigrateWorkerTasks(limit: 1);
            Assert.False(partial.Ok);
            Assert.False(File.Exists(service.WorkerCommandMarkerPath));

            var rest = service.MigrateWorkerTasks(limit: 1);
            Assert.True(rest.Ok);
            Assert.True(service.WorkerCommandApplied);
        }
        finally
        {
            TestFiles.Delete(directory);
        }
    }

    [Fact]
    public void Migrate_ProcessesQueueLargerThanListLimitInPages()
    {
        var directory = TestFiles.NewDirectory();
        try
        {
            var exe = Path.Combine(directory, "AgentNotify.exe");
            File.WriteAllText(exe, "stub");
            File.WriteAllText(Path.Combine(directory, TaskLauncher.FileName), "stub");

            using var db = new TempDb();
            var registrar = new FakeRegistrar();
            var service = new ReminderService(db.Store, registrar, new FakePresenter(), () => Now, exePath: exe);
            // 620 записей: больше общего лимита ReminderStore.List (500), все будущие.
            var ids = BulkReminders.InsertPending(db.Store, 620, Now.AddDays(1), TimeSpan.FromSeconds(30));
            foreach (var id in ids) registrar.SimulateLegacyCommand(TaskNames.Reminder(id), exe);

            var first = service.MigrateWorkerTasks();
            Assert.Equal(500, first.Migrated);
            Assert.Equal(120, first.Remaining);
            Assert.False(first.Ok);
            Assert.False(File.Exists(service.WorkerCommandMarkerPath));

            var second = service.MigrateWorkerTasks();
            Assert.Equal(120, second.Migrated);
            Assert.Equal(0, second.Remaining);
            Assert.True(second.Ok);
            Assert.True(service.WorkerCommandApplied);

            var third = service.MigrateWorkerTasks();
            Assert.Equal(0, third.Migrated);
            Assert.Equal(620, third.AlreadyOk);
            Assert.True(third.Ok);
            Assert.All(ids, id => Assert.EndsWith(TaskLauncher.FileName, registrar.Actions[TaskNames.Reminder(id)]));
        }
        finally
        {
            TestFiles.Delete(directory);
        }
    }

    [Fact]
    public void Migrate_SkipsSendingRecordsWithoutConsumingBudget()
    {
        var directory = TestFiles.NewDirectory();
        try
        {
            var exe = Path.Combine(directory, "AgentNotify.exe");
            File.WriteAllText(exe, "stub");
            File.WriteAllText(Path.Combine(directory, TaskLauncher.FileName), "stub");

            using var db = new TempDb();
            var registrar = new FakeRegistrar();
            var clock = Now;
            var service = new ReminderService(db.Store, registrar, new FakePresenter(), () => clock, exePath: exe);
            // Одна запись уже доставляется (sending), три ждут миграции.
            var overdue = service.Schedule("2026-11-26T09:01:00+00:00", "доставляется").Reminder;
            var future = new List<string>();
            for (var i = 1; i <= 3; i++)
                future.Add(service.Schedule($"2026-11-27T{9 + i:00}:30:00+03:00", "ждут").Reminder.Id);

            clock = Now.AddMinutes(2);
            db.Store.ClaimDue(clock); // запись стала sending
            foreach (var id in future) registrar.SimulateLegacyCommand(TaskNames.Reminder(id), exe);
            registrar.SimulateLegacyCommand(TaskNames.Reminder(overdue.Id), exe);

            var migration = service.MigrateWorkerTasks(limit: 3);

            Assert.Equal(3, migration.Migrated);
            Assert.Equal(0, migration.Remaining);
            Assert.Equal(ReminderState.Sending, db.Store.Get(overdue.Id)!.State);
            Assert.All(future, id => Assert.EndsWith(TaskLauncher.FileName, registrar.Actions[TaskNames.Reminder(id)]));
        }
        finally
        {
            TestFiles.Delete(directory);
        }
    }

    [Fact]
    public void Migrate_RetiresOverdueLegacyTriggerWhenInvisibleSweepIsConfirmed()
    {
        var directory = TestFiles.NewDirectory();
        try
        {
            var exe = Path.Combine(directory, "AgentNotify.exe");
            var launcher = Path.Combine(directory, TaskLauncher.FileName);
            File.WriteAllText(exe, "stub");
            File.WriteAllText(launcher, "stub");

            using var db = new TempDb();
            var registrar = new FakeRegistrar();
            var clock = Now.AddMinutes(-10);
            var service = new ReminderService(db.Store, registrar, new FakePresenter(), () => clock, exePath: exe);
            // Обход уже поставлен на лаунчер: доставка просроченного будет невидимой.
            registrar.RegisterSweep(TaskNames.Sweep, launcher, "run-due", Now);
            var overdue = service.Schedule("2026-11-26T08:59:00+00:00", "просрочено").Reminder;
            registrar.SimulateLegacyCommand(overdue.TaskName!, exe);
            clock = Now; // запись стала просроченной

            var migration = service.MigrateWorkerTasks();

            Assert.Equal(1, migration.Retired);
            Assert.Equal(0, migration.Migrated);
            Assert.Equal(0, migration.Blocked);
            Assert.True(migration.Ok);
            Assert.DoesNotContain(overdue.TaskName!, registrar.Existing);
            // Запись не удалена: её доставит обход.
            Assert.Equal(ReminderState.Pending, db.Store.Get(overdue.Id)!.State);
            Assert.Null(db.Store.Get(overdue.Id)!.TaskName);
        }
        finally
        {
            TestFiles.Delete(directory);
        }
    }

    [Fact]
    public void Migrate_DoesNotRetireOverdueTriggerWhileSweepIsNotInvisible()
    {
        var directory = TestFiles.NewDirectory();
        try
        {
            var exe = Path.Combine(directory, "AgentNotify.exe");
            File.WriteAllText(exe, "stub");
            File.WriteAllText(Path.Combine(directory, TaskLauncher.FileName), "stub");

            using var db = new TempDb();
            var registrar = new FakeRegistrar();
            var clock = Now.AddMinutes(-10);
            var service = new ReminderService(db.Store, registrar, new FakePresenter(), () => clock, exePath: exe);
            var overdue = service.Schedule("2026-11-26T08:59:00+00:00", "просрочено без обхода").Reminder;
            registrar.SimulateLegacyCommand(overdue.TaskName!, exe);
            clock = Now; // запись стала просроченной

            var migration = service.MigrateWorkerTasks();

            Assert.Equal(0, migration.Retired);
            Assert.Equal(1, migration.Blocked);
            Assert.False(migration.Ok);
            // Триггер остался, запись осталась: снимать единственный путь доставки нельзя.
            Assert.Contains(overdue.TaskName!, registrar.Existing);
            Assert.Equal(ReminderState.Pending, db.Store.Get(overdue.Id)!.State);
            Assert.False(File.Exists(service.WorkerCommandMarkerPath));
        }
        finally
        {
            TestFiles.Delete(directory);
        }
    }

    [Fact]
    public void Migrate_DoesNotRetireOverdueTriggerWhileSweepStillUsesConsoleExe()
    {
        var directory = TestFiles.NewDirectory();
        try
        {
            var exe = Path.Combine(directory, "AgentNotify.exe");
            var launcher = Path.Combine(directory, TaskLauncher.FileName);
            File.WriteAllText(exe, "stub");
            File.WriteAllText(launcher, "stub");

            using var db = new TempDb();
            var registrar = new FakeRegistrar();
            var clock = Now.AddMinutes(-10);
            var service = new ReminderService(db.Store, registrar, new FakePresenter(), () => clock, exePath: exe);
            registrar.RegisterSweep(TaskNames.Sweep, exe, "run-due", Now); // обход старой сборки
            var overdue = service.Schedule("2026-11-26T08:59:00+00:00", "просрочено").Reminder;
            registrar.SimulateLegacyCommand(overdue.TaskName!, exe);
            clock = Now; // запись стала просроченной

            var migration = service.MigrateWorkerTasks();

            Assert.Equal(0, migration.Retired);
            Assert.Equal(1, migration.Blocked);
            Assert.Contains(overdue.TaskName!, registrar.Existing);
        }
        finally
        {
            TestFiles.Delete(directory);
        }
    }

    [Fact]
    public void TaskXml_WithLauncherCommandKeepsInteractivePrincipalAndNoWindowSettings()    {
        var document = System.Xml.Linq.XDocument.Parse(TaskXml.BuildReminder(
            "AgentNotify.Reminder.rem_x", @"C:\app\AgentNotify.Launcher.exe", "run-due --home \"" + Home + "\"",
            new DateTimeOffset(2026, 12, 1, 6, 30, 0, TimeSpan.Zero), "S-1-5-21-1-2-3-1001", "тест"));

        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Assert.EndsWith("AgentNotify.Launcher.exe", document.Descendants(ns + "Command").Single().Value);
        Assert.Equal("InteractiveToken", document.Descendants(ns + "LogonType").Single().Value);
        Assert.Equal("LeastPrivilege", document.Descendants(ns + "RunLevel").Single().Value);
        Assert.Contains("run-due --home", document.Descendants(ns + "Arguments").Single().Value);
    }
}
