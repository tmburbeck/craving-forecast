using CravingForecast.Core;

namespace CravingForecast.Tests;

public class SignalTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 20, 0, 0, 0, TimeSpan.FromHours(-6));
    private static (DateTimeOffset, DateTimeOffset) At(double startHour, double endHour) =>
        (Day.AddHours(startHour), Day.AddHours(endHour));

    [Theory]
    [InlineData(5.5, SleepQuality.Poor)]
    [InlineData(6.5, SleepQuality.Ok)]
    [InlineData(7.5, SleepQuality.Good)]
    public void Sleep_hours_map_to_quality(double hours, SleepQuality expected) =>
        Assert.Equal(expected, SignalClassifier.Sleep(hours));

    [Theory]
    [InlineData(2500, ActivityLevel.Low)]
    [InlineData(7000, ActivityLevel.Normal)]
    [InlineData(14000, ActivityLevel.High)]
    public void Steps_map_to_activity(double steps, ActivityLevel expected) =>
        Assert.Equal(expected, SignalClassifier.Activity(steps));

    [Fact]
    public void Overlapping_watch_and_phone_sleep_is_not_double_counted()
    {
        // Watch: 11 PM–6 AM. Phone: 11:30 PM–5 AM. Real sleep = 7 hours.
        var hours = SignalClassifier.HoursAsleep([At(-1, 6), At(-0.5, 5)]);
        Assert.Equal(7, hours, 3);
    }

    [Fact]
    public void Three_meetings_with_short_gaps_count_as_back_to_back()
    {
        Assert.True(SignalClassifier.IsBackToBack([At(9, 10), At(10.1, 11), At(11.25, 12)]));
        Assert.False(SignalClassifier.IsBackToBack([At(9, 10), At(11, 12), At(14, 15)]));
    }

    [Fact]
    public void Five_hours_of_meetings_counts_even_with_gaps() =>
        Assert.True(SignalClassifier.IsBackToBack([At(8, 10.5), At(13, 15.5)]));
}
