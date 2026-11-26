using AgentNotify.Core;
using Xunit;

namespace AgentNotify.Tests;

public class ReminderStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_List_And_Cancel()
    {
        using var db = new TempDb();
        var created = db.Store.Create("текст", Now.AddHours(2), Now);
        Assert.StartsWith("rem_", created.Id);
        Assert.Equal(ReminderState.Pending, db.Store.Get(created.Id)!.State);

        var list = db.Store.List(includeFinished: false);
        Assert.Single(list);
        Assert.Equal("текст", list[0].Text);
        Assert.Equal(created.DueUtc, list[0].DueUtc);

        var cancelled = db.Store.Cancel(created.Id);
        Assert.Equal(CancelStatus.Cancelled, cancelled.Status);
        Assert.Equal(ReminderState.Cancelled, db.Store.Get(created.Id)!.State);
        Assert.Empty(db.Store.List(includeFinished: false));
        Assert.Single(db.Store.List(includeFinished: true));
    }

    [Fact]
    public void ClaimDue_OnlyDueRecords()
    {
        using var db = new TempDb();
        var due = db.Store.Create("срок прошёл", Now.AddMinutes(-1), Now.AddMinutes(-5));
        var future = db.Store.Create("будущее", Now.AddMinutes(10), Now);

        var claimed = db.Store.ClaimDue(Now);
        Assert.Single(claimed);
        Assert.Equal(due.Id, claimed[0].Id);
        Assert.Equal(ReminderState.Sending, db.Store.Get(due.Id)!.State);
        Assert.Equal(ReminderState.Pending, db.Store.Get(future.Id)!.State);

        Assert.Empty(db.Store.ClaimDue(Now));
    }

    [Fact]
    public void ClaimDue_ConcurrentWorkers_DeliverOnce()
    {
        using var db = new TempDb();
        db.Store.Create("одна запись", Now.AddMinutes(-1), Now.AddMinutes(-2));
        var results = new List<Reminder>[2];
        Parallel.For(0, 2, new ParallelOptions { MaxDegreeOfParallelism = 2 }, i =>
        {
            var store = new ReminderStore(db.Store.DbPath);
            results[i] = store.ClaimDue(Now).ToList();
        });
        Assert.Equal(1, results.Sum(r => r.Count));
    }

    [Fact]
    public void MarkSent_ClearsClaimAndSticks()
    {
        using var db = new TempDb();
        db.Store.Create("текст", Now.AddMinutes(-1), Now);
        var claimed = db.Store.ClaimDue(Now).Single();
        Assert.True(db.Store.MarkSent(claimed.Id, claimed.ClaimToken!, Now));

        var stored = db.Store.Get(claimed.Id)!;
        Assert.Equal(ReminderState.Sent, stored.State);
        Assert.NotNull(stored.SentUtc);
        Assert.Null(stored.ClaimToken);
        Assert.Empty(db.Store.ClaimDue(Now.AddHours(1)));
    }

    [Fact]
    public void MarkFailed_KeepsRecordPendingWithError()
    {
        using var db = new TempDb();
        db.Store.Create("текст", Now.AddMinutes(-1), Now);
        var claimed = db.Store.ClaimDue(Now).Single();
        Assert.True(db.Store.MarkFailed(claimed.Id, claimed.ClaimToken!, "сбой показа"));

        var stored = db.Store.Get(claimed.Id)!;
        Assert.Equal(ReminderState.Pending, stored.State);
        Assert.Equal(1, stored.Attempts);
        Assert.Equal("сбой показа", stored.LastError);

        var retry = db.Store.ClaimDue(Now.AddMinutes(1)).Single();
        Assert.Equal(2, retry.Attempts);
    }

    [Fact]
    public void MarkSent_RejectsForeignClaimToken()
    {
        using var db = new TempDb();
        db.Store.Create("текст", Now.AddMinutes(-1), Now);
        var claimed = db.Store.ClaimDue(Now).Single();
        Assert.False(db.Store.MarkSent(claimed.Id, "чужой-токен", Now));
        Assert.Equal(ReminderState.Sending, db.Store.Get(claimed.Id)!.State);
    }

    [Fact]
    public void RecoverStaleClaims_ReturnsAbandonedSending()
    {
        using var db = new TempDb();
        db.Store.Create("текст", Now.AddMinutes(-1), Now);
        db.Store.ClaimDue(Now);

        Assert.Equal(0, db.Store.RecoverStaleClaims(Now.AddMinutes(1), TimeSpan.FromMinutes(10)));
        var recovered = db.Store.RecoverStaleClaims(Now.AddMinutes(30), TimeSpan.FromMinutes(10));
        Assert.Equal(1, recovered);

        var stored = db.Store.List(includeFinished: false).Single();
        Assert.Equal(ReminderState.Pending, stored.State);
        Assert.Contains("прерванная отправка", stored.LastError);
        Assert.Single(db.Store.ClaimDue(Now.AddMinutes(31)));
    }

    [Fact]
    public void Cancel_RefusesWhenDeliveryStartedOrFinished()
    {
        using var db = new TempDb();
        var created = db.Store.Create("текст", Now.AddMinutes(-1), Now);
        var claimed = db.Store.ClaimDue(Now).Single();
        Assert.Equal(CancelStatus.InProgress, db.Store.Cancel(claimed.Id).Status);
        Assert.Equal(ReminderState.Sending, db.Store.Get(created.Id)!.State);

        Assert.True(db.Store.MarkSent(created.Id, claimed.ClaimToken!, Now));
        Assert.Equal(CancelStatus.AlreadySent, db.Store.Cancel(created.Id).Status);
        Assert.Equal(ReminderState.Sent, db.Store.Get(created.Id)!.State);
    }

    [Fact]
    public void Cancel_UnknownReturnsNotFound()
    {
        using var db = new TempDb();
        Assert.Equal(CancelStatus.NotFound, db.Store.Cancel("rem_0000000000000000").Status);
    }

    [Fact]
    public void Store_PersistsAcrossReopen()
    {
        using var db = new TempDb();
        var created = db.Store.Create("персистентность", Now.AddHours(1), Now);
        var reopened = new ReminderStore(db.Store.DbPath);
        Assert.Equal("персистентность", reopened.Get(created.Id)!.Text);
    }

    [Fact]
    public void Purge_RemovesRow()
    {
        using var db = new TempDb();
        var created = db.Store.Create("удалить", Now.AddHours(1), Now);
        Assert.True(db.Store.Purge(created.Id));
        Assert.Null(db.Store.Get(created.Id));
        Assert.False(db.Store.Purge(created.Id));
    }
}
