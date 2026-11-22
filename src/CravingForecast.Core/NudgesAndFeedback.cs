namespace CravingForecast.Core;

public sealed record Nudge(DateTimeOffset SendAt, DangerZone Zone, string Title, string Body, IReadOnlyList<string> Tips);

public static class NudgePlanner
{
    public static readonly TimeSpan LeadTime = TimeSpan.FromMinutes(30);

    private static readonly Dictionary<CravingType, string[]> Snacks = new()
    {
        [CravingType.Sweet] = ["Greek yogurt with frozen berries", "Frozen grapes and a protein shake", "Cottage cheese with cinnamon and apple"],
        [CravingType.Salty] = ["3 cups air-popped popcorn", "Cucumber spears with salt and tzatziki", "Jerky with a pickle or two"],
        [CravingType.Both] = ["Greek yogurt with berries", "Air-popped popcorn, pre-portioned", "Edamame with sea salt"]
    };

    private static readonly Dictionary<Trigger, string[]> Actions = new()
    {
        [Trigger.Slump] = ["10-minute walk outside for light and movement", "Water and a protein snack instead of a second coffee"],
        [Trigger.Stress] = ["5-minute walk before you walk in the door", "Box breathing: 4 in, 4 hold, 4 out, 4 hold"],
        [Trigger.Tv] = ["Pour the snack into a bowl, put the bag away", "Keep hands busy: tea, sparkling water, a puzzle"],
        [Trigger.Sleep] = ["Eat a protein-forward lunch a little earlier", "A 10-minute daylight walk"],
        [Trigger.Bored] = ["Change rooms or step outside for 10 minutes", "Start one small task you've been putting off"],
        [Trigger.Gap] = ["Eat your planned snack now, before you're starving", "Set a 3–4 hour meal reminder"],
        [Trigger.Social] = ["Decide on one treat in advance and enjoy it", "Eat a protein snack before you go"],
        [Trigger.Late] = ["Decide tonight's snack at dinner, not at 9 PM", "Brush your teeth to close the kitchen, then herbal tea"]
    };

    private static readonly Dictionary<Trigger, string> Titles = new()
    {
        [Trigger.Slump] = "Afternoon slump ahead",
        [Trigger.Stress] = "Stress window coming up",
        [Trigger.Tv] = "Screen-time snacking window",
        [Trigger.Sleep] = "Short-sleep day: heads up",
        [Trigger.Bored] = "Boredom window ahead",
        [Trigger.Gap] = "Long gap coming: eat first",
        [Trigger.Social] = "Social eating window",
        [Trigger.Late] = "Decision-fatigue window tonight"
    };

    public static IReadOnlyList<string> TipsFor(OnboardingProfile p, Trigger ctx)
    {
        var s = Snacks[p.CravingType];
        var a = Actions[ctx];
        return p.NudgeStyle switch
        {
            NudgeStyle.Snack => [s[0], s[1], a[0]],
            NudgeStyle.Distract => [a[0], a[1], s[0]],
            _ => [s[0], a[0], a[1]]
        };
    }

    /// <summary>One nudge per zone, 30 minutes before it starts, in the user's time zone.</summary>
    public static IReadOnlyList<Nudge> Plan(OnboardingProfile p, Forecast f)
    {
        var tz = p.TimeZone;
        return f.Zones.Select(z =>
        {
            var local = f.Date.ToDateTime(new TimeOnly(z.StartHour % 24, 0));
            if (z.StartHour >= 24) local = local.AddDays(1);
            var start = new DateTimeOffset(local, tz.GetUtcOffset(local));
            var tips = TipsFor(p, z.Context);
            return new Nudge(start - LeadTime, z, Titles[z.Context],
                $"Your {z.Label} window starts in 30 minutes. Try: {tips[0]}.", tips);
        }).ToList();
    }
}

public sealed record FeedbackReminder(DateTimeOffset SendAt, DangerZone Zone, string Title, string Body);

/// <summary>
/// A gentle "Was this accurate?" after each zone ends. The answers are what make the forecast personal,
/// and people answer far more often when asked while the afternoon is still fresh.
/// </summary>
public static class FeedbackReminders
{
    /// <summary>How long after a zone ends the reminder goes out.</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromMinutes(15);

    /// <summary>No reminders at or after this local hour; a late-night ping isn't worth it.</summary>
    public const int QuietFromHour = 23;

    /// <summary>One reminder per zone that can be rated today (it ends before midnight), skipping late-night ones.</summary>
    public static IReadOnlyList<FeedbackReminder> Plan(OnboardingProfile p, Forecast f)
    {
        var tz = p.TimeZone;
        return f.Zones
            // Matches when Today starts offering "Was this accurate?": the hour after the zone's last hour.
            .Where(z => z.EndHour + 1 < 24)
            .Select(z =>
            {
                var local = f.Date.ToDateTime(new TimeOnly(z.EndHour + 1, 0)) + Delay;
                return (z, local);
            })
            .Where(x => x.local.Hour < QuietFromHour)
            .Select(x => new FeedbackReminder(
                new DateTimeOffset(x.local, tz.GetUtcOffset(x.local)), x.z,
                $"How did {x.z.Label} go?",
                "Was Salus right about this one? One tap helps it learn your days."))
            .ToList();
    }
}

public sealed record FeedbackResult(IReadOnlyDictionary<string, RuleStats> RuleStats, IReadOnlyList<CheckIn> NewCheckIns);

/// <summary>
/// Turns "Was this accurate?" into training data. Confirmations and misses update the trust of
/// every rule that covered the zone, and also become weighted check-ins so the personal layer learns.
/// </summary>
public static class FeedbackProcessor
{
    public const double ConfirmWeight = 3, MissWeight = 2;

    public static FeedbackResult Apply(
        OnboardingProfile profile,
        Forecast forecast,
        DangerZone zone,
        FeedbackVerdict verdict,
        int? actualHour,
        IReadOnlyDictionary<string, RuleStats> current)
    {
        if (verdict == FeedbackVerdict.DifferentTime && actualHour is null)
            throw new ArgumentException("A different-time answer needs the hour the craving hit.", nameof(actualHour));

        var tz = profile.TimeZone;
        DateTimeOffset At(int hour)
        {
            var local = forecast.Date.ToDateTime(new TimeOnly(hour % 24, 30));
            return new DateTimeOffset(local, tz.GetUtcOffset(local));
        }

        var stats = new Dictionary<string, RuleStats>(current);
        var covering = forecast.Rules.Where(r => r.Window.Hours.Intersect(zone.Hours).Any()).Select(r => r.Window.Id);
        foreach (var id in covering)
        {
            var s = stats.GetValueOrDefault(id) ?? new RuleStats();
            stats[id] = verdict == FeedbackVerdict.Accurate ? s with { Accurate = s.Accurate + 1 } : s with { Missed = s.Missed + 1 };
        }

        var checkIns = new List<CheckIn>();
        if (verdict == FeedbackVerdict.Accurate)
        {
            checkIns.AddRange(zone.Hours.Select(h => new CheckIn(At(h), 7, [zone.Context], ConfirmWeight, CheckInSource.Feedback)));
        }
        else
        {
            checkIns.AddRange(zone.Hours.Select(h => new CheckIn(At(h), 2, [], MissWeight, CheckInSource.Feedback)));
            if (verdict == FeedbackVerdict.DifferentTime)
                checkIns.Add(new CheckIn(At(actualHour!.Value), 8, [zone.Context], ConfirmWeight, CheckInSource.Feedback));
        }

        return new FeedbackResult(stats, checkIns);
    }
}
