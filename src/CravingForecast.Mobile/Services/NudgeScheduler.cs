using CravingForecast.Core.Contracts;
using Plugin.LocalNotification;
using Plugin.LocalNotification.Core.Models;

namespace CravingForecast.Mobile.Services;

/// <summary>
/// Schedules nudges as local notifications on the phone, so they fire even when the app is closed
/// and no push server is needed. Also schedules a "Was this accurate?" prompt after each window.
/// </summary>
public sealed class NudgeScheduler
{
    public static readonly TimeSpan FeedbackDelay = TimeSpan.FromMinutes(15);

    public async Task<bool> EnsurePermissionAsync()
    {
        if (await LocalNotificationCenter.Current.AreNotificationsEnabled()) return true;
        return await LocalNotificationCenter.Current.RequestNotificationPermission();
    }

    public async Task RescheduleAsync(IReadOnlyList<NudgeDto> nudges)
    {
        if (!await EnsurePermissionAsync()) return;
        LocalNotificationCenter.Current.CancelAll();

        var id = 1000;
        foreach (var n in nudges)
        {
            await LocalNotificationCenter.Current.Show(new NotificationRequest
            {
                NotificationId = id++,
                Title = n.Title,
                Description = n.Body,
                ReturningData = "nudge",
                Schedule = new NotificationRequestSchedule { NotifyTime = n.SendAt.LocalDateTime }
            });

            var zoneEnd = n.SendAt + TimeSpan.FromMinutes(30) + TimeSpan.FromHours(n.Zone.EndHour - n.Zone.StartHour + 1);
            if (n.Zone.EndHour < 24)
                await LocalNotificationCenter.Current.Show(new NotificationRequest
                {
                    NotificationId = id++,
                    Title = "Was this accurate?",
                    Description = $"How did your {n.Zone.Label} window go? One tap helps the forecast learn.",
                    ReturningData = "feedback",
                    Schedule = new NotificationRequestSchedule { NotifyTime = (zoneEnd + FeedbackDelay).LocalDateTime }
                });
        }
    }
}
