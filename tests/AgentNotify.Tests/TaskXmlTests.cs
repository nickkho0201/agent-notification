using System.Xml.Linq;
using AgentNotify.Windows;
using Xunit;

namespace AgentNotify.Tests;

public class TaskXmlTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private const string Sid = "S-1-5-21-1-2-3-1001";
    private const string Exe = @"C:\Users\test\AppData\Local\AgentNotification\app\AgentNotify.exe";
    private static readonly TimeZoneInfo Msk = TimeZoneInfo.CreateCustomTimeZone("test-msk", TimeSpan.FromHours(3), "test", "test");

    [Fact]
    public void Reminder_UsesSystemLocalBoundaryAndWorkerArguments()
    {
        var due = new DateTimeOffset(2026, 12, 1, 6, 30, 0, TimeSpan.Zero); // 09:30 в UTC+3
        var xml = TaskXml.BuildReminder("AgentNotify.Reminder.rem_x", Exe, "run-due --home \"C:\\data dir\"", due, Sid, "тест", Msk);
        var doc = XDocument.Parse(xml);

        Assert.Equal("2026-12-01T09:30:00", doc.Descendants(Ns + "StartBoundary").Single().Value);
        Assert.Equal(Exe, doc.Descendants(Ns + "Command").Single().Value);
        Assert.Equal("run-due --home \"C:\\data dir\"", doc.Descendants(Ns + "Arguments").Single().Value);
        Assert.Equal(Sid, doc.Descendants(Ns + "UserId").Single().Value);
        Assert.Equal("InteractiveToken", doc.Descendants(Ns + "LogonType").Single().Value);
        Assert.Equal("LeastPrivilege", doc.Descendants(Ns + "RunLevel").Single().Value);
        Assert.Equal("IgnoreNew", doc.Descendants(Ns + "MultipleInstancesPolicy").Single().Value);
        Assert.Equal("true", doc.Descendants(Ns + "StartWhenAvailable").Single().Value);
        Assert.Equal("false", doc.Descendants(Ns + "DisallowStartIfOnBatteries").Single().Value);
        Assert.Equal("false", doc.Descendants(Ns + "StopIfGoingOnBatteries").Single().Value);
        Assert.Equal("false", doc.Descendants(Ns + "WakeToRun").Single().Value);
        Assert.Equal("тест", doc.Descendants(Ns + "Description").Single().Value);
    }

    [Fact]
    public void Boundary_IsNeverEarlierThanDue_AndCeilsFractionalSeconds()
    {
        var due = new DateTimeOffset(2026, 12, 1, 6, 30, 0, 400, TimeSpan.Zero);
        var boundary = TaskXml.FormatBoundary(due, Msk);
        Assert.Equal("2026-12-01T09:30:01", boundary);

        var parsed = DateTimeOffset.Parse("2026-12-01T09:30:01+03:00", System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(parsed >= due);
    }

    [Fact]
    public void Boundary_UsesInstantNotSourceOffset()
    {
        var due = new DateTimeOffset(2026, 12, 1, 9, 30, 0, TimeSpan.FromHours(5)); // 04:30Z
        Assert.Equal("2026-12-01T07:30:00", TaskXml.FormatBoundary(due, Msk));
    }

    [Fact]
    public void Boundary_HandlesDaylightSavingOffset()
    {
        var pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        var summer = new DateTimeOffset(2026, 7, 1, 16, 0, 0, TimeSpan.Zero); // 09:00 PDT
        Assert.Equal("2026-07-01T09:00:00", TaskXml.FormatBoundary(summer, pacific));
        var winter = new DateTimeOffset(2026, 1, 1, 17, 0, 0, TimeSpan.Zero); // 09:00 PST
        Assert.Equal("2026-01-01T09:00:00", TaskXml.FormatBoundary(winter, pacific));
    }

    [Fact]
    public void Sweep_HasLogonAndFiveMinuteRepetition()
    {
        var now = new DateTimeOffset(2026, 11, 26, 8, 0, 0, TimeSpan.Zero);
        var xml = TaskXml.BuildSweep("AgentNotify.Sweep", Exe, "run-due", now, Sid, "обход", zone: Msk);
        var doc = XDocument.Parse(xml);

        var logon = doc.Descendants(Ns + "LogonTrigger").Single();
        Assert.Equal(Sid, logon.Element(Ns + "UserId")!.Value);
        Assert.Equal("2026-11-26T11:00:00", doc.Descendants(Ns + "TimeTrigger").Single().Element(Ns + "StartBoundary")!.Value);
        Assert.Equal("PT5M", doc.Descendants(Ns + "Interval").Single().Value);
        Assert.Equal("false", doc.Descendants(Ns + "StopAtDurationEnd").Single().Value);
        Assert.Equal("IgnoreNew", doc.Descendants(Ns + "MultipleInstancesPolicy").Single().Value);
        Assert.Equal("PT10M", doc.Descendants(Ns + "ExecutionTimeLimit").Single().Value);
    }

    [Fact]
    public void Xml_IsSchemaOrderedAndEscaped()
    {
        var due = DateTimeOffset.UtcNow.AddHours(1);
        var xml = TaskXml.BuildReminder("AgentNotify.Reminder.rem_y", Exe, "run-due --home \"C:\\a&b\"", due, Sid, "описание <x> & \"y\"", Msk);
        var doc = XDocument.Parse(xml);
        var registration = doc.Root!.Element(Ns + "RegistrationInfo")!;
        var names = registration.Elements().Select(e => e.Name.LocalName).ToArray();
        Assert.Equal(new[] { "URI", "Author", "Description" }, names);
        Assert.Contains("&", doc.Descendants(Ns + "Arguments").Single().Value);
        Assert.Contains("<x>", doc.Descendants(Ns + "Description").Single().Value);
    }

    [Fact]
    public void TaskNames_IncludeUserSidScope()
    {
        var one = TaskNames.ScopeFor("S-1-5-21-1", @"C:\Users\a\AppData\Local\AgentNotification");
        var two = TaskNames.ScopeFor("S-1-5-21-2", @"C:\Users\a\AppData\Local\AgentNotification");
        Assert.NotEqual(one, two);
        Assert.StartsWith("AgentNotify.", one);
        Assert.DoesNotContain("S-1-5-21", one);

        var otherHome = TaskNames.ScopeFor("S-1-5-21-1", @"C:\Temp\isolated");
        Assert.NotEqual(one, otherHome);

        Assert.StartsWith(one + ".Reminder.rem_abc", TaskNames.ScopeFor("S-1-5-21-1", @"C:\Users\a\AppData\Local\AgentNotification") + ".Reminder.rem_abc");
    }

    [Fact]
    public void TaskNames_DefaultScopeMatchesCurrentUser()
    {
        Assert.StartsWith("AgentNotify.", TaskNames.Sweep);
        Assert.EndsWith(".Sweep", TaskNames.Sweep);
        Assert.Contains(".Reminder.rem_123", TaskNames.Reminder("rem_123"));
    }
}
