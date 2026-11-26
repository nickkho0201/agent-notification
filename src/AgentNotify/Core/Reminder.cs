using System.Text.RegularExpressions;

namespace AgentNotify.Core;

public enum ReminderState
{
    Pending,
    Sending,
    Sent,
    Cancelled
}

public enum CancelStatus
{
    NotFound,
    Cancelled,
    AlreadySent,
    InProgress
}

/// <summary>Исход точечного изменения активной записи. Менять можно только <c>pending</c>.</summary>
public enum UpdateStatus
{
    /// <summary>Поля записаны; при смене срока в базе остаётся маркер синхронизации триггера.</summary>
    Updated,

    /// <summary>Переданные значения совпадают с текущими: запись и Планировщик не менялись.</summary>
    NoChange,

    NotFound,

    /// <summary>Запись уже доставляется, доставлена или отменена — изменение отклонено.</summary>
    NotPending
}

public static class ReminderStateNames
{
    public static string ToDb(this ReminderState state) => state switch
    {
        ReminderState.Pending => "pending",
        ReminderState.Sending => "sending",
        ReminderState.Sent => "sent",
        ReminderState.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    public static ReminderState FromDb(string value) => value switch
    {
        "pending" => ReminderState.Pending,
        "sending" => ReminderState.Sending,
        "sent" => ReminderState.Sent,
        "cancelled" => ReminderState.Cancelled,
        _ => throw new InvalidOperationException($"Неизвестное состояние в базе: {value}")
    };
}

public sealed class Reminder
{
    public required string Id { get; init; }
    public required string Text { get; init; }
    public required DateTimeOffset DueUtc { get; init; }
    public required TimeSpan Offset { get; init; }
    public required DateTimeOffset CreatedUtc { get; init; }
    public ReminderState State { get; set; }
    public int Attempts { get; set; }
    public string? ClaimToken { get; set; }
    public DateTimeOffset? ClaimedUtc { get; set; }
    public DateTimeOffset? SentUtc { get; set; }
    public string? LastError { get; set; }
    public string? TaskName { get; set; }

    /// <summary>Момент последнего изменения записи (аудит и сверка версии при синхронизации).</summary>
    public DateTimeOffset? UpdatedUtc { get; set; }

    /// <summary>
    /// Срок, для которого точный триггер Планировщика ещё не подтверждён. Маркер пишется в той же
    /// транзакции, что и новый срок, поэтому падение между SQLite и Планировщиком не теряет факт
    /// необходимой перерегистрации: обход повторит её с тем же именем задачи.
    /// </summary>
    public DateTimeOffset? SyncDueUtc { get; set; }

    public DateTimeOffset DueLocal => DueUtc.ToOffset(Offset);
    public string DueLocalIso => DueLocal.ToString("yyyy-MM-ddTHH:mm:sszzz");
    public string DueUtcIso => DueUtc.ToString("yyyy-MM-ddTHH:mm:ss'Z'");

    /// <summary>
    /// Тот же срок в текущей зоне Windows с учётом DST на дату срока. <see cref="DueLocal"/>
    /// исторически хранит смещение, с которым запись создана, и не обязан совпадать с зоной ПК.
    /// </summary>
    public DateTimeOffset DueCurrentZoneLocal => TimeZoneInfo.ConvertTime(DueUtc, TimeZoneInfo.Local);

    public string DueCurrentZoneLocalIso => DueCurrentZoneLocal.ToString("yyyy-MM-ddTHH:mm:sszzz");

    public object ToPublicObject() => new
    {
        id = Id,
        text = Text,
        dueLocal = DueLocalIso,
        dueCurrentZoneLocal = DueCurrentZoneLocalIso,
        dueUtc = DueUtcIso,
        offset = TimeContext.FormatOffset(Offset),
        state = State.ToDb(),
        attempts = Attempts,
        sentUtc = SentUtc?.ToString("yyyy-MM-ddTHH:mm:ss'Z'"),
        lastError = LastError,
        windowsTask = TaskName
    };
}

public sealed class ReminderValidationException : Exception
{
    public ReminderValidationException(string message) : base(message) { }
}

/// <summary>Разбор и проверка пользовательского ввода. Время принимается только как ISO 8601 с явным смещением.</summary>
public static class ReminderInput
{
    public const int MaxTextLength = 1000;
    public static readonly TimeSpan MaxHorizon = TimeSpan.FromDays(3650);

    private static readonly Regex IsoWithOffset = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2})?(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex IdPattern = new(@"^rem_[a-z0-9]{6,32}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>true, если строка — ISO 8601 с явным смещением или Z (без смещения неоднозначна).</summary>
    public static bool HasExplicitOffset(string value) => IsoWithOffset.IsMatch(value);

    public static bool IsValidId(string value) => IdPattern.IsMatch(value);

    public static DateTimeOffset ParseDue(string? at, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(at))
            throw new ReminderValidationException("Не задано время. Нужен ISO 8601 с явным смещением, например 2026-11-27T09:30:00+03:00.");
        var value = at.Trim();
        if (!HasExplicitOffset(value))
            throw new ReminderValidationException("Время должно быть ISO 8601 с обязательным смещением или Z, например 2026-11-27T09:30:00+03:00. Время без смещения неоднозначно.");
        if (!DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var due))
            throw new ReminderValidationException("Не удалось разобрать время.");

        if (due <= now)
            throw new ReminderValidationException($"Время уже прошло ({due:yyyy-MM-ddTHH:mm:sszzz}); напоминание можно ставить только на будущее.");
        if (due - now > MaxHorizon)
            throw new ReminderValidationException("Слишком далёкая дата: поддерживается не более 10 лет вперёд.");
        return due;
    }

    public static string NormalizeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ReminderValidationException("Текст напоминания пуст.");
        var normalized = text.Replace("\r\n", "\n").Trim();
        if (normalized.Length == 0)
            throw new ReminderValidationException("Текст напоминания пуст.");

        var count = 0;
        for (var i = 0; i < normalized.Length; i++)
        {
            var ch = normalized[i];
            if (char.IsHighSurrogate(ch))
            {
                if (i + 1 >= normalized.Length || !char.IsLowSurrogate(normalized[i + 1]))
                    throw new ReminderValidationException("Текст содержит некорректную суррогатную пару Unicode.");
                i++;
                count++;
                continue;
            }
            if (char.IsLowSurrogate(ch))
                throw new ReminderValidationException("Текст содержит некорректную суррогатную пару Unicode.");
            if (ch != '\t' && ch != '\n' && ch != '\r' && (char.IsControl(ch) || ch == '\u0000'))
                throw new ReminderValidationException("Текст содержит управляющие символы, которые не поддерживаются.");
            count++;
        }

        if (count > MaxTextLength)
            throw new ReminderValidationException($"Текст длиннее {MaxTextLength} символов.");
        return normalized;
    }

    /// <summary>
    /// Проверка корректности суррогатных пар Unicode без ограничений на длину и управляющие
    /// символы: нужна для текста поиска, где переводы строк не создают напоминание.
    /// </summary>
    public static bool IsWellFormedUnicode(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (char.IsHighSurrogate(ch))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1])) return false;
                i++;
                continue;
            }
            if (char.IsLowSurrogate(ch)) return false;
        }
        return true;
    }

    public static string ValidateId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || !IsValidId(id.Trim()))
            throw new ReminderValidationException("Некорректный идентификатор напоминания (ожидается вид rem_xxxxxxxx).");
        return id.Trim();
    }
}
