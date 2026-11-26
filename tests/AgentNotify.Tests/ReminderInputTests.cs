using AgentNotify.Core;
using Xunit;

namespace AgentNotify.Tests;

public class ReminderInputTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("2026-11-27T09:30:00+03:00", 3)]
    [InlineData("2026-11-27T06:30:00Z", 0)]
    [InlineData("2026-11-27T09:30+03:00", 3)]
    [InlineData("2026-11-27T01:30:00.123-05:00", -5)]
    public void ParseDue_AcceptOffsetForms(string at, int offsetHours)
    {
        var due = ReminderInput.ParseDue(at, Now);
        Assert.Equal(TimeSpan.FromHours(offsetHours), due.Offset);
        Assert.True(due > Now);
    }

    [Theory]
    [InlineData("2026-11-27T09:30:00")]      // нет смещения — неоднозначно
    [InlineData("2026-11-27 09:30:00+03:00")] // не ISO
    [InlineData("27.11.2026 09:30 +03:00")]
    [InlineData("завтра утром")]
    [InlineData("")]
    [InlineData(null)]
    public void ParseDue_RejectsAmbiguousOrInvalid(string? at)
    {
        Assert.Throws<ReminderValidationException>(() => ReminderInput.ParseDue(at, Now));
    }

    [Fact]
    public void ParseDue_RejectsPast()
    {
        Assert.Throws<ReminderValidationException>(() => ReminderInput.ParseDue("2026-11-26T08:59:59+00:00", Now));
    }

    [Fact]
    public void ParseDue_RejectsFarFuture()
    {
        Assert.Throws<ReminderValidationException>(() => ReminderInput.ParseDue("2040-01-01T00:00:00+00:00", Now));
    }

    [Fact]
    public void NormalizeText_KeepsUnicodeQuotesXmlAndNewlines()
    {
        var text = "Позвонить <Маше> & \"уточнить\" 'срок'\r\nвторая строка \t таб ✓";
        var normalized = ReminderInput.NormalizeText(text);
        Assert.Contains("<Маше>", normalized);
        Assert.Contains("& \"уточнить\" 'срок'", normalized);
        Assert.Contains("\nвторая строка", normalized);
        Assert.Contains("✓", normalized);
        Assert.DoesNotContain("\r", normalized);
    }

    [Fact]
    public void NormalizeText_RejectsEmptyControlAndTooLong()
    {
        Assert.Throws<ReminderValidationException>(() => ReminderInput.NormalizeText("   "));
        Assert.Throws<ReminderValidationException>(() => ReminderInput.NormalizeText("текст\u0000"));
        Assert.Throws<ReminderValidationException>(() => ReminderInput.NormalizeText("текст\u0007"));
        Assert.Throws<ReminderValidationException>(() => ReminderInput.NormalizeText(new string('я', ReminderInput.MaxTextLength + 1)));
    }

    [Fact]
    public void NormalizeText_RejectsBrokenSurrogatePair()
    {
        Assert.Throws<ReminderValidationException>(() => ReminderInput.NormalizeText("a\uD800b"));
        Assert.Throws<ReminderValidationException>(() => ReminderInput.NormalizeText("a\uDC00"));
    }

    [Fact]
    public void ValidateId_ChecksFormat()
    {
        Assert.Equal("rem_abc123", ReminderInput.ValidateId(" rem_abc123 "));
        Assert.Throws<ReminderValidationException>(() => ReminderInput.ValidateId("rem_ABC"));
        Assert.Throws<ReminderValidationException>(() => ReminderInput.ValidateId("../etc/passwd"));
        Assert.Throws<ReminderValidationException>(() => ReminderInput.ValidateId(null));
    }

    [Fact]
    public void TimeContext_ReportsLocalOffsetAndZone()
    {
        var context = TimeContext.Current();
        Assert.Matches(@"^[+-]\d{2}:\d{2}$", context.Offset);
        Assert.EndsWith("Z", context.NowUtc);
        Assert.False(string.IsNullOrWhiteSpace(context.WindowsTimeZoneId));
        Assert.Contains(context.Offset[..3], context.NowLocal);
    }
}
