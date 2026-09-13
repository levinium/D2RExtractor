using D2RExtractor.Services.Updates;
using Shouldly;
using Xunit;

namespace D2RExtractor.Tests;

/// <summary>How often the app is allowed to go and look.</summary>
public class UpdateScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AMachineThatHasNeverCheckedIsDue()
    {
        UpdateSchedule.IsDue(null, Now).ShouldBeTrue();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-12)]
    [InlineData(-23)]
    public void AnHourOrSoAgoIsNotDue(int hoursAgo)
    {
        UpdateSchedule.IsDue(Now.AddHours(hoursAgo), Now).ShouldBeFalse();
    }

    [Theory]
    [InlineData(-24)]
    [InlineData(-25)]
    [InlineData(-500)]
    public void ADayOrMoreAgoIsDue(int hoursAgo)
    {
        UpdateSchedule.IsDue(Now.AddHours(hoursAgo), Now).ShouldBeTrue();
    }

    /// <summary>
    /// A stored time in the future should be impossible and is exactly what a clock correction
    /// leaves behind. The naive comparison reads it as "checked recently" forever, so the check
    /// silently never runs again — on the machines least likely to notice. Treating it as due costs
    /// one request and repairs the stored value.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(24)]
    [InlineData(24 * 365)]
    public void ATimestampFromTheFutureIsDueRatherThanNever(int hoursAhead)
    {
        UpdateSchedule.IsDue(Now.AddHours(hoursAhead), Now).ShouldBeTrue();
    }

    [Fact]
    public void TheIntervalIsOnceADay()
    {
        UpdateSchedule.Interval.ShouldBe(TimeSpan.FromDays(1));
    }
}
