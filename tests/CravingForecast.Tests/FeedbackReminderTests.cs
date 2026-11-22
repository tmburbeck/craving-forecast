using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CravingForecast.Api;
using CravingForecast.Core;
using CravingForecast.Core.Contracts;

namespace CravingForecast.Tests;

public class FeedbackReminderTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static readonly OnboardingRequest Quiz = new(7, 23, WorkSchedule.Office, MealTime.Dinner, GrazeDriver.Boredom,
        SkippedMeal.None, [Trigger.Slump, Trigger.Tv], CravingType.Sweet, NudgeStyle.Mix, "UTC");

    private static readonly DateOnly Day = new(2026, 6, 1); // PushApi's clock starts at midnight UTC on this day

    private static async Task<HttpClient> SignIn(PushApi api, string email, bool register = false)
    {
        var c = api.CreateClient();
        if (register) (await c.PostAsJsonAsync("/api/auth/register", new AuthRequest(email, "correct horse battery"))).EnsureSuccessStatusCode();
        var tokens = await (await c.PostAsJsonAsync("/api/auth/login", new AuthRequest(email, "correct horse battery"))).Content.ReadFromJsonAsync<TokenResponse>(Json);
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        return c;
    }

    /// <summary>A subscribed person and the first of today's zones that gets a reminder.</summary>
    private static async Task<ZoneDto> Setup(PushApi api, string email)
    {
        var c = await SignIn(api, email, register: true);
        (await c.PostAsJsonAsync("/api/me/profile", Quiz, Json)).EnsureSuccessStatusCode();
        (await c.PostAsJsonAsync("/api/me/push/subscriptions",
            new PushSubscriptionRequest("https://push.example.com/" + email, new PushKeys("p256dh-key", "auth-secret")), Json)).EnsureSuccessStatusCode();
        var forecast = await c.GetFromJsonAsync<ForecastDto>("/api/me/forecast", Json);
        return forecast!.Zones.First(z => z.EndHour + 1 < FeedbackReminders.QuietFromHour);
    }

    private static DateTimeOffset ZoneEnd(ZoneDto z) => new(Day.ToDateTime(new TimeOnly(z.EndHour + 1, 0)), TimeSpan.Zero);

    private static List<PushMessage> Reminders(PushApi api) =>
        api.Push.Sent.Select(s => s.Message).Where(m => m.Tag.StartsWith("feedback-")).ToList();

    [Fact]
    public async Task A_reminder_follows_each_zone_once_after_it_ends()
    {
        using var api = new PushApi();
        var zone = await Setup(api, "ana@example.com");

        api.Clock.Now = ZoneEnd(zone).AddMinutes(10);
        await api.DispatchAsync();
        Assert.Empty(Reminders(api));

        api.Clock.Now = ZoneEnd(zone) + FeedbackReminders.Delay + TimeSpan.FromMinutes(1);
        await api.DispatchAsync();
        var reminder = Assert.Single(Reminders(api));
        Assert.Equal($"How did {zone.Label} go?", reminder.Title);
        Assert.Equal("/today", reminder.Url);

        api.Clock.Now = api.Clock.Now.AddMinutes(1);
        await api.DispatchAsync();
        Assert.Single(Reminders(api));
    }

    [Fact]
    public async Task A_zone_already_rated_gets_no_reminder()
    {
        using var api = new PushApi();
        var zone = await Setup(api, "ana@example.com");

        api.Clock.Now = ZoneEnd(zone).AddMinutes(5);
        var c = await SignIn(api, "ana@example.com");
        (await c.PostAsJsonAsync("/api/me/feedback", new FeedbackRequest(Day, zone.StartHour, FeedbackVerdict.Accurate, null), Json)).EnsureSuccessStatusCode();

        api.Clock.Now = ZoneEnd(zone) + FeedbackReminders.Delay + TimeSpan.FromMinutes(1);
        await api.DispatchAsync();
        Assert.Empty(Reminders(api));
    }

    [Fact]
    public void Late_night_zones_get_no_reminder_and_times_follow_the_users_time_zone()
    {
        var profile = new OnboardingProfile(7, 23, WorkSchedule.Office, MealTime.Dinner, GrazeDriver.Boredom, SkippedMeal.None,
            [Trigger.Tv], CravingType.Sweet, NudgeStyle.Mix, Day, "America/Los_Angeles");
        DangerZone Zone(int start, int end) => new(start, end, Trigger.Tv, 0.7, Enumerable.Range(start, end - start + 1).ToArray());
        var forecast = new Forecast(Day, 1, "", 1, 0, [], [], [], [], [Zone(14, 15), Zone(20, 21), Zone(21, 22), Zone(22, 23)]);

        var plan = FeedbackReminders.Plan(profile, forecast);

        // 2–4 PM and 8–10 PM are asked about; zones ending at 11 PM or midnight are not.
        Assert.Equal([14, 20], plan.Select(r => r.Zone.StartHour));
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 16, 15, 0, TimeSpan.FromHours(-7)), plan[0].SendAt);
    }
}
