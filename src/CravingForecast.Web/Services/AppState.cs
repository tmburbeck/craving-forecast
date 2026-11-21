using CravingForecast.Core;
using CravingForecast.Core.Contracts;

namespace CravingForecast.Web.Services;

/// <summary>Who is signed in and today's forecast, shared by every screen so they redraw together.</summary>
public sealed class AppState(Api api)
{
    public MeResponse? Me { get; private set; }
    public ForecastDto? Forecast { get; private set; }
    public IReadOnlyList<ZoneDto> Pending { get; private set; } = [];

    /// <summary>The last confirmation to show on Today ("Logged. Your forecast just updated.").</summary>
    public string? Notice { get; set; }

    public event Action? Changed;

    public bool IsToday => Forecast is { } f && f.Date == DateOnly.FromDateTime(DateTime.Now);

    /// <summary>The web app has no health data, so it asks once a day unless sleep is already recorded.</summary>
    public bool NeedsSleepTap => IsToday && Forecast!.Sleep is null;

    /// <summary>Who is signed in; null when nobody is.</summary>
    public async Task<MeResponse?> RefreshMeAsync() => Me = await api.GetMeAsync();

    public async Task LoadAsync()
    {
        Forecast = await api.GetForecastAsync();
        Pending = await api.GetPendingFeedbackAsync();
        Changed?.Invoke();
    }

    public void Seed(MeResponse me, ForecastDto forecast)
    {
        Me = me;
        Forecast = forecast;
        Changed?.Invoke();
    }

    public void Reset()
    {
        Me = null;
        Forecast = null;
        Pending = [];
        Notice = null;
        Changed?.Invoke();
    }

    public Task SetSleepAsync(SleepQuality q) =>
        ApplyAsync(() => api.PutContextAsync(DateOnly.FromDateTime(DateTime.Now), new ContextRequest(q, null, null)),
            q == SleepQuality.Poor ? "Flagged as a higher-risk day." : "Thanks. Forecast updated.");

    public Task CheckInAsync(int level, IEnumerable<Trigger> context) =>
        ApplyAsync(() => api.CheckInAsync(new CheckInRequest(level, [.. context], null)), "Logged. Your forecast just updated.");

    public Task FeedbackAsync(ZoneDto zone, FeedbackVerdict verdict, int? hour) =>
        ApplyAsync(() => api.SendFeedbackAsync(new FeedbackRequest(DateOnly.FromDateTime(DateTime.Now), zone.StartHour, verdict, hour)),
            verdict switch
            {
                FeedbackVerdict.Accurate => "Confirmed. Those rules gained trust.",
                FeedbackVerdict.Missed => "Got it. That window will cool down.",
                _ => "Thanks. I'll shift that window."
            });

    private async Task ApplyAsync(Func<Task<ForecastDto>> call, string notice)
    {
        Forecast = await call();
        Pending = await api.GetPendingFeedbackAsync();
        Notice = notice;
        Changed?.Invoke();
    }
}
