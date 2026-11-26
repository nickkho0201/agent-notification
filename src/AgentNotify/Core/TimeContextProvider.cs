namespace AgentNotify.Core;

/// <summary>Достоверный локальный контекст времени для клиента (агента): зона, смещение, DST, ISO-время.</summary>
public sealed record TimeContext(
    string NowLocal,
    string NowUtc,
    string Offset,
    string WindowsTimeZoneId,
    string? IanaTimeZoneId,
    bool IsDaylightSavingTime,
    string TimeZoneDisplayName)
{
    public static TimeContext Current()
    {
        var now = DateTimeOffset.Now;
        var tz = TimeZoneInfo.Local;
        string? iana = null;
        try
        {
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(tz.Id, out var converted))
                iana = converted;
        }
        catch
        {
            // конвертация не критична
        }

        return new TimeContext(
            now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            now.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'"),
            FormatOffset(now.Offset),
            tz.Id,
            iana,
            tz.IsDaylightSavingTime(now),
            tz.DisplayName);
    }

    public static string FormatOffset(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var abs = offset.Duration();
        return $"{sign}{abs.Hours:00}:{abs.Minutes:00}";
    }
}
