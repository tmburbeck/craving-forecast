using CravingForecast.Core;

namespace CravingForecast.Tests;

public class EngineTests
{
    private static readonly DateOnly Start = new(2026, 11, 16); // a Monday

    private static OnboardingProfile Profile(
        GrazeDriver graze = GrazeDriver.Fatigue,
        MealTime meal = MealTime.Dinner,
        SkippedMeal skip = SkippedMeal.Breakfast,
        params Trigger[] triggers) =>
        new(7, 23, WorkSchedule.Office, meal, graze, skip,
            triggers.Length == 0 ? [Trigger.Late, Trigger.Stress] : triggers,
            CravingType.Sweet, NudgeStyle.Mix, Start);

    private static readonly Dictionary<string, RuleStats> NoStats = [];

    private static Forecast Build(OnboardingProfile p, DateOnly date, DailyContext? ctx = null,
        IReadOnlyList<CheckIn>? checkIns = null, IReadOnlyDictionary<string, RuleStats>? stats = null) =>
        ForecastEngine.Build(p, date, ctx, checkIns ?? [], stats ?? NoStats);

    private static DateTimeOffset LocalTime(OnboardingProfile p, DateOnly d, int hour, int minute = 0)
    {
        var local = d.ToDateTime(new TimeOnly(hour, minute));
        return new DateTimeOffset(local, p.TimeZone.GetUtcOffset(local));
    }

    [Theory]
    [InlineData(1, 0.0)]
    [InlineData(7, 0.5)]
    [InlineData(14, 1.0)]
    [InlineData(30, 1.0)]
    public void Personalization_ceiling_follows_the_day_1_7_14_schedule(int day, double expected) =>
        Assert.Equal(expected, PersonalizationSchedule.Ceiling(day), 3);

    [Fact]
    public void Fatigue_grazer_with_late_meals_matches_revenge_bedtime_snacker_first()
    {
        var p = Profile(GrazeDriver.Fatigue, MealTime.Late, SkippedMeal.None, Trigger.Late, Trigger.Tv);
        Assert.Equal("revenge", ArchetypeMatcher.Match(p)[0].Archetype.Id);
    }

    [Fact]
    public void Day_one_ignores_check_ins_completely()
    {
        var p = Profile();
        var checkIns = Enumerable.Range(0, 10).Select(_ => new CheckIn(LocalTime(p, Start, 10), 10, [])).ToList();
        var f = Build(p, Start, checkIns: checkIns);
        Assert.All(f.Hours, h => Assert.Equal(0, h.PersonalWeight));
        Assert.Equal(f.Hours[10].Baseline, f.Hours[10].Risk, 3);
    }

    [Fact]
    public void Day_fourteen_lets_strong_personal_data_create_a_zone_the_baseline_never_predicted()
    {
        var p = Profile();
        var day14 = Start.AddDays(13);
        // Baseline: 10 AM is quiet for this profile.
        Assert.True(Build(p, day14).Hours[10].Risk < ZoneDetector.Threshold);

        var checkIns = Enumerable.Range(0, 8)
            .Select(i => new CheckIn(LocalTime(p, Start.AddDays(i), 10, 15), 9, [Trigger.Bored]))
            .ToList();
        var f = Build(p, day14, checkIns: checkIns);

        Assert.True(f.Hours[10].PersonalWeight > 0.75);
        Assert.Contains(f.Zones, z => z.Hours.Contains(10) && z.Context == Trigger.Bored);
    }

    [Fact]
    public void Poor_sleep_raises_total_risk_and_good_sleep_lowers_it()
    {
        var p = Profile();
        var d = Start.AddDays(1);
        double Total(SleepQuality s) => Build(p, d, new DailyContext(d, s)).Hours.Sum(h => h.Risk);
        Assert.True(Total(SleepQuality.Poor) > Total(SleepQuality.Ok));
        Assert.True(Total(SleepQuality.Good) < Total(SleepQuality.Ok));
    }

    [Fact]
    public void Friday_evening_carries_more_risk_than_thursday_evening()
    {
        var p = Profile();
        var thu = new DateOnly(2026, 11, 19);
        var fri = thu.AddDays(1);
        double Evening(DateOnly d) => Build(p, d).Hours.Where(h => h.Hour is >= 17 and <= 22).Sum(h => h.Risk);
        Assert.True(Evening(fri) > Evening(thu));
    }

    [Fact]
    public void Back_to_back_meetings_raise_post_work_risk()
    {
        var p = Profile();
        var d = Start.AddDays(2);
        var calm = Build(p, d).Hours[p.WorkEndHour].Risk;
        var busy = Build(p, d, new DailyContext(d, BackToBackMeetings: true)).Hours[p.WorkEndHour].Risk;
        Assert.True(busy > calm);
    }

    [Fact]
    public void Repeated_misses_phase_a_rule_out()
    {
        Assert.False(RuleTrust.IsPhasedOut(new RuleStats(0, 1)));
        Assert.True(RuleTrust.IsPhasedOut(new RuleStats(0, 5)));
        Assert.True(RuleTrust.Weight(new RuleStats(3, 0)) > RuleTrust.Weight(new RuleStats()));
    }

    [Fact]
    public void Missed_feedback_lowers_trust_and_adds_low_weighted_check_ins()
    {
        var p = Profile();
        var f = Build(p, Start);
        var zone = f.Zones[0];

        var result = FeedbackProcessor.Apply(p, f, zone, FeedbackVerdict.Missed, null, NoStats);

        Assert.NotEmpty(result.RuleStats);
        Assert.All(result.RuleStats.Values, s => Assert.Equal(1, s.Missed));
        Assert.All(result.NewCheckIns, c =>
        {
            Assert.Equal(2, c.Level);
            Assert.Equal(FeedbackProcessor.MissWeight, c.Weight);
            Assert.Equal(CheckInSource.Feedback, c.Source);
        });
    }

    [Fact]
    public void Different_time_feedback_records_a_strong_craving_at_the_actual_hour()
    {
        var p = Profile();
        var f = Build(p, Start);
        var result = FeedbackProcessor.Apply(p, f, f.Zones[0], FeedbackVerdict.DifferentTime, 11, NoStats);
        var strong = Assert.Single(result.NewCheckIns, c => c.Level == 8);
        Assert.Equal(11, TimeZoneInfo.ConvertTime(strong.At, p.TimeZone).Hour);
        Assert.Throws<ArgumentException>(() =>
            FeedbackProcessor.Apply(p, f, f.Zones[0], FeedbackVerdict.DifferentTime, null, NoStats));
    }

    [Fact]
    public void Nudges_go_out_thirty_minutes_before_each_zone_in_local_time()
    {
        var p = Profile();
        var f = Build(p, Start);
        var nudges = NudgePlanner.Plan(p, f);
        Assert.Equal(f.Zones.Count, nudges.Count);
        foreach (var n in nudges)
        {
            var local = TimeZoneInfo.ConvertTime(n.SendAt, p.TimeZone);
            Assert.Equal(30, local.Minute);
            Assert.Equal((n.Zone.StartHour + 23) % 24, local.Hour);
            Assert.Equal(3, n.Tips.Count);
        }
    }

    [Fact]
    public void Day_one_always_has_at_least_one_zone()
    {
        var quiet = new OnboardingProfile(7, 23, WorkSchedule.Remote, MealTime.Lunch, GrazeDriver.None, SkippedMeal.None,
            [], CravingType.Both, NudgeStyle.Mix, Start);
        Assert.NotEmpty(Build(quiet, Start).Zones);
    }

    [Fact]
    public void Hours_range_wraps_past_midnight() =>
        Assert.Equal(new[] { 22, 23, 0, 1 }, Hours.Range(22, 25));
}
