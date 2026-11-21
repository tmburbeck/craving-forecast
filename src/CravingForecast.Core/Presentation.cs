using CravingForecast.Core.Contracts;

namespace CravingForecast.Core.Presentation;

// Wording and display rules shared by every front end (the MAUI app and the web app), so they can't drift apart.

public static class TriggerLabels
{
    public static readonly IReadOnlyDictionary<Trigger, string> All = new Dictionary<Trigger, string>
    {
        [Trigger.Slump] = "Afternoon energy slump",
        [Trigger.Stress] = "Stressed after work",
        [Trigger.Tv] = "Watching TV / scrolling",
        [Trigger.Sleep] = "Slept poorly",
        [Trigger.Bored] = "Bored at home",
        [Trigger.Gap] = "Long gap since eating",
        [Trigger.Social] = "Social / eating out",
        [Trigger.Late] = "Late night, decision fatigue"
    };

    public static string Of(Trigger trigger) => All[trigger];

    /// <summary>The "Was this accurate?" question, worded to read well for every trigger.</summary>
    public static string FeedbackPrompt(ZoneDto zone) =>
        $"I predicted a danger zone from {zone.Label}: {Of(zone.Context).ToLowerInvariant()}.";
}

public enum RiskLevel { Calm, Watch, Danger }

public static class Risk
{
    /// <summary>Below this an hour reads as calm.</summary>
    public const double WatchThreshold = 0.35;

    /// <summary>Danger matches the engine's zone threshold, so a danger-coloured hour always belongs to a real zone.</summary>
    public static RiskLevel Level(double risk) =>
        risk >= ZoneDetector.Threshold ? RiskLevel.Danger : risk >= WatchThreshold ? RiskLevel.Watch : RiskLevel.Calm;
}

public static class TimeText
{
    /// <summary>"3 PM", "12 AM", with a non-breaking space so a headline never wraps between "3" and "PM".</summary>
    public static string Hour(int hour)
    {
        var h = ((hour % 24) + 24) % 24;
        return $"{(h % 12 == 0 ? 12 : h % 12)}\u00A0{(h < 12 ? "AM" : "PM")}";
    }

    /// <summary>"3p", "12a": for chart axes.</summary>
    public static string ShortHour(int hour)
    {
        var h = ((hour % 24) + 24) % 24;
        return $"{(h % 12 == 0 ? 12 : h % 12)}{(h < 12 ? "a" : "p")}";
    }

    public static string Clock(DateTimeOffset time) => time.ToLocalTime().ToString("h:mm\u00A0tt");
}

/// <summary>The weather-style headline at the top of Today.</summary>
public sealed record Outlook(RiskLevel Now, bool InZone, string Status, string Headline, string Detail);

public static class OutlookBuilder
{
    public static bool Covers(ZoneDto z, int hour) =>
        z.StartHour <= z.EndHour ? hour >= z.StartHour && hour <= z.EndHour : hour >= z.StartHour || hour <= z.EndHour;

    /// <param name="localNow">The user's current local time; only its date and hour are used.</param>
    public static Outlook For(ForecastDto f, DateTime localNow)
    {
        var isToday = f.Date == DateOnly.FromDateTime(localNow);
        var hour = localNow.Hour;
        var risk = isToday ? f.Hours.FirstOrDefault(h => h.Hour == hour)?.Risk ?? 0 : 0;
        var current = isToday ? f.Zones.FirstOrDefault(z => Covers(z, hour)) : null;

        if (current is not null)
            return new Outlook(RiskLevel.Danger, true, "Right now · Danger zone",
                $"Danger zone until {TimeText.Hour(current.EndHour + 1)}",
                $"{TriggerLabels.Of(current.Context)} tends to hit now. Lean on one of the ideas below.");

        var level = Risk.Level(risk) == RiskLevel.Calm ? RiskLevel.Calm : RiskLevel.Watch;
        var status = level == RiskLevel.Calm ? "Right now · Calm" : "Right now · Watch";
        var next = (isToday ? f.Zones.Where(z => z.StartHour > hour) : f.Zones).MinBy(z => z.StartHour);
        if (next is null)
            return new Outlook(level, false, status, "Clear for the rest of today",
                "No danger zones left. Check in any time.");

        var nudge = f.NextNudge is { } n ? $" A nudge is set for {TimeText.Clock(n.SendAt)}." : "";
        return new Outlook(level, false, status, $"Clear until {TimeText.Hour(next.StartHour)}",
            $"{TriggerLabels.Of(next.Context)} usually hits around then.{nudge}");
    }
}

public static class CheckInScale
{
    private static readonly string[] Words =
        ["Pick a number", "Barely there", "Barely there", "Mild", "Mild", "Noticeable", "Noticeable", "Strong", "Strong", "Very strong", "Hard to ignore"];

    public static string Word(int level) => Words[Math.Clamp(level, 0, 10)];

    public static RiskLevel Level(int level) => level >= 7 ? RiskLevel.Danger : level >= 4 ? RiskLevel.Watch : RiskLevel.Calm;
}

public sealed record QuizOption(object Value, string Label);

public sealed record QuizQuestion(string Key, string Text, IReadOnlyList<QuizOption> Options, bool Multi = false);

/// <summary>Layer 1: the nine-question quiz that matches the user to archetypes.</summary>
public static class OnboardingQuiz
{
    public static readonly IReadOnlyList<QuizQuestion> Questions =
    [
        new("wake", "When do you usually wake up?", Opts((5, "5 AM"), (6, "6 AM"), (7, "7 AM"), (8, "8 AM"), (9, "9 AM or later"))),
        new("bed", "When do you usually go to bed?", Opts((21, "9 PM"), (22, "10 PM"), (23, "11 PM"), (0, "Midnight"), (1, "1 AM or later"))),
        new("sched", "What is your typical work schedule?", Opts((WorkSchedule.Office, "9–5, on-site"), (WorkSchedule.Early, "Early shift, done by 3"), (WorkSchedule.Evening, "Evening or night shift"), (WorkSchedule.Remote, "Remote or flexible"), (WorkSchedule.Rotating, "Rotating or irregular"))),
        new("meal", "When do you usually eat your largest meal?", Opts((MealTime.Breakfast, "Breakfast"), (MealTime.Lunch, "Lunch"), (MealTime.Dinner, "Dinner"), (MealTime.Late, "Late at night"))),
        new("graze", "Do you tend to graze? What drives it?", Opts((GrazeDriver.Boredom, "Boredom"), (GrazeDriver.Stress, "Stress"), (GrazeDriver.Fatigue, "Late-night fatigue"), (GrazeDriver.None, "I don't really graze"))),
        new("skip", "Which meal do you skip most often?", Opts((SkippedMeal.Breakfast, "Breakfast"), (SkippedMeal.Lunch, "Lunch"), (SkippedMeal.None, "I rarely skip"))),
        new("triggers", "When do cravings usually get you?", TriggerLabels.All.Select(t => new QuizOption(t.Key, t.Value)).ToList(), Multi: true),
        new("type", "What do you usually reach for?", Opts((CravingType.Sweet, "Sweet"), (CravingType.Salty, "Salty or crunchy"), (CravingType.Both, "Anything, honestly"))),
        new("style", "What helps you more in the moment?", Opts((NudgeStyle.Snack, "A better snack ready to go"), (NudgeStyle.Distract, "A quick distraction"), (NudgeStyle.Mix, "A mix of both")))
    ];

    private static IReadOnlyList<QuizOption> Opts(params (object Value, string Label)[] options) =>
        options.Select(o => new QuizOption(o.Value, o.Label)).ToList();

    /// <summary>Turns the answers (one value per single-choice question) and the picked triggers into the API request.</summary>
    public static OnboardingRequest BuildRequest(IReadOnlyDictionary<string, object> answers, IEnumerable<Trigger> triggers, string timeZoneId) => new(
        (int)answers["wake"], (int)answers["bed"], (WorkSchedule)answers["sched"], (MealTime)answers["meal"],
        (GrazeDriver)answers["graze"], (SkippedMeal)answers["skip"], [.. triggers],
        (CravingType)answers["type"], (NudgeStyle)answers["style"], timeZoneId);
}
