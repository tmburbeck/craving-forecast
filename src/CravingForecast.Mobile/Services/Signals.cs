using CravingForecast.Core;
using Plugin.Maui.CalendarStore;

namespace CravingForecast.Mobile.Services;

/// <summary>Sleep and activity from Apple Health or Health Connect. Null means "no data": fall back to the morning tap.</summary>
public interface IHealthSignals
{
    bool IsAvailable { get; }
    Task<SleepQuality?> GetLastNightSleepAsync();
    Task<ActivityLevel?> GetYesterdayActivityAsync();
}

public interface ICalendarSignals
{
    /// <summary>Null when calendar permission is denied.</summary>
    Task<bool?> HasBackToBackMeetingsAsync(DateOnly day);
}

public sealed class CalendarSignals(ICalendarStore store) : ICalendarSignals
{
    public async Task<bool?> HasBackToBackMeetingsAsync(DateOnly day)
    {
        var status = await Permissions.CheckStatusAsync<Permissions.CalendarRead>();
        if (status != PermissionStatus.Granted)
            status = await Permissions.RequestAsync<Permissions.CalendarRead>();
        if (status != PermissionStatus.Granted) return null;

        var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue));
        var events = await store.GetEvents(startDate: start, endDate: start.AddDays(1));
        return SignalClassifier.IsBackToBack(events.Where(e => !e.AllDay).Select(e => (e.StartDate, e.EndDate)));
    }
}
