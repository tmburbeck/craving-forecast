using CravingForecast.Core;
using CravingForecast.Core.Contracts;

namespace CravingForecast.Mobile.Services;

/// <summary>
/// The day's forecast, shared by the Today and Insights tabs and the check-in sheet.
/// Every change goes through here so nudges are rescheduled and all screens redraw together.
/// </summary>
public sealed class ForecastStore(ApiClient api, MorningSync sync, NudgeScheduler nudges)
{
    public ForecastDto? Forecast { get; private set; }
    public IReadOnlyList<ZoneDto> Pending { get; private set; } = [];
    public bool NeedsSleepTap { get; private set; }

    /// <summary>False until the morning sync has run in this app session (onboarding only seeds the forecast).</summary>
    public bool Synced { get; private set; }

    public event Action? Changed;

    private static string TodayKey => $"sleepTap:{DateTime.Now:yyyy-MM-dd}";

    /// <summary>The answer to this morning's "How did you sleep?" tap, if the user gave one.</summary>
    public static SleepQuality? SleepTapToday =>
        Enum.TryParse<SleepQuality>(Preferences.Get(TodayKey, ""), out var q) ? q : null;

    public async Task LoadAsync(bool force = false)
    {
        var result = await sync.RunAsync(force);
        Forecast = result.Forecast ?? await api.GetForecastAsync();
        NeedsSleepTap = !result.SleepFromHealth && SleepTapToday is null;
        Pending = await api.GetPendingFeedbackAsync();
        Synced = true;
        Changed?.Invoke();
    }

    public void Seed(ForecastDto forecast)
    {
        Forecast = forecast;
        Changed?.Invoke();
    }

    public Task SetSleepAsync(SleepQuality quality)
    {
        Preferences.Set(TodayKey, quality.ToString());
        NeedsSleepTap = false;
        return ApplyAsync(() => api.PutContextAsync(DateOnly.FromDateTime(DateTime.Now), new ContextRequest(quality, null, null)));
    }

    public Task CheckInAsync(int level, IEnumerable<Trigger> context) =>
        ApplyAsync(() => api.CheckInAsync(new CheckInRequest(level, [.. context], null)));

    public Task FeedbackAsync(ZoneDto zone, FeedbackVerdict verdict, int? actualHour) =>
        ApplyAsync(() => api.SendFeedbackAsync(new FeedbackRequest(DateOnly.FromDateTime(DateTime.Now), zone.StartHour, verdict, actualHour)));

    private async Task ApplyAsync(Func<Task<ForecastDto?>> call)
    {
        Forecast = await call() ?? Forecast;
        Pending = await api.GetPendingFeedbackAsync();
        await nudges.RescheduleAsync(await api.GetNudgesAsync(2));
        Changed?.Invoke();
    }
}
