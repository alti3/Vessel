using Vessel.Domain.Common;

namespace Vessel.Domain.ValueObjects;

/// <summary>
///     A five-field cron expression (minute hour day-of-month month day-of-week) evaluated in a time zone. Supports
///     <c>*</c>, lists, ranges, steps, month and weekday names, <c>?</c>, the macros <c>@yearly</c>, <c>@annually</c>,
///     <c>@monthly</c>, <c>@weekly</c>, <c>@daily</c>, <c>@midnight</c>, <c>@hourly</c> and the Coolify frequency names
///     (<c>every_minute</c>, <c>hourly</c>, <c>daily</c>, <c>weekly</c>, <c>monthly</c>, <c>yearly</c>). A six-field
///     expression is accepted only when its leading seconds field is <c>0</c> (Hangfire style), because schedules are
///     dispatched with minute granularity. When both day-of-month and day-of-week are restricted a day matches either
///     (standard cron semantics).
///     <para>
///         Daylight saving time: a local time skipped by a forward transition runs once at the first valid instant
///         after the gap; a local time repeated by a backward transition runs once, at its first occurrence
///         (upstream Coolify v4.4.0 "the repeated hour runs once").
///     </para>
/// </summary>
public sealed class CronSchedule
{
    public const int MaxLength = 120;

    private static readonly string[] MonthNames =
        ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

    private static readonly string[] DayNames = ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];

    private readonly bool[] _daysOfMonth;
    private readonly bool[] _daysOfWeek;
    private readonly bool _dayOfMonthRestricted;
    private readonly bool _dayOfWeekRestricted;
    private readonly bool[] _hours;
    private readonly bool[] _minutes;
    private readonly bool[] _months;

    private CronSchedule(string expression, bool[] minutes, bool[] hours, bool[] daysOfMonth, bool[] months,
        bool[] daysOfWeek, bool dayOfMonthRestricted, bool dayOfWeekRestricted)
    {
        Expression = expression;
        _minutes = minutes;
        _hours = hours;
        _daysOfMonth = daysOfMonth;
        _months = months;
        _daysOfWeek = daysOfWeek;
        _dayOfMonthRestricted = dayOfMonthRestricted;
        _dayOfWeekRestricted = dayOfWeekRestricted;
    }

    /// <summary>The expression as written (trimmed).</summary>
    public string Expression { get; }

    public static CronSchedule Parse(string expression)
    {
        return TryParse(expression, out CronSchedule? schedule, out var error)
            ? schedule!
            : throw new DomainException(error!);
    }

    public static bool IsValid(string? expression)
    {
        return TryParse(expression, out _, out _);
    }

    public static bool TryParse(string? expression, out CronSchedule? schedule, out string? error)
    {
        schedule = null;
        error = null;
        var trimmed = expression?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            error = "Cron expression is required.";
            return false;
        }

        if (trimmed.Length > MaxLength)
        {
            error = $"Cron expression must be at most {MaxLength} characters.";
            return false;
        }

        var normalized = Expand(trimmed);
        var fields = normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length == 6)
        {
            if (fields[0] != "0")
            {
                error = "Cron schedules run with minute granularity; a seconds field must be 0.";
                return false;
            }

            fields = fields[1..];
        }

        if (fields.Length != 5)
        {
            error = "Cron expression must have five fields: minute hour day-of-month month day-of-week.";
            return false;
        }

        try
        {
            bool[] minutes = ParseField(fields[0], 0, 59, null, "minute");
            bool[] hours = ParseField(fields[1], 0, 23, null, "hour");
            bool[] daysOfMonth = ParseField(fields[2], 1, 31, null, "day-of-month");
            bool[] months = ParseField(fields[3], 1, 12, MonthNames, "month");
            bool[] daysOfWeek = ParseField(fields[4], 0, 7, DayNames, "day-of-week");
            // 7 is Sunday as well.
            if (daysOfWeek[7]) daysOfWeek[0] = true;
            schedule = new CronSchedule(trimmed, minutes, hours, daysOfMonth, months, daysOfWeek,
                !IsWildcard(fields[2]), !IsWildcard(fields[4]));
            if (!schedule.CanEverMatch())
            {
                schedule = null;
                error = "Cron expression never matches a calendar date.";
                return false;
            }

            return true;
        }
        catch (FormatException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    /// <summary>Resolves an IANA or Windows time zone ID; <see langword="null" /> or empty means UTC.</summary>
    public static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId)) return TimeZoneInfo.Utc;
        return TryResolveTimeZone(timeZoneId, out TimeZoneInfo? zone)
            ? zone!
            : throw new DomainException($"Unknown time zone '{timeZoneId}'.");
    }

    public static bool TryResolveTimeZone(string? timeZoneId, out TimeZoneInfo? zone)
    {
        zone = null;
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            zone = TimeZoneInfo.Utc;
            return true;
        }

        if (timeZoneId.Length > 64) return false;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId.Trim());
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }

    /// <summary>The first occurrence strictly after <paramref name="afterUtc" />, or null when none within 5 years.</summary>
    public DateTimeOffset? GetNextOccurrence(DateTimeOffset afterUtc, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        DateTimeOffset after = afterUtc.ToUniversalTime();
        DateTime local = TimeZoneInfo.ConvertTime(after, zone).DateTime;
        DateTime candidate = TruncateToMinute(local).AddMinutes(1);
        DateTime limit = local.AddYears(5);
        while (candidate <= limit)
        {
            DateTime? match = NextLocalMatch(candidate, limit);
            if (match is null) return null;
            DateTimeOffset utc = ToUtc(match.Value, zone);
            if (utc > after) return utc;
            candidate = match.Value.AddMinutes(1);
        }

        return null;
    }

    /// <summary>
    ///     Occurrences in <c>(fromUtcExclusive, toUtcInclusive]</c>, oldest first, at most <paramref name="max" />
    ///     (the newest ones are kept when there are more).
    /// </summary>
    public IReadOnlyList<DateTimeOffset> GetOccurrences(DateTimeOffset fromUtcExclusive, DateTimeOffset toUtcInclusive,
        TimeZoneInfo zone, int max = 1000)
    {
        var result = new Queue<DateTimeOffset>();
        DateTimeOffset cursor = fromUtcExclusive;
        var total = 0;
        while (true)
        {
            DateTimeOffset? next = GetNextOccurrence(cursor, zone);
            if (next is null || next.Value > toUtcInclusive) break;
            result.Enqueue(next.Value);
            total++;
            if (result.Count > max) result.Dequeue();
            cursor = next.Value;
            // Hard stop for pathological windows (every minute for years): keep the newest window only.
            if (total > 1_000_000) break;
        }

        return result.ToArray();
    }

    public override string ToString()
    {
        return Expression;
    }

    private bool Matches(DateTime local)
    {
        return _minutes[local.Minute] && _hours[local.Hour] && _months[local.Month] && DayMatches(local);
    }

    private bool DayMatches(DateTime local)
    {
        var dom = _daysOfMonth[local.Day];
        var dow = _daysOfWeek[(int)local.DayOfWeek];
        if (_dayOfMonthRestricted && _dayOfWeekRestricted) return dom || dow;
        return dom && dow;
    }

    private DateTime? NextLocalMatch(DateTime start, DateTime limit)
    {
        DateTime value = start;
        while (value <= limit)
        {
            if (!_months[value.Month])
            {
                value = new DateTime(value.Year, value.Month, 1).AddMonths(1);
                continue;
            }

            if (!DayMatches(value))
            {
                value = value.Date.AddDays(1);
                continue;
            }

            if (!_hours[value.Hour])
            {
                value = new DateTime(value.Year, value.Month, value.Day, value.Hour, 0, 0).AddHours(1);
                continue;
            }

            if (!_minutes[value.Minute])
            {
                value = value.AddMinutes(1);
                continue;
            }

            return value;
        }

        return null;
    }

    private bool CanEverMatch()
    {
        // Day 29-31 in months that never have them (for example "0 0 30 2 *").
        if (_dayOfWeekRestricted && !_dayOfMonthRestricted) return true;
        if (_dayOfWeekRestricted && _dayOfMonthRestricted) return true;
        for (var month = 1; month <= 12; month++)
        {
            if (!_months[month]) continue;
            var days = month == 2 ? 29 : DateTime.DaysInMonth(2001, month);
            for (var day = 1; day <= days; day++)
                if (_daysOfMonth[day])
                    return true;
        }

        return false;
    }

    private static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(unspecified))
        {
            // Skipped by a forward DST transition: run at the first valid local minute after the gap.
            DateTime valid = unspecified;
            for (var i = 0; i < 24 * 60 && zone.IsInvalidTime(valid); i++) valid = valid.AddMinutes(1);
            return new DateTimeOffset(valid, zone.GetUtcOffset(valid)).ToUniversalTime();
        }

        if (zone.IsAmbiguousTime(unspecified))
        {
            // Repeated by a backward transition: the first occurrence (larger offset) only.
            TimeSpan offset = zone.GetAmbiguousTimeOffsets(unspecified).Max();
            return new DateTimeOffset(unspecified, offset).ToUniversalTime();
        }

        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified)).ToUniversalTime();
    }

    private static DateTime TruncateToMinute(DateTime value)
    {
        return new DateTime(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0, DateTimeKind.Unspecified);
    }

    private static string Expand(string expression)
    {
        return expression.ToLowerInvariant() switch
        {
            "@yearly" or "@annually" or "yearly" or "annually" => "0 0 1 1 *",
            "@monthly" or "monthly" => "0 0 1 * *",
            "@weekly" or "weekly" => "0 0 * * 0",
            "@daily" or "@midnight" or "daily" => "0 0 * * *",
            "@hourly" or "hourly" => "0 * * * *",
            "every_minute" => "* * * * *",
            _ => expression
        };
    }

    private static bool IsWildcard(string field)
    {
        return field is "*" or "?";
    }

    private static bool[] ParseField(string field, int min, int max, string[]? names, string label)
    {
        var values = new bool[max + 1];
        foreach (var part in field.Split(','))
        {
            if (part.Length == 0) throw new FormatException($"Cron {label} field has an empty list item.");
            var step = 1;
            var range = part;
            var slash = part.IndexOf('/', StringComparison.Ordinal);
            if (slash >= 0)
            {
                range = part[..slash];
                if (!int.TryParse(part[(slash + 1)..], System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out step) || step <= 0 || step > max)
                    throw new FormatException($"Cron {label} field has an invalid step '{part}'.");
            }

            int start;
            int end;
            if (range is "*" or "?")
            {
                start = min;
                end = max == 7 ? 6 : max;
            }
            else
            {
                var dash = range.IndexOf('-', StringComparison.Ordinal);
                if (dash >= 0)
                {
                    start = ParseValue(range[..dash], min, max, names, label);
                    end = ParseValue(range[(dash + 1)..], min, max, names, label);
                    if (end < start) throw new FormatException($"Cron {label} range '{range}' is reversed.");
                }
                else
                {
                    start = ParseValue(range, min, max, names, label);
                    end = slash >= 0 ? (max == 7 ? 6 : max) : start;
                }
            }

            for (var value = start; value <= end; value += step) values[value] = true;
        }

        return values;
    }

    private static int ParseValue(string text, int min, int max, string[]? names, string label)
    {
        if (names is not null)
        {
            var index = Array.FindIndex(names, name => string.Equals(name, text, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) return names == MonthNames ? index + 1 : index;
        }

        if (!int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
                out var value) || value < min || value > max)
            throw new FormatException($"Cron {label} value '{text}' must be between {min} and {max}.");
        return value;
    }
}
