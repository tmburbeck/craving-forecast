// Shared by the API and the mobile app.
using CravingForecast.Core;

namespace CravingForecast.Core.Contracts;

// ---------- Requests ----------

public sealed record OnboardingRequest(
    int WakeHour,
    int BedHour,
    WorkSchedule Schedule,
    MealTime LargestMeal,
    GrazeDriver Graze,
    SkippedMeal Skip,
    List<Trigger>? Triggers,
    CravingType CravingType,
    NudgeStyle NudgeStyle,
    string? TimeZoneId);

public sealed record CheckInRequest(int Level, List<Trigger>? Context, DateTimeOffset? At);

/// <summary>Any field left null keeps its current value, so the app can send sleep and calendar separately.</summary>
public sealed record ContextRequest(SleepQuality? Sleep, bool? BackToBackMeetings, ActivityLevel? Activity);

public sealed record FeedbackRequest(DateOnly Date, int ZoneStartHour, FeedbackVerdict Verdict, int? ActualHour);

// ---------- Responses ----------

public sealed record ArchetypeDto(string Id, string Name, string Description, double Strength);
public sealed record RuleDto(string Id, string Name, string StartsAt, double Trust, bool PhasedOut);
public sealed record FactorDto(string Label, double Multiplier);
public sealed record HourDto(int Hour, double Risk, double Baseline, double PersonalWeight, Trigger Context);
public sealed record ZoneDto(int StartHour, int EndHour, string Label, Trigger Context, double Peak);
public sealed record NudgeDto(DateTimeOffset SendAt, string Title, string Body, IReadOnlyList<string> Tips, ZoneDto Zone);

public sealed record ForecastDto(
    DateOnly Date,
    int Day,
    string Phase,
    double Ceiling,
    double PersonalShare,
    IReadOnlyList<ArchetypeDto> Archetypes,
    IReadOnlyList<FactorDto> ContextFactors,
    IReadOnlyList<RuleDto> Rules,
    IReadOnlyList<ZoneDto> Zones,
    IReadOnlyList<HourDto> Hours,
    NudgeDto? NextNudge,
    SleepQuality? Sleep = null);

public sealed record OnboardingResponse(Guid UserId, ForecastDto Forecast);

/// <summary>Who is signed in, and whether they've done the onboarding quiz yet.</summary>
public sealed record MeResponse(string Email, bool HasProfile);

// ---------- Accounts (ASP.NET Core Identity endpoints under /api/auth) ----------

public sealed record AuthRequest(string Email, string Password);
public sealed record RefreshRequest(string RefreshToken);
public sealed record TokenResponse(string TokenType, string AccessToken, long ExpiresIn, string RefreshToken);
