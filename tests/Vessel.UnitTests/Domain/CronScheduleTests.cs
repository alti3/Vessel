using Vessel.Domain.Common;
using Vessel.Domain.ValueObjects;

namespace Vessel.UnitTests.Domain;

/// <summary>NTF-08: time-zone-aware cron evaluation used by every Vessel schedule.</summary>
public sealed class CronScheduleTests
{
    private static readonly TimeZoneInfo Berlin = CronSchedule.ResolveTimeZone("Europe/Berlin");

    [Theory]
    [InlineData("*/15 * * * *")]
    [InlineData("0 2 * * 1-5")]
    [InlineData("0 0 1 JAN,JUL *")]
    [InlineData("30 4 * * SUN")]
    [InlineData("@daily")]
    [InlineData("every_minute")]
    [InlineData("0 0 3 * * *")]
    public void Parse_AcceptsSupportedForms(string expression)
    {
        Assert.True(CronSchedule.IsValid(expression));
    }

    [Theory]
    [InlineData("")]
    [InlineData("* * * *")]
    [InlineData("60 * * * *")]
    [InlineData("* 24 * * *")]
    [InlineData("5 0 3 * * *")]
    [InlineData("0 0 30 2 *")]
    [InlineData("*/0 * * * *")]
    [InlineData("5-1 * * * *")]
    [InlineData("; rm -rf / * * * *")]
    public void Parse_RejectsInvalidExpressions(string expression)
    {
        Assert.False(CronSchedule.IsValid(expression));
        Assert.Throws<DomainException>(() => CronSchedule.Parse(expression));
    }

    [Fact]
    public void Next_StepsThroughTheHour()
    {
        CronSchedule cron = CronSchedule.Parse("*/15 * * * *");
        var from = new DateTimeOffset(2026, 11, 20, 12, 7, 30, TimeSpan.Zero);

        Assert.Equal(new DateTimeOffset(2026, 11, 20, 12, 15, 0, TimeSpan.Zero),
            cron.GetNextOccurrence(from, TimeZoneInfo.Utc));
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 12, 30, 0, TimeSpan.Zero),
            cron.GetNextOccurrence(new DateTimeOffset(2026, 11, 20, 12, 15, 0, TimeSpan.Zero), TimeZoneInfo.Utc));
    }

    [Fact]
    public void DayOfMonthAndDayOfWeek_MatchEither()
    {
        // The 1st of the month or any Friday.
        CronSchedule cron = CronSchedule.Parse("0 0 1 * 5");
        // 2026-11-20 is a Friday.
        DateTimeOffset? next = cron.GetNextOccurrence(new DateTimeOffset(2026, 11, 19, 0, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero), next);
        Assert.Equal(new DateTimeOffset(2026, 11, 27, 0, 0, 0, TimeSpan.Zero),
            cron.GetNextOccurrence(next!.Value, TimeZoneInfo.Utc));
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero),
            cron.GetNextOccurrence(new DateTimeOffset(2026, 11, 27, 0, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc));
    }

    [Fact]
    public void TimeZone_IsAppliedIncludingDaylightSavingOffsets()
    {
        CronSchedule cron = CronSchedule.Parse("0 9 * * *");
        Assert.Equal(new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero),
            cron.GetNextOccurrence(new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero), Berlin));
        Assert.Equal(new DateTimeOffset(2026, 7, 15, 7, 0, 0, TimeSpan.Zero),
            cron.GetNextOccurrence(new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero), Berlin));
    }

    [Fact]
    public void SkippedLocalTime_RunsOnceAtTheEndOfTheGap()
    {
        // 2026-03-29: Berlin clocks jump from 02:00 to 03:00 (01:00 UTC).
        CronSchedule cron = CronSchedule.Parse("30 2 * * *");
        IReadOnlyList<DateTimeOffset> occurrences = cron.GetOccurrences(
            new DateTimeOffset(2026, 3, 28, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 3, 30, 12, 0, 0, TimeSpan.Zero), Berlin);

        Assert.Equal(
            [
                new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 3, 30, 0, 30, 0, TimeSpan.Zero)
            ],
            occurrences);
    }

    [Fact]
    public void RepeatedLocalHour_RunsOnce()
    {
        // 2026-10-25: Berlin clocks fall back from 03:00 to 02:00; 02:30 happens twice locally.
        CronSchedule cron = CronSchedule.Parse("30 2 * * *");
        IReadOnlyList<DateTimeOffset> occurrences = cron.GetOccurrences(
            new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero), Berlin);

        DateTimeOffset single = Assert.Single(occurrences);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero), single);
    }

    [Fact]
    public void UnknownTimeZone_IsRejected()
    {
        Assert.False(CronSchedule.TryResolveTimeZone("Mars/Olympus", out _));
        Assert.Throws<DomainException>(() => CronSchedule.ResolveTimeZone("Mars/Olympus"));
        Assert.Same(TimeZoneInfo.Utc, CronSchedule.ResolveTimeZone(null));
    }
}
