using AgentNotify.Core;
using Xunit;

namespace AgentNotify.Tests;

public class ReminderSearchTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);

    private static ReminderSearchQuery Query(
        string? text = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        bool overdue = false,
        string? id = null) => new(text, from, to, overdue, id);

    private static ReminderPage Search(TempDb db, ReminderSearchQuery query, int limit = 500, ReminderCursor? after = null) =>
        db.Store.Search(query, limit, after, Now);

    [Fact]
    public void Text_IsCaseInsensitiveUnicodeLiteralSubstring()
    {
        using var db = new TempDb();
        db.Store.Create("Оплата счёта за ноябрь", Now.AddHours(1), Now);
        db.Store.Create("оплатить интернет", Now.AddHours(2), Now);
        db.Store.Create("Позвонить в сервис", Now.AddHours(3), Now);
        db.Store.Create("ЁЛКА у подъезда", Now.AddHours(4), Now);
        db.Store.Create("Юникод ✓ и «кавычки»", Now.AddHours(5), Now);

        var upper = Search(db, Query("ОПЛАТ"));
        Assert.Equal(2, upper.Total);
        Assert.Contains(upper.Reminders, r => r.Text == "Оплата счёта за ноябрь");
        Assert.Contains(upper.Reminders, r => r.Text == "оплатить интернет");

        // Литеральная подстрока без морфологии: «оплата» не совпадает с «оплатить».
        var exact = Search(db, Query("оплата"));
        Assert.Single(exact.Reminders);
        Assert.Equal("Оплата счёта за ноябрь", exact.Reminders[0].Text);

        Assert.Single(Search(db, Query("ёлка")).Reminders);
        Assert.Single(Search(db, Query("✓")).Reminders);
        Assert.Single(Search(db, Query("КАВЫЧКИ")).Reminders);
    }

    [Fact]
    public void Text_IsTrimmedAndBlankMeansNoFilter()
    {
        using var db = new TempDb();
        db.Store.Create("оплата", Now.AddHours(1), Now);
        db.Store.Create("звонок", Now.AddHours(2), Now);

        var query = ReminderSearchInput.Create("  оплата  ", null, null, false, null, null, null, out _);
        Assert.Equal("оплата", query.Text);
        Assert.Single(Search(db, query).Reminders);

        var blank = ReminderSearchInput.Create("   ", null, null, false, null, null, null, out _);
        Assert.Null(blank.Text);
        Assert.Equal(2, Search(db, blank).Total);
    }

    [Fact]
    public void Bounds_FromInclusive_ToExclusive_ComparedByUtc()
    {
        using var db = new TempDb();
        var at10 = db.Store.Create("десять", new DateTimeOffset(2026, 11, 26, 10, 0, 0, TimeSpan.Zero), Now);
        db.Store.Create("одиннадцать", new DateTimeOffset(2026, 11, 26, 11, 0, 0, TimeSpan.Zero), Now);

        var page = Search(db, Query(from: new DateTimeOffset(2026, 11, 26, 10, 0, 0, TimeSpan.Zero),
                                   to: new DateTimeOffset(2026, 11, 26, 11, 0, 0, TimeSpan.Zero)));

        Assert.Single(page.Reminders);
        Assert.Equal(at10.Id, page.Reminders[0].Id);

        // Та же граница, но с явным смещением источника: сравнение идёт по UTC.
        var withOffset = Search(db, Query(from: new DateTimeOffset(2026, 11, 26, 13, 0, 0, TimeSpan.FromHours(3)),
                                          to: new DateTimeOffset(2026, 11, 26, 14, 0, 0, TimeSpan.FromHours(3))));
        Assert.Single(withOffset.Reminders);
        Assert.Equal(at10.Id, withOffset.Reminders[0].Id);
    }

    [Fact]
    public void Overdue_ComparesDueWithProvidedNow()
    {
        using var db = new TempDb();
        db.Store.Create("прошлое", Now.AddMinutes(-1), Now.AddHours(-1));
        var exactlyNow = db.Store.Create("ровно сейчас", Now, Now.AddHours(-1));
        db.Store.Create("будущее", Now.AddMinutes(1), Now);

        var page = Search(db, Query(overdue: true));
        Assert.Equal(2, page.Total);
        Assert.Contains(page.Reminders, r => r.Id == exactlyNow.Id);
        Assert.DoesNotContain(page.Reminders, r => r.Text == "будущее");

        Assert.Empty(Search(db, Query(text: "будущее", overdue: true)).Reminders);
    }

    [Fact]
    public void Id_IsExactAndCombinesWithOtherCriteria()
    {
        using var db = new TempDb();
        var first = db.Store.Create("оплата", Now.AddHours(1), Now);
        db.Store.Create("оплата", Now.AddHours(2), Now);

        var byId = Search(db, Query(id: first.Id));
        Assert.Single(byId.Reminders);
        Assert.Equal(first.Id, byId.Reminders[0].Id);

        Assert.Empty(Search(db, Query(id: "rem_ffffffffffffffff")).Reminders);
        Assert.Empty(Search(db, Query(text: "звонок", id: first.Id)).Reminders);
        Assert.Single(Search(db, Query(text: "опла", id: first.Id)).Reminders);
    }

    [Fact]
    public void Filters_CombineWithAnd()
    {
        using var db = new TempDb();
        var target = db.Store.Create("оплата счёта", Now.AddHours(1), Now);
        db.Store.Create("оплата счёта", Now.AddHours(3), Now);
        db.Store.Create("позвонить", Now.AddHours(1), Now);

        var page = Search(db, Query(
            text: "оплат",
            from: Now.AddMinutes(30),
            to: Now.AddHours(2),
            overdue: false));

        Assert.Single(page.Reminders);
        Assert.Equal(target.Id, page.Reminders[0].Id);
    }

    [Fact]
    public void OnlyPendingAndSendingAreReturned()
    {
        using var db = new TempDb();
        var pending = db.Store.Create("pending", Now.AddHours(1), Now);
        var sending = db.Store.Create("sending", Now.AddMinutes(-1), Now.AddHours(-1));
        var sent = db.Store.Create("sent", Now.AddMinutes(-2), Now.AddHours(-1));
        var cancelled = db.Store.Create("cancelled", Now.AddHours(2), Now);

        var claimed = db.Store.ClaimDue(Now).ToList();
        Assert.Equal(2, claimed.Count);
        var sentClaim = claimed.Single(r => r.Id == sent.Id);
        Assert.True(db.Store.MarkSent(sentClaim.Id, sentClaim.ClaimToken!, Now));
        Assert.Equal(ReminderState.Sending, db.Store.Get(sending.Id)!.State);
        Assert.Equal(ReminderState.Sent, db.Store.Get(sent.Id)!.State);
        Assert.Equal(CancelStatus.Cancelled, db.Store.Cancel(cancelled.Id).Status);

        var page = Search(db, Query());
        Assert.Contains(page.Reminders, r => r.Id == pending.Id);
        Assert.Contains(page.Reminders, r => r.Id == sending.Id);
        Assert.DoesNotContain(page.Reminders, r => r.Id == sent.Id);
        Assert.DoesNotContain(page.Reminders, r => r.Id == cancelled.Id);
        Assert.All(page.Reminders, r => Assert.True(r.State is ReminderState.Pending or ReminderState.Sending));
    }

    [Fact]
    public void Pagination_DoesNotLoseMatchesBeyondFirstPage()
    {
        using var db = new TempDb();
        var ids = BulkReminders.InsertPending(db.Store, 620, Now.AddHours(1), TimeSpan.FromMinutes(1));

        var seen = new List<string>();
        ReminderCursor? cursor = null;
        var pages = 0;
        while (true)
        {
            var page = Search(db, Query(), limit: 200, after: cursor);
            seen.AddRange(page.Reminders.Select(r => r.Id));
            pages++;
            if (!page.Truncated) break;
            Assert.NotNull(page.Next);
            cursor = page.Next;
            Assert.True(pages < 10, "курсор не завершил обход");
        }

        Assert.Equal(620, seen.Count);
        Assert.Equal(ids.Count, seen.Count);
        Assert.Equal(ids.Count, seen.Distinct(StringComparer.Ordinal).Count());
        Assert.True(pages >= 4);
    }

    [Fact]
    public void TextMatchBeyondFirst500_IsFound()
    {
        using var db = new TempDb();
        BulkReminders.InsertPending(db.Store, 620, Now.AddHours(1), TimeSpan.FromMinutes(1));

        var page = Search(db, Query(text: "массовая запись 615"));

        Assert.Equal(1, page.Total);
        Assert.Single(page.Reminders);
        Assert.False(page.Truncated);
        var index = Convert.ToInt64(page.Reminders[0].Id[4..], 16);
        Assert.Equal(615, index);
    }

    [Fact]
    public void Search_DoesNotChangeQueue()
    {
        using var db = new TempDb();
        var overdue = db.Store.Create("просрочено", Now.AddMinutes(-5), Now.AddHours(-1));
        db.Store.Create("будущее", Now.AddHours(1), Now);

        var before = db.Store.List(includeFinished: true)
            .Select(r => (r.Id, r.State, r.Attempts, r.Text, r.DueUtc, r.LastError, r.TaskName))
            .OrderBy(x => x.Id)
            .ToArray();

        Search(db, Query());
        Search(db, Query(text: "просроч", overdue: true));
        Search(db, Query(from: Now.AddHours(-10), to: Now.AddHours(10)), limit: 1);

        var after = db.Store.List(includeFinished: true)
            .Select(r => (r.Id, r.State, r.Attempts, r.Text, r.DueUtc, r.LastError, r.TaskName))
            .OrderBy(x => x.Id)
            .ToArray();
        Assert.Equal(before, after);
        Assert.Equal(ReminderState.Pending, db.Store.Get(overdue.Id)!.State);

        // Поиск не забирает запись: её по-прежнему доставляет обход.
        Assert.Single(db.Store.ClaimDue(Now));
    }

    [Fact]
    public void TotalIsReportedWhenPageIsTruncated()
    {
        using var db = new TempDb();
        BulkReminders.InsertPending(db.Store, 620, Now.AddHours(1), TimeSpan.FromMinutes(1));

        var first = Search(db, Query(), limit: 500);
        Assert.Equal(620, first.Total);
        Assert.Equal(500, first.Returned);
        Assert.True(first.Truncated);
        Assert.NotNull(first.Next);

        var second = Search(db, Query(), limit: 500, after: first.Next);
        Assert.Equal(620, second.Total);
        Assert.Equal(120, second.Returned);
        Assert.False(second.Truncated);
        Assert.Null(second.Next);
        Assert.Empty(first.Reminders.Select(r => r.Id).Intersect(second.Reminders.Select(r => r.Id)));
    }

    [Fact]
    public void FractionalBounds_AreCeiledToMilliseconds()
    {
        using var db = new TempDb();
        var at000 = db.Store.Create("000", new DateTimeOffset(2026, 11, 26, 10, 0, 0, 0, TimeSpan.Zero), Now);
        var at001 = db.Store.Create("001", new DateTimeOffset(2026, 11, 26, 10, 0, 0, 1, TimeSpan.Zero), Now);
        var at002 = db.Store.Create("002", new DateTimeOffset(2026, 11, 26, 10, 0, 0, 2, TimeSpan.Zero), Now);

        // from=.0005 не включает .000, но включает .001: дробная граница округлена вверх до .001.
        var from = ReminderSearchInput.Create(null, "2026-11-26T10:00:00.0005Z", null, false, null, null, null, out _);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(at001.DueUtc.ToUnixTimeMilliseconds()), from.FromUtc);
        var fromPage = Search(db, from);
        Assert.Equal(2, fromPage.Total);
        Assert.DoesNotContain(fromPage.Reminders, r => r.Id == at000.Id);
        Assert.Contains(fromPage.Reminders, r => r.Id == at001.Id);

        // to=.0015 включает .001 и исключает .002: без округления вверх .001 потерялся бы.
        var to = ReminderSearchInput.Create(null, null, "2026-11-26T10:00:00.0015Z", false, null, null, null, out _);
        var toPage = Search(db, to);
        Assert.Equal(2, toPage.Total);
        Assert.Contains(toPage.Reminders, r => r.Id == at000.Id);
        Assert.Contains(toPage.Reminders, r => r.Id == at001.Id);
        Assert.DoesNotContain(toPage.Reminders, r => r.Id == at002.Id);

        // Дробная граница с положительным смещением ведёт себя так же.
        var withOffset = ReminderSearchInput.Create(null, "2026-11-26T13:00:00.0005+03:00", null, false, null, null, null, out _);
        Assert.Equal(from.FromUtc, withOffset.FromUtc);
    }

    [Fact]
    public void PublicTime_KeepsRecordOffsetAndAddsCurrentZone()
    {
        using var db = new TempDb();
        var dueWithOffset = new DateTimeOffset(2027, 7, 1, 17, 0, 0, TimeSpan.FromHours(5));
        var reminder = db.Store.Create("зональное время", dueWithOffset, Now);

        Assert.Equal(TimeSpan.FromHours(5), reminder.DueLocal.Offset);
        Assert.EndsWith("+05:00", reminder.DueLocalIso);
        Assert.True(reminder.DueCurrentZoneLocal.Offset != TimeSpan.FromHours(5)
                    || TimeZoneInfo.Local.GetUtcOffset(reminder.DueUtc) == TimeSpan.FromHours(5));
        Assert.Equal(TimeZoneInfo.Local.GetUtcOffset(reminder.DueUtc), reminder.DueCurrentZoneLocal.Offset);
        Assert.Equal(reminder.DueUtc.UtcDateTime, reminder.DueCurrentZoneLocal.UtcDateTime);
    }

    [Fact]
    public void InvalidCriteria_AreRejectedWithClearErrors()
    {
        Assert.Throws<ReminderValidationException>(() =>
            ReminderSearchInput.Create(null, "2026-11-26T00:00:00", null, false, null, null, null, out _));
        Assert.Throws<ReminderValidationException>(() =>
            ReminderSearchInput.Create(null, "2026-11-27T00:00:00+03:00", "2026-11-26T00:00:00+03:00", false, null, null, null, out _));
        Assert.Throws<ReminderValidationException>(() =>
            ReminderSearchInput.Create(null, "2026-11-27T00:00:00+03:00", "2026-11-27T00:00:00+03:00", false, null, null, null, out _));
        Assert.Throws<ReminderValidationException>(() =>
            ReminderSearchInput.Create(null, null, null, false, null, 0, null, out _));
        Assert.Throws<ReminderValidationException>(() =>
            ReminderSearchInput.Create(null, null, null, false, null, 501, null, out _));
        Assert.Throws<ReminderValidationException>(() =>
            ReminderSearchInput.Create(null, null, null, false, "../../etc", null, null, out _));
        Assert.Throws<ReminderValidationException>(() =>
            ReminderSearchInput.Create(null, null, null, false, null, null, "не-курсор", out _));
        Assert.Throws<ReminderValidationException>(() => ReminderSearchInput.ParseLimit("abc"));
    }

    [Fact]
    public void Cursor_RoundTripsAndRejectsForeignPayload()
    {
        var encoded = ReminderCursor.Encode(1764151200000, "rem_0123456789abcdef");
        var decoded = ReminderCursor.Decode(encoded);
        Assert.Equal(1764151200000, decoded.DueMs);
        Assert.Equal("rem_0123456789abcdef", decoded.Id);

        var forged = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("123:../../x"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Throws<ReminderValidationException>(() => ReminderCursor.Decode(forged));
    }
}
