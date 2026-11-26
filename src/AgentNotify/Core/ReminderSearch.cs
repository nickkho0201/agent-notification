using System.Globalization;
using System.Text;

namespace AgentNotify.Core;

/// <summary>
/// Необязательные критерии поиска активных напоминаний (pending/sending). Критерии объединяются
/// по AND. Поиск только читает очередь: состояние записей и задачи Windows не меняются.
/// </summary>
/// <param name="Text">Литеральная подстрока текста, без учёта регистра Unicode; без морфологии.</param>
/// <param name="FromUtc">Нижняя граница срока включительно (сравнение по UTC).</param>
/// <param name="ToUtc">Верхняя граница срока исключительно (сравнение по UTC).</param>
/// <param name="Overdue">Только записи с наступившим сроком (due &lt;= текущего времени).</param>
/// <param name="Id">Точный идентификатор записи.</param>
public sealed record ReminderSearchQuery(
    string? Text,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    bool Overdue,
    string? Id)
{
    public bool IsEmpty => Text is null && FromUtc is null && ToUtc is null && !Overdue && Id is null;

    public bool MatchesText(string text) =>
        Text is null || text.Contains(Text, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Позиция в устойчивой сортировке (due_utc_ms, id) для следующей страницы.</summary>
public sealed record ReminderCursor(long DueMs, string Id)
{
    public static string Encode(long dueMs, string id)
    {
        var raw = dueMs.ToString(CultureInfo.InvariantCulture) + ":" + id;
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static ReminderCursor Decode(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ReminderValidationException("Курсор пуст.");
        var normalized = value.Trim().Replace('-', '+').Replace('_', '/');
        normalized = (normalized.Length % 4) switch
        {
            2 => normalized + "==",
            3 => normalized + "=",
            0 => normalized,
            _ => throw new ReminderValidationException("Некорректный курсор: передайте nextCursor из предыдущего ответа.")
        };
        string raw;
        try
        {
            raw = Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
        }
        catch (FormatException)
        {
            throw new ReminderValidationException("Некорректный курсор: передайте nextCursor из предыдущего ответа.");
        }
        var separator = raw.IndexOf(':');
        if (separator <= 0
            || !long.TryParse(raw[..separator], NumberStyles.Integer, CultureInfo.InvariantCulture, out var dueMs)
            || !ReminderInput.IsValidId(raw[(separator + 1)..]))
            throw new ReminderValidationException("Некорректный курсор: передайте nextCursor из предыдущего ответа.");
        return new ReminderCursor(dueMs, raw[(separator + 1)..]);
    }
}

/// <summary>Страница поиска: элементы, общее число совпадений и честный указатель на остаток.</summary>
public sealed record ReminderPage(
    IReadOnlyList<Reminder> Reminders,
    int Total,
    bool Truncated,
    ReminderCursor? Next)
{
    public int Returned => Reminders.Count;
}

/// <summary>
/// Разбор и проверка необязательных критериев поиска. Общий вход для MCP и CLI: одинаковые
/// сообщения об ошибках, никакой самодеятельной трактовки дат и идентификаторов.
/// </summary>
public static class ReminderSearchInput
{
    public const int DefaultLimit = 500;
    public const int MaxLimit = 500;

    public static ReminderSearchQuery Create(
        string? text,
        string? from,
        string? to,
        bool overdue,
        string? id,
        int? limit,
        string? cursor,
        out ReminderCursor? after)
    {
        after = null;
        var query = new ReminderSearchQuery(
            NormalizeText(text),
            ParseBoundary(from, "from"),
            ParseBoundary(to, "to"),
            overdue,
            string.IsNullOrWhiteSpace(id) ? null : ReminderInput.ValidateId(id));

        if (query.FromUtc is not null && query.ToUtc is not null && query.FromUtc >= query.ToUtc)
            throw new ReminderValidationException("Нижняя граница from должна быть раньше верхней границы to (to не включается).");

        var pageSize = limit ?? DefaultLimit;
        if (pageSize < 1 || pageSize > MaxLimit)
            throw new ReminderValidationException($"Параметр limit должен быть от 1 до {MaxLimit}.");

        if (!string.IsNullOrWhiteSpace(cursor))
            after = ReminderCursor.Decode(cursor);

        return query;
    }

    public static int ParseLimit(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DefaultLimit;
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit))
            throw new ReminderValidationException("Параметр limit должен быть целым числом.");
        if (limit < 1 || limit > MaxLimit)
            throw new ReminderValidationException($"Параметр limit должен быть от 1 до {MaxLimit}.");
        return limit;
    }

    /// <summary>
    /// Граница срока: только ISO 8601 с явным смещением или Z. Дата без смещения неоднозначна
    /// и отклоняется так же, как при создании напоминания. Сроки в очереди хранятся с точностью
    /// до миллисекунды, поэтому дробную границу округляем вверх: для целых миллисекунд это
    /// сохраняет ровно тот же отбор (from включительно, to исключительно), но не теряет записи
    /// из-за усечения.
    /// </summary>
    public static DateTimeOffset? ParseBoundary(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (!ReminderInput.HasExplicitOffset(trimmed))
            throw new ReminderValidationException($"Параметр {name} должен быть ISO 8601 с явным смещением, например 2026-11-26T00:00:00+03:00.");
        if (!DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            throw new ReminderValidationException($"Не удалось разобрать параметр {name}.");
        return CeilToMilliseconds(parsed.ToUniversalTime());
    }

    /// <summary>Округление вверх до миллисекунды (для дат до 1970 года — корректное ceil к нулю).</summary>
    private static DateTimeOffset CeilToMilliseconds(DateTimeOffset value)
    {
        var ticks = value.UtcTicks;
        var remainder = ((ticks % TimeSpan.TicksPerMillisecond) + TimeSpan.TicksPerMillisecond) % TimeSpan.TicksPerMillisecond;
        var ceilTicks = ticks - remainder + (remainder > 0 ? TimeSpan.TicksPerMillisecond : 0);
        return new DateTimeOffset(ceilTicks, TimeSpan.Zero);
    }

    public static string? NormalizeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var normalized = text.Trim();
        if (!ReminderInput.IsWellFormedUnicode(normalized))
            throw new ReminderValidationException("Текст поиска содержит некорректную суррогатную пару Unicode.");
        if (normalized.Length > ReminderInput.MaxTextLength)
            throw new ReminderValidationException($"Текст поиска длиннее {ReminderInput.MaxTextLength} символов.");
        return normalized;
    }
}
