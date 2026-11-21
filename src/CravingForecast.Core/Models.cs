using System.Text.Json.Serialization;

namespace CravingForecast.Core;

public enum WorkSchedule { Office, Early, Evening, Remote, Rotating }
public enum MealTime { Breakfast, Lunch, Dinner, Late }
public enum GrazeDriver { Boredom, Stress, Fatigue, None }
public enum SkippedMeal { Breakfast, Lunch, None }
public enum CravingType { Sweet, Salty, Both }
public enum NudgeStyle { Snack, Distract, Mix }
public enum SleepQuality { Good, Ok, Poor }
public enum ActivityLevel { Low, Normal, High }
public enum CheckInSource { User, Feedback }
public enum FeedbackVerdict { Accurate, Missed, DifferentTime }

/// <summary>The situation around a craving. Used for quiz triggers, check-in context and nudge matching.</summary>
public enum Trigger { Slump, Stress, Tv, Sleep, Bored, Gap, Social, Late }

/// <summary>Answers from the ~60 second onboarding quiz.</summary>
public sealed record OnboardingProfile(
    int WakeHour,
    int BedHour,
    WorkSchedule Schedule,
    MealTime LargestMeal,
    GrazeDriver Graze,
    SkippedMeal Skip,
    IReadOnlyList<Trigger> Triggers,
    CravingType CravingType,
    NudgeStyle NudgeStyle,
    DateOnly StartDate,
    string TimeZoneId = "America/Chicago")
{
    /// <summary>Bedtime on a 24+ scale so windows before a midnight bedtime don't wrap oddly (midnight = 24).</summary>
    [JsonIgnore]
    public int BedHourExtended => BedHour < 12 ? BedHour + 24 : BedHour;

    [JsonIgnore]
    public int WorkEndHour => Schedule switch
    {
        WorkSchedule.Early => 15,
        WorkSchedule.Evening => 23,
        _ => 17
    };

    public bool Has(Trigger t) => Triggers.Contains(t);

    public bool IsAwake(int hour) => BedHour > WakeHour
        ? hour >= WakeHour && hour < BedHour
        : hour >= WakeHour || hour < BedHour;

    [JsonIgnore]
    public TimeZoneInfo TimeZone => TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
}

/// <summary>A 1–10 craving/hunger rating. Feedback answers become weighted check-ins too.</summary>
public sealed record CheckIn(
    DateTimeOffset At,
    int Level,
    IReadOnlyList<Trigger> Context,
    double Weight = 1,
    CheckInSource Source = CheckInSource.User);

/// <summary>Passive daily signals: a sleep tap or HealthKit/Health Connect sync, calendar load, activity.</summary>
public sealed record DailyContext(
    DateOnly Date,
    SleepQuality? Sleep = null,
    bool BackToBackMeetings = false,
    ActivityLevel? Activity = null);

/// <summary>Counts of "Was this accurate?" answers for one baseline rule.</summary>
public sealed record RuleStats(int Accurate = 0, int Missed = 0);
