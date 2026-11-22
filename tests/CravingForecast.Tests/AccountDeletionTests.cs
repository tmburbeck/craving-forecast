using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CravingForecast.Api;
using CravingForecast.Core;
using CravingForecast.Core.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CravingForecast.Tests;

public class AccountDeletionTests
{
    private const string Password = "correct horse battery";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static readonly OnboardingRequest Quiz = new(7, 23, WorkSchedule.Office, MealTime.Dinner, GrazeDriver.Boredom,
        SkippedMeal.None, [Trigger.Slump], CravingType.Sweet, NudgeStyle.Mix, "UTC");

    /// <summary>A signed-in person with a forecast, a check-in and a notification subscription.</summary>
    private static async Task<HttpClient> PersonWithData(SalusApi api, string email)
    {
        var c = api.CreateClient();
        (await c.PostAsJsonAsync("/api/auth/register", new AuthRequest(email, Password))).EnsureSuccessStatusCode();
        var tokens = await (await c.PostAsJsonAsync("/api/auth/login", new AuthRequest(email, Password))).Content.ReadFromJsonAsync<TokenResponse>(Json);
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        (await c.PostAsJsonAsync("/api/me/profile", Quiz, Json)).EnsureSuccessStatusCode();
        (await c.PostAsJsonAsync("/api/me/checkins", new CheckInRequest(3, [], null), Json)).EnsureSuccessStatusCode();
        (await c.PostAsJsonAsync("/api/me/push/subscriptions",
            new PushSubscriptionRequest($"https://push.example/{Guid.NewGuid():N}", new PushKeys("p256dh", "auth")), Json)).EnsureSuccessStatusCode();
        return c;
    }

    private static async Task<(int Users, int States, int Subscriptions)> Counts(SalusApi api)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.Users.CountAsync(), await db.States.CountAsync(), await db.PushSubscriptions.CountAsync());
    }

    [Fact]
    public async Task The_wrong_password_deletes_nothing()
    {
        using var api = new SalusApi();
        var ana = await PersonWithData(api, "ana@example.com");

        var res = await ana.PostAsJsonAsync("/api/me/delete", new DeleteAccountRequest("not my password"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("isn't right", await res.Content.ReadAsStringAsync());
        Assert.Equal((1, 1, 1), await Counts(api));
        Assert.Equal(HttpStatusCode.OK, (await ana.GetAsync("/api/me/forecast")).StatusCode);
    }

    [Fact]
    public async Task Deleting_erases_the_account_and_everything_kept_with_it_and_no_one_else()
    {
        using var api = new SalusApi();
        var ana = await PersonWithData(api, "ana@example.com");
        await PersonWithData(api, "ben@example.com");

        Assert.Equal(HttpStatusCode.NoContent, (await ana.PostAsJsonAsync("/api/me/delete", new DeleteAccountRequest(Password))).StatusCode);

        Assert.Equal((1, 1, 1), await Counts(api)); // only Ben's are left
        var fresh = api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await fresh.PostAsJsonAsync("/api/auth/login", new AuthRequest("ana@example.com", Password))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fresh.PostAsJsonAsync("/api/auth/login", new AuthRequest("ben@example.com", Password))).StatusCode);
    }

    [Fact]
    public async Task An_old_token_is_signed_out_and_the_email_can_sign_up_again_from_scratch()
    {
        using var api = new SalusApi();
        var ana = await PersonWithData(api, "ana@example.com");
        (await ana.PostAsJsonAsync("/api/me/delete", new DeleteAccountRequest(Password))).EnsureSuccessStatusCode();

        // The same token, as another device still holding it would send it.
        Assert.Equal(HttpStatusCode.Unauthorized, (await ana.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ana.PostAsJsonAsync("/api/me/profile", Quiz, Json)).StatusCode);

        var again = api.CreateClient();
        (await again.PostAsJsonAsync("/api/auth/register", new AuthRequest("ana@example.com", Password))).EnsureSuccessStatusCode();
        var tokens = await (await again.PostAsJsonAsync("/api/auth/login", new AuthRequest("ana@example.com", Password))).Content.ReadFromJsonAsync<TokenResponse>(Json);
        again.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        var me = await again.GetFromJsonAsync<MeResponse>("/api/me", Json);
        Assert.False(me!.HasProfile);
    }

    [Fact]
    public async Task Deleting_needs_sign_in()
    {
        using var api = new SalusApi();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await api.CreateClient().PostAsJsonAsync("/api/me/delete", new DeleteAccountRequest(Password))).StatusCode);
    }
}
