using CravingForecast.Core;
using CravingForecast.Core.Contracts;

namespace CravingForecast.Api;

/// <summary>Glue between stored user state, the clock and the pure Core engine.</summary>
public sealed class ForecastService(TimeProvider clock)
{
    public DateTimeOffset Now => clock.GetUtcNow();

    public DateTimeOffset LocalNow(OnboardingProfile p) => TimeZoneInfo.ConvertTime(Now, p.TimeZone);

    public DateOnly Today(OnboardingProfile p) => DateOnly.FromDateTime(LocalNow(p).DateTime);

    public Forecast ForecastFor(UserState u, DateOnly date) =>
        ForecastEngine.Build(u.Profile, date, u.ContextFor(date), u.CheckIns, u.RuleStats);

    /// <summary>Nudges still in the future for today and the following days.</summary>
    public IReadOnlyList<Nudge> UpcomingNudges(UserState u, int days)
    {
        var today = Today(u.Profile);
        return Enumerable.Range(0, Math.Clamp(days, 1, 7))
            .SelectMany(i => NudgePlanner.Plan(u.Profile, ForecastFor(u, today.AddDays(i))))
            .Where(n => n.SendAt > Now)
            .OrderBy(n => n.SendAt)
            .ToList();
    }

    /// <summary>Zones from today that have ended and haven't been rated yet.</summary>
    public IReadOnlyList<DangerZone> PendingFeedback(UserState u)
    {
        var today = Today(u.Profile);
        var localHour = LocalNow(u.Profile).Hour;
        return ForecastFor(u, today).Zones
            .Where(z => z.EndHour < 24 && z.EndHour + 1 <= localHour)
            .Where(z => !u.FeedbackGiven.Contains($"{UserState.Key(today)}@{z.StartHour}"))
            .ToList();
    }

    public ForecastDto Dto(UserState u, DateOnly date)
    {
        var f = ForecastFor(u, date);
        var next = NudgePlanner.Plan(u.Profile, f).Where(n => n.SendAt > Now).MinBy(n => n.SendAt);
        return Map.Forecast(f, next) with { Sleep = u.ContextFor(date)?.Sleep };
    }
}
