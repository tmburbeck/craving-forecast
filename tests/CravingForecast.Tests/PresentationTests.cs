using CravingForecast.Core;
using CravingForecast.Core.Contracts;
using CravingForecast.Core.Presentation;

namespace CravingForecast.Tests;

public class PresentationTests
{
    private static readonly DateOnly Day = new(2026, 11, 20);

    private static ForecastDto Forecast(params ZoneDto[] zones) => new(
        Day, 5, "Learning", 0.3, 0.2, [], [], [], zones,
        Enumerable.Range(0, 24).Select(h => new HourDto(h, h is >= 15 and <= 16 ? 0.7 : h == 11 ? 0.4 : 0.1, 0.1, 0, Trigger.Slump)).ToList(),
        null);

    private static readonly ZoneDto Slump = new(15, 16, "3–5 PM", Trigger.Slump, 0.7);

    [Fact]
    public void Before_a_zone_the_headline_says_clear_until_it_starts()
    {
        var o = OutlookBuilder.For(Forecast(Slump), Day.ToDateTime(new TimeOnly(9, 30)));
        Assert.False(o.InZone);
        Assert.Equal(RiskLevel.Calm, o.Now);
        Assert.Equal("Clear until 3\u00A0PM", o.Headline);
        Assert.Contains("Afternoon energy slump", o.Detail);
    }

    [Fact]
    public void Inside_a_zone_the_headline_says_when_it_ends()
    {
        var o = OutlookBuilder.For(Forecast(Slump), Day.ToDateTime(new TimeOnly(15, 10)));
        Assert.True(o.InZone);
        Assert.Equal(RiskLevel.Danger, o.Now);
        Assert.Equal("Danger zone until 5\u00A0PM", o.Headline);
    }

    [Fact]
    public void A_moderately_risky_hour_outside_a_zone_reads_as_watch()
    {
        var o = OutlookBuilder.For(Forecast(Slump), Day.ToDateTime(new TimeOnly(11, 0)));
        Assert.Equal(RiskLevel.Watch, o.Now);
        Assert.Equal("Right now · Watch", o.Status);
    }

    [Fact]
    public void After_the_last_zone_the_rest_of_the_day_is_clear()
    {
        var o = OutlookBuilder.For(Forecast(Slump), Day.ToDateTime(new TimeOnly(21, 0)));
        Assert.Equal("Clear for the rest of today", o.Headline);
    }

    [Fact]
    public void Zones_that_wrap_past_midnight_cover_the_early_hours()
    {
        var late = new ZoneDto(22, 1, "10 PM–2 AM", Trigger.Late, 0.8);
        Assert.True(OutlookBuilder.Covers(late, 23));
        Assert.True(OutlookBuilder.Covers(late, 0));
        Assert.False(OutlookBuilder.Covers(late, 12));
    }

    [Theory]
    [InlineData(0.1, RiskLevel.Calm)]
    [InlineData(0.35, RiskLevel.Watch)]
    [InlineData(0.55, RiskLevel.Danger)]
    public void Risk_levels_use_the_engines_zone_threshold(double risk, RiskLevel expected) =>
        Assert.Equal(expected, Risk.Level(risk));

    [Fact]
    public void Quiz_answers_become_an_onboarding_request()
    {
        var answers = new Dictionary<string, object>
        {
            ["wake"] = 7, ["bed"] = 23, ["sched"] = WorkSchedule.Remote, ["meal"] = MealTime.Dinner,
            ["graze"] = GrazeDriver.Stress, ["skip"] = SkippedMeal.None, ["type"] = CravingType.Salty, ["style"] = NudgeStyle.Snack
        };
        var r = OnboardingQuiz.BuildRequest(answers, [Trigger.Tv], "UTC");
        Assert.Equal(9, OnboardingQuiz.Questions.Count);
        Assert.Equal(WorkSchedule.Remote, r.Schedule);
        Assert.Equal([Trigger.Tv], r.Triggers);
    }
}
