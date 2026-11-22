using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CravingForecast.Api;
using CravingForecast.Core;
using CravingForecast.Core.Contracts;

namespace CravingForecast.Tests;

public class MidZoneCheckInTests
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

    /// <summary>A subscribed person and the first of today's zones long enough for a mid-zone prompt.</summary>
    private static async Task<ZoneDto> Setup(PushApi api, string email)
    {
        var c = await SignIn(api, email, register: true);
        (await c.PostAsJsonAsync("/api/me/profile", Quiz, Json)).EnsureSuccessStatusCode();
        (await c.PostAsJsonAsync("/api/me/push/subscriptions",
            new PushSubscriptionRequest("https://push.example.com/" + email, new PushKeys("p256dh-key", "auth-secret")), Json)).EnsureSuccessStatusCode();
        var forecast = await c.GetFromJsonAsync<ForecastDto>("/api/me/forecast", Json);
        return forecast!.Zones.First(z => z.EndHour - z.StartHour + 1 >= MidZoneCheckIns.MinHours && z.EndHour < 22);
    }

    private static DateTimeOffset At(int hour) => new(Day.ToDateTime(new TimeOnly(hour, 0)), TimeSpan.Zero);
    private static DateTimeOffset Midpoint(ZoneDto z) => At(z.StartHour).AddMinutes((z.EndHour - z.StartHour + 1) * 30);

    private static List<PushMessage> Prompts(PushApi api) =>
        api.Push.Sent.Select(s => s.Message).Where(m => m.Tag.StartsWith("checkin-")).ToList();

    [Fact]
    public async Task Halfway_through_a_zone_one_prompt_opens_the_check_in()
    {
        using var api = new PushApi();
        var zone = await Setup(api, "ana@example.com");

        api.Clock.Now = Midpoint(zone).AddMinutes(-5);
        await api.DispatchAsync();
        Assert.Empty(Prompts(api));

        api.Clock.Now = Midpoint(zone).AddMinutes(1);
        await api.DispatchAsync();
        var prompt = Assert.Single(Prompts(api));
        Assert.Equal($"Halfway through {zone.Label}", prompt.Title);
        Assert.Equal("/checkin", prompt.Url);

        api.Clock.Now = Midpoint(zone).AddMinutes(2);
        await api.DispatchAsync();
        Assert.Single(Prompts(api));
    }

    [Fact]
    public async Task Someone_who_already_checked_in_during_the_zone_is_left_alone()
    {
        using var api = new PushApi();
        var zone = await Setup(api, "ana@example.com");

        api.Clock.Now = At(zone.StartHour).AddMinutes(10);
        var c = await SignIn(api, "ana@example.com");
        (await c.PostAsJsonAsync("/api/me/checkins", new CheckInRequest(6, [], null), Json)).EnsureSuccessStatusCode();

        api.Clock.Now = Midpoint(zone).AddMinutes(1);
        await api.DispatchAsync();
        Assert.Empty(Prompts(api));
    }

    [Fact]
    public async Task A_check_in_from_before_the_zone_doesnt_count()
    {
        using var api = new PushApi();
        var zone = await Setup(api, "ana@example.com");

        api.Clock.Now = At(zone.StartHour).AddMinutes(-20);
        var c = await SignIn(api, "ana@example.com");
        (await c.PostAsJsonAsync("/api/me/checkins", new CheckInRequest(3, [], null), Json)).EnsureSuccessStatusCode();

        api.Clock.Now = Midpoint(zone).AddMinutes(1);
        await api.DispatchAsync();
        Assert.Single(Prompts(api));
    }

    [Fact]
    public void Short_and_late_zones_are_skipped_and_times_follow_the_users_time_zone()
    {
        var profile = new OnboardingProfile(7, 23, WorkSchedule.Office, MealTime.Dinner, GrazeDriver.Boredom, SkippedMeal.None,
            [Trigger.Tv], CravingType.Sweet, NudgeStyle.Mix, Day, "America/Los_Angeles");
        DangerZone Zone(int start, int end) => new(start, end, Trigger.Tv, 0.7, Enumerable.Range(start, end - start + 1).ToArray());
        var forecast = new Forecast(Day, 1, "", 1, 0, [], [], [], [],
            [Zone(10, 10), Zone(14, 16), Zone(21, 22), Zone(22, 23), Zone(24, 25)]);

        var plan = MidZoneCheckIns.Plan(profile, forecast);

        // 10–11 AM is too short; 10 PM–midnight would land at 11 PM; the after-midnight zone isn't today's.
        Assert.Equal([14, 21], plan.Select(p => p.Zone.StartHour));
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 15, 30, 0, TimeSpan.FromHours(-7)), plan[0].SendAt);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 22, 0, 0, TimeSpan.FromHours(-7)), plan[1].SendAt);
    }
}
