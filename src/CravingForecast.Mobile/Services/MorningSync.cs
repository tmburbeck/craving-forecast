using CravingForecast.Core.Contracts;

namespace CravingForecast.Mobile.Services;

public sealed record SyncResult(ForecastDto? Forecast, bool SleepFromHealth);

/// <summary>
/// Layer 2, once per day: read sleep and activity from Health and the day's calendar,
/// send them to the API, then reschedule tomorrow's and today's nudges.
/// </summary>
public sealed class MorningSync(ApiClient api, IHealthSignals health, ICalendarSignals calendar, NudgeScheduler nudges)
{
    public async Task<SyncResult> RunAsync(bool force = false)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (!force && Session.LastSync == today)
            return new SyncResult(await api.GetForecastAsync(), true);

        var sleep = health.IsAvailable ? await Safe(health.GetLastNightSleepAsync) : null;
        var activity = health.IsAvailable ? await Safe(health.GetYesterdayActivityAsync) : null;
        var busy = await Safe(() => calendar.HasBackToBackMeetingsAsync(today));

        var forecast = await api.PutContextAsync(today, new ContextRequest(sleep, busy, activity));
        await nudges.RescheduleAsync(await api.GetNudgesAsync(2));
        Session.LastSync = today;
        return new SyncResult(forecast, sleep is not null);
    }

    /// <summary>A denied permission or missing sensor should never block the forecast.</summary>
    private static async Task<T?> Safe<T>(Func<Task<T?>> read) where T : struct
    {
        try { return await read(); }
        catch { return null; }
    }
}
