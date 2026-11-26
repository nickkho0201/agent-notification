using System.Text.Json.Nodes;
using AgentNotify.Core;
using AgentNotify.Mcp;
using AgentNotify.Windows;
using Xunit;

namespace AgentNotify.Tests;

public class McpServerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);

    private static (McpServer Server, FakeRegistrar Registrar, FakePresenter Presenter) Build(TempDb db)
    {
        var registrar = new FakeRegistrar();
        var presenter = new FakePresenter();
        var service = new ReminderService(db.Store, registrar, presenter, () => Now, @"C:\fake\AgentNotify.exe");
        return (new McpServer(service), registrar, presenter);
    }

    private static JsonObject Call(McpServer server, string method, JsonObject? parameters = null)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = method,
            ["params"] = parameters ?? new JsonObject()
        };
        var response = server.HandleMessage(request.ToJsonString());
        Assert.NotNull(response);
        return JsonNode.Parse(response!)!.AsObject();
    }

    private static JsonObject Tool(McpServer server, string name, JsonObject arguments)
    {
        var response = Call(server, "tools/call", new JsonObject { ["name"] = name, ["arguments"] = arguments });
        return response["result"]!.AsObject();
    }

    [Fact]
    public void Initialize_ReportsToolsCapabilityAndInstructions()
    {
        using var db = new TempDb();
        var (server, _, _) = Build(db);

        var response = Call(server, "initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-06-18",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "test", ["version"] = "1" }
        });

        var result = response["result"]!.AsObject();
        Assert.Equal("2025-06-18", result["protocolVersion"]!.GetValue<string>());
        Assert.NotNull(result["capabilities"]!["tools"]);
        Assert.Equal("agent-notification", result["serverInfo"]!["name"]!.GetValue<string>());
        Assert.Contains("list_reminders", result["instructions"]!.GetValue<string>());
        var instructions = result["instructions"]!.GetValue<string>();
        Assert.Contains("overdue", instructions);
        Assert.Contains("nextCursor", instructions);
        Assert.Contains("литеральная подстрока", instructions);
        Assert.Contains("«сегодня»", instructions);
        Assert.Contains("update_reminder", instructions);
        Assert.Contains("syncPending", instructions);
    }

    [Fact]
    public void Initialize_NegotiatesUnknownProtocolVersion()
    {
        using var db = new TempDb();
        var (server, _, _) = Build(db);
        var response = Call(server, "initialize", new JsonObject { ["protocolVersion"] = "1999-01-01" });
        Assert.Equal("2025-06-18", response["result"]!["protocolVersion"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsList_ExactlyFourStrictTools()
    {
        using var db = new TempDb();
        var (server, _, _) = Build(db);

        var tools = Call(server, "tools/list")["result"]!["tools"]!.AsArray();
        var names = tools.Select(t => t!["name"]!.GetValue<string>()).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "cancel_reminder", "list_reminders", "schedule_reminder", "update_reminder" }, names);
        foreach (var tool in tools)
        {
            var schema = tool!["inputSchema"]!.AsObject();
            Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        }

        var update = tools.Single(t => t!["name"]!.GetValue<string>() == "update_reminder")!["inputSchema"]!.AsObject();
        Assert.Equal("[\"id\"]", update["required"]!.ToJsonString());
        var properties = update["properties"]!.AsObject().Select(p => p.Key).OrderBy(k => k).ToArray();
        Assert.Equal(new[] { "at", "id", "text" }, properties);
    }

    [Fact]
    public void Schedule_ThenList_ThenCancel()
    {
        using var db = new TempDb();
        var (server, registrar, _) = Build(db);

        var created = Tool(server, "schedule_reminder", new JsonObject
        {
            ["at"] = "2026-11-27T09:30:00+03:00",
            ["text"] = "проверить <данные> & \"кавычки\"\nвторая строка"
        });
        Assert.False(created["isError"]!.GetValue<bool>());
        var structured = created["structuredContent"]!.AsObject();
        var id = structured["id"]!.GetValue<string>();
        Assert.StartsWith("rem_", id);
        Assert.True(structured["windowsTaskRegistered"]!.GetValue<bool>());
        Assert.Contains("вторая строка", structured["text"]!.GetValue<string>());
        Assert.True(registrar.Exists(TaskNames.Reminder(id)));

        var listed = Tool(server, "list_reminders", new JsonObject())["structuredContent"]!.AsObject();
        Assert.Single(listed["reminders"]!.AsArray());
        Assert.Equal(id, listed["reminders"]![0]!["id"]!.GetValue<string>());
        var context = listed["timeContext"]!.AsObject();
        Assert.Matches(@"^[+-]\d{2}:\d{2}$", context["offset"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(context["timeZoneWindows"]!.GetValue<string>()));

        var cancelled = Tool(server, "cancel_reminder", new JsonObject { ["id"] = id })["structuredContent"]!.AsObject();
        Assert.True(cancelled["cancelled"]!.GetValue<bool>());
        Assert.True(cancelled["windowsTaskRemoved"]!.GetValue<bool>());

        var after = Tool(server, "list_reminders", new JsonObject())["structuredContent"]!.AsObject();
        Assert.Empty(after["reminders"]!.AsArray());
    }

    [Fact]
    public void Schedule_RejectsTimeWithoutOffset()
    {
        using var db = new TempDb();
        var (server, _, _) = Build(db);
        var result = Tool(server, "schedule_reminder", new JsonObject { ["at"] = "2026-11-27T09:30:00", ["text"] = "x" });
        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Contains("смещени", result["content"]![0]!["text"]!.GetValue<string>());
        Assert.Empty(db.Store.List(includeFinished: true));
    }

    [Fact]
    public void Schedule_RejectsUnknownArguments()
    {
        using var db = new TempDb();
        var (server, _, _) = Build(db);
        var result = Tool(server, "schedule_reminder", new JsonObject
        {
            ["at"] = "2026-11-27T09:30:00+03:00",
            ["text"] = "x",
            ["command"] = "calc.exe",
            ["cwd"] = "C:\\"
        });
        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Empty(db.Store.List(includeFinished: true));
    }

    [Fact]
    public void Schedule_RejectsNonStringAndPast()
    {
        using var db = new TempDb();
        var (server, _, _) = Build(db);
        var nonString = Tool(server, "schedule_reminder", new JsonObject { ["at"] = 123, ["text"] = "x" });
        Assert.True(nonString["isError"]!.GetValue<bool>());
        var past = Tool(server, "schedule_reminder", new JsonObject { ["at"] = "2020-01-01T00:00:00+00:00", ["text"] = "x" });
        Assert.True(past["isError"]!.GetValue<bool>());
    }

    [Fact]
    public void Cancel_UnknownAndMalformedId()
    {
        using var db = new TempDb();
        var (server, _, _) = Build(db);
        Assert.True(Tool(server, "cancel_reminder", new JsonObject { ["id"] = "rem_0000000000000000" })["isError"]!.GetValue<bool>());
        Assert.True(Tool(server, "cancel_reminder", new JsonObject { ["id"] = "../../x" })["isError"]!.GetValue<bool>());
    }

    [Fact]
    public void NotificationsGetNoResponse_AndUnknownMethodErrors()
    {
        using var db = new TempDb();
        var (server, _, _) = Build(db);
        Assert.Null(server.HandleMessage("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));
        var unknown = Call(server, "resources/list");
        Assert.Equal(-32601, unknown["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void ParseError_ReturnsJsonRpcError()
    {
        using var db = new TempDb();
        var (server, _, _) = Build(db);
        var response = JsonNode.Parse(server.HandleMessage("{not json")!)!.AsObject();
        Assert.Equal(-32700, response["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void ToolCall_UnknownToolRejected()
    {
        using var db = new TempDb();
        var (server, _, _) = Build(db);
        var response = Call(server, "tools/call", new JsonObject { ["name"] = "execute_shell", ["arguments"] = new JsonObject() });
        Assert.Equal(-32602, response["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void Cancel_AlreadyDeliveredIsErrorNotSuccess()
    {
        using var db = new TempDb();
        var (server, _, _) = Build(db);
        var created = Tool(server, "schedule_reminder", new JsonObject
        {
            ["at"] = "2026-11-27T09:30:00+03:00",
            ["text"] = "уже ушло"
        })["structuredContent"]!.AsObject();
        var id = created["id"]!.GetValue<string>();

        var claimed = db.Store.ClaimDue(Now.AddHours(30)).Single();
        Assert.Equal(id, claimed.Id);
        Assert.True(db.Store.MarkSent(claimed.Id, claimed.ClaimToken!, Now.AddHours(31)));

        var result = Tool(server, "cancel_reminder", new JsonObject { ["id"] = id });
        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Contains("уже доставлено", result["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(ReminderState.Sent, db.Store.Get(id)!.State);
    }

    [Fact]
    public void Schedule_WithoutExactTriggerReportsSweepStateHonestly()
    {
        using var db = new TempDb();
        var (server, registrar, _) = Build(db);
        registrar.FailRegistration = true;

        var result = Tool(server, "schedule_reminder", new JsonObject
        {
            ["at"] = "2026-11-27T09:30:00+03:00",
            ["text"] = "без триггера"
        });
        var structured = result["structuredContent"]!.AsObject();
        Assert.False(structured["windowsTaskRegistered"]!.GetValue<bool>());
        Assert.False(structured["sweepTaskRegistered"]!.GetValue<bool>());
        var warnings = structured["warnings"]!.AsArray().Select(w => w!.GetValue<string>()).ToArray();
        Assert.Contains(warnings, w => w.Contains("НЕ найдена"));
        Assert.DoesNotContain(warnings, w => w.Contains("будет доставлена"));
    }

    [Fact]
    public void List_ReportsSweepPresence()
    {
        using var db = new TempDb();
        var (server, registrar, _) = Build(db);
        var before = Tool(server, "list_reminders", new JsonObject())["structuredContent"]!.AsObject();
        Assert.False(before["sweepTaskRegistered"]!.GetValue<bool>());

        registrar.RegisterSweep(TaskNames.Sweep, @"C:\fake\AgentNotify.exe", "run-due", Now);
        var after = Tool(server, "list_reminders", new JsonObject())["structuredContent"]!.AsObject();
        Assert.True(after["sweepTaskRegistered"]!.GetValue<bool>());
    }

    [Fact]
    public void List_ReturnsNoExecutableSurface()
    {
        using var db = new TempDb();
        var (server, _, _) = Build(db);
        var result = Tool(server, "list_reminders", new JsonObject())["structuredContent"]!.AsObject();
        var json = result.ToJsonString();
        Assert.DoesNotContain("command", json);
        Assert.DoesNotContain("path", json);
    }

    [Fact]
    public void List_NoArgumentsKeepsCompatibleShapeAndPaginationFields()
    {
        using var db = new TempDb();
        var (server, _, _) = Build(db);
        db.Store.Create("первое", Now.AddHours(1), Now);
        db.Store.Create("второе", Now.AddHours(2), Now);

        var payload = Tool(server, "list_reminders", new JsonObject())["structuredContent"]!.AsObject();
        Assert.Equal(2, payload["reminders"]!.AsArray().Count);
        Assert.Equal(2, payload["returned"]!.GetValue<int>());
        Assert.Equal(2, payload["total"]!.GetValue<int>());
        Assert.False(payload["truncated"]!.GetValue<bool>());
        Assert.Null(payload["nextCursor"]);
        var counts = payload["counts"]!.AsObject();
        Assert.Equal(2, counts["pending"]!.GetValue<int>());
        Assert.Equal(0, counts["sending"]!.GetValue<int>());
        Assert.Equal(0, counts["sent"]!.GetValue<int>());
        Assert.Equal(0, counts["cancelled"]!.GetValue<int>());
        Assert.NotNull(payload["timeContext"]!["timeZoneWindows"]);
        Assert.NotNull(payload["sweepTaskRegistered"]);
    }

    [Fact]
    public void List_SearchByTextIdOverdueAndAndCombination()
    {
        using var db = new TempDb();
        var (server, _, _) = Build(db);
        var paid = db.Store.Create("Оплата счёта", Now.AddHours(1), Now);
        var call = db.Store.Create("позвонить в сервис", Now.AddHours(2), Now);
        var overdue = db.Store.Create("оплата просрочена", Now.AddMinutes(-5), Now.AddHours(-1));

        var byText = Tool(server, "list_reminders", new JsonObject { ["text"] = "ОПЛАТ" })["structuredContent"]!.AsObject();
        Assert.Equal(2, byText["total"]!.GetValue<int>());
        Assert.Equal("ОПЛАТ", byText["appliedFilters"]!["text"]!.GetValue<string>());

        var byId = Tool(server, "list_reminders", new JsonObject { ["id"] = call.Id })["structuredContent"]!.AsObject();
        Assert.Single(byId["reminders"]!.AsArray());
        Assert.Equal(call.Id, byId["reminders"]![0]!["id"]!.GetValue<string>());

        var byOverdue = Tool(server, "list_reminders", new JsonObject { ["overdue"] = true })["structuredContent"]!.AsObject();
        Assert.Single(byOverdue["reminders"]!.AsArray());
        Assert.Equal(overdue.Id, byOverdue["reminders"]![0]!["id"]!.GetValue<string>());

        var combined = Tool(server, "list_reminders", new JsonObject
        {
            ["text"] = "оплат",
            ["from"] = Now.AddHours(-2).ToString("yyyy-MM-ddTHH:mm:sszzz"),
            ["to"] = Now.AddHours(2).ToString("yyyy-MM-ddTHH:mm:sszzz"),
            ["overdue"] = true
        })["structuredContent"]!.AsObject();
        Assert.Single(combined["reminders"]!.AsArray());
        Assert.Equal(overdue.Id, combined["reminders"]![0]!["id"]!.GetValue<string>());
        Assert.Equal(paid.Id, db.Store.Get(paid.Id)!.Id);
    }

    [Fact]
    public void List_EmptySearchIsNotAnError()
    {
        using var db = new TempDb();
        var (server, _, _) = Build(db);
        db.Store.Create("звонок", Now.AddHours(1), Now);

        var result = Tool(server, "list_reminders", new JsonObject { ["text"] = "нет такого" });
        Assert.False(result["isError"]!.GetValue<bool>());
        var payload = result["structuredContent"]!.AsObject();
        Assert.Empty(payload["reminders"]!.AsArray());
        Assert.Equal(0, payload["total"]!.GetValue<int>());
        Assert.False(payload["truncated"]!.GetValue<bool>());
        Assert.Null(payload["nextCursor"]);
    }

    [Fact]
    public void List_RejectsInvalidFiltersWithoutChangingQueue()
    {
        using var db = new TempDb();
        var (server, _, _) = Build(db);
        var created = db.Store.Create("не менять", Now.AddHours(1), Now);

        Assert.True(Tool(server, "list_reminders", new JsonObject { ["from"] = "2026-11-26T00:00:00" })["isError"]!.GetValue<bool>());
        Assert.True(Tool(server, "list_reminders", new JsonObject
        {
            ["from"] = "2026-11-27T00:00:00+03:00",
            ["to"] = "2026-11-26T00:00:00+03:00"
        })["isError"]!.GetValue<bool>());
        Assert.True(Tool(server, "list_reminders", new JsonObject { ["limit"] = 0 })["isError"]!.GetValue<bool>());
        Assert.True(Tool(server, "list_reminders", new JsonObject { ["limit"] = 501 })["isError"]!.GetValue<bool>());
        Assert.True(Tool(server, "list_reminders", new JsonObject { ["limit"] = "10" })["isError"]!.GetValue<bool>());
        Assert.True(Tool(server, "list_reminders", new JsonObject { ["overdue"] = "yes" })["isError"]!.GetValue<bool>());
        Assert.True(Tool(server, "list_reminders", new JsonObject { ["id"] = "not-an-id" })["isError"]!.GetValue<bool>());
        Assert.True(Tool(server, "list_reminders", new JsonObject { ["cursor"] = "не-курсор" })["isError"]!.GetValue<bool>());
        Assert.True(Tool(server, "list_reminders", new JsonObject { ["command"] = "calc.exe" })["isError"]!.GetValue<bool>());

        var stored = db.Store.Get(created.Id)!;
        Assert.Equal(ReminderState.Pending, stored.State);
        Assert.Equal("не менять", stored.Text);
    }

    [Fact]
    public void List_PaginatesWithCursorWithoutLoss()
    {
        using var db = new TempDb();
        var (server, _, _) = Build(db);
        BulkReminders.InsertPending(db.Store, 620, Now.AddHours(1), TimeSpan.FromMinutes(1));

        var first = Tool(server, "list_reminders", new JsonObject { ["limit"] = 500 })["structuredContent"]!.AsObject();
        Assert.Equal(500, first["reminders"]!.AsArray().Count);
        Assert.Equal(620, first["total"]!.GetValue<int>());
        Assert.True(first["truncated"]!.GetValue<bool>());
        var cursor = first["nextCursor"]!.GetValue<string>();

        var second = Tool(server, "list_reminders", new JsonObject { ["limit"] = 500, ["cursor"] = cursor })["structuredContent"]!.AsObject();
        Assert.Equal(120, second["reminders"]!.AsArray().Count);
        Assert.False(second["truncated"]!.GetValue<bool>());
        Assert.Null(second["nextCursor"]);

        var firstIds = first["reminders"]!.AsArray().Select(r => r!["id"]!.GetValue<string>()).ToHashSet();
        Assert.All(second["reminders"]!.AsArray(), r => Assert.DoesNotContain(r!["id"]!.GetValue<string>(), firstIds));
    }

    [Fact]
    public void List_DueCurrentZoneLocalUsesCurrentZoneNotRecordOffset()
    {
        using var db = new TempDb();
        var (server, _, _) = Build(db);
        var dueUtc = new DateTimeOffset(2027, 7, 1, 12, 0, 0, TimeSpan.Zero);
        db.Store.Create("зональное время", dueUtc.ToOffset(TimeSpan.FromHours(5)), Now);

        var payload = Tool(server, "list_reminders", new JsonObject())["structuredContent"]!.AsObject();
        var reminder = payload["reminders"]![0]!.AsObject();
        Assert.EndsWith("+05:00", reminder["dueLocal"]!.GetValue<string>());

        var currentZone = DateTimeOffset.Parse(reminder["dueCurrentZoneLocal"]!.GetValue<string>(),
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(TimeZoneInfo.Local.GetUtcOffset(dueUtc), currentZone.Offset);
        Assert.Equal(dueUtc.UtcDateTime, currentZone.UtcDateTime);
        Assert.Equal(dueUtc, DateTimeOffset.Parse(reminder["dueUtc"]!.GetValue<string>(),
            System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime());
    }

    [Fact]
    public void Update_TextThenDueReportsHonestFields()
    {
        using var db = new TempDb();
        var (server, registrar, _) = Build(db);
        var created = Tool(server, "schedule_reminder", new JsonObject
        {
            ["at"] = "2026-11-27T09:30:00+03:00",
            ["text"] = "исходный текст"
        })["structuredContent"]!.AsObject();
        var id = created["id"]!.GetValue<string>();
        var originalDue = created["dueUtc"]!.GetValue<string>();

        var textOnly = Tool(server, "update_reminder", new JsonObject { ["id"] = id, ["text"] = "новый текст" });
        Assert.False(textOnly["isError"]!.GetValue<bool>());
        var textPayload = textOnly["structuredContent"]!.AsObject();
        Assert.True(textPayload["changed"]!.GetValue<bool>());
        Assert.True(textPayload["textChanged"]!.GetValue<bool>());
        Assert.False(textPayload["dueChanged"]!.GetValue<bool>());
        Assert.Equal(originalDue, textPayload["dueUtc"]!.GetValue<string>());
        Assert.True(textPayload["windowsTaskRegistered"]!.GetValue<bool>());
        Assert.False(textPayload["syncPending"]!.GetValue<bool>());
        Assert.Single(registrar.Registered);

        var moved = Tool(server, "update_reminder", new JsonObject { ["id"] = id, ["at"] = "2026-11-27T11:45:00+03:00" });
        Assert.False(moved["isError"]!.GetValue<bool>());
        var movedPayload = moved["structuredContent"]!.AsObject();
        Assert.True(movedPayload["dueChanged"]!.GetValue<bool>());
        Assert.False(movedPayload["textChanged"]!.GetValue<bool>());
        Assert.Equal("2026-11-27T08:45:00Z", movedPayload["dueUtc"]!.GetValue<string>());
        Assert.Equal("новый текст", movedPayload["text"]!.GetValue<string>());
        Assert.Equal(2, registrar.Registered.Count);
        Assert.All(registrar.Registered, name => Assert.Equal(TaskNames.Reminder(id), name));

        var noop = Tool(server, "update_reminder", new JsonObject { ["id"] = id, ["at"] = "2026-11-27T11:45:00+03:00", ["text"] = "новый текст" });
        Assert.False(noop["structuredContent"]!["changed"]!.GetValue<bool>());
        Assert.Equal(2, registrar.Registered.Count);
    }

    [Fact]
    public void Update_RejectsMissingFieldsUnknownIdAndExtraArguments()
    {
        using var db = new TempDb();
        var (server, _, _) = Build(db);
        var created = Tool(server, "schedule_reminder", new JsonObject
        {
            ["at"] = "2026-11-27T09:30:00+03:00",
            ["text"] = "текст"
        })["structuredContent"]!.AsObject();
        var id = created["id"]!.GetValue<string>();

        Assert.True(Tool(server, "update_reminder", new JsonObject { ["id"] = id })["isError"]!.GetValue<bool>());
        Assert.True(Tool(server, "update_reminder", new JsonObject { ["id"] = id, ["at"] = "2026-11-27T09:30:00" })["isError"]!.GetValue<bool>());
        Assert.True(Tool(server, "update_reminder", new JsonObject { ["id"] = id, ["text"] = "x", ["due"] = "x" })["isError"]!.GetValue<bool>());
        Assert.True(Tool(server, "update_reminder", new JsonObject { ["id"] = "rem_0000000000000000", ["text"] = "x" })["isError"]!.GetValue<bool>());
        Assert.True(Tool(server, "update_reminder", new JsonObject { ["id"] = id, ["at"] = 123 })["isError"]!.GetValue<bool>());
        // Явно пустой at — ошибка, даже если text передан.
        Assert.True(Tool(server, "update_reminder", new JsonObject { ["id"] = id, ["at"] = "", ["text"] = "x" })["isError"]!.GetValue<bool>());
        // Явный JSON null не означает «поле не передано».
        var nullAt = JsonNode.Parse("{\"id\":\"" + id + "\",\"at\":null,\"text\":\"x\"}")!.AsObject();
        Assert.True(Tool(server, "update_reminder", nullAt)["isError"]!.GetValue<bool>());
        var nullText = JsonNode.Parse("{\"id\":\"" + id + "\",\"at\":\"2026-11-27T09:30:00+03:00\",\"text\":null}")!.AsObject();
        Assert.True(Tool(server, "update_reminder", nullText)["isError"]!.GetValue<bool>());

        var stored = db.Store.Get(id)!;
        Assert.Equal("текст", stored.Text);
        Assert.Equal(new DateTimeOffset(2026, 11, 27, 6, 30, 0, TimeSpan.Zero), stored.DueUtc);
    }

    [Fact]
    public void Update_CancelledIsErrorNotSuccess()
    {
        using var db = new TempDb();
        var (server, _, _) = Build(db);
        var created = Tool(server, "schedule_reminder", new JsonObject
        {
            ["at"] = "2026-11-27T09:30:00+03:00",
            ["text"] = "текст"
        })["structuredContent"]!.AsObject();
        var id = created["id"]!.GetValue<string>();
        Tool(server, "cancel_reminder", new JsonObject { ["id"] = id });

        var update = Tool(server, "update_reminder", new JsonObject { ["id"] = id, ["text"] = "нельзя" });

        Assert.True(update["isError"]!.GetValue<bool>());
        Assert.Contains("отменено", update["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("текст", db.Store.Get(id)!.Text);
    }
}


