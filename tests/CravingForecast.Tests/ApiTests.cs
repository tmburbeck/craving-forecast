using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CravingForecast.Core;
using CravingForecast.Core.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace CravingForecast.Tests;

/// <summary>The real API in memory, with its own throwaway SQLite database.</summary>
public sealed class SalusApi : WebApplicationFactory<Program>
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"salus-tests-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Salus"] = $"Data Source={_db}",
            ["Auth:RequestsPerMinute"] = "1000"
        }));

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        File.Delete(_db);
    }
}

public class ApiTests
{
    private const string Password = "correct horse battery";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static readonly OnboardingRequest Quiz = new(7, 23, WorkSchedule.Office, MealTime.Dinner, GrazeDriver.Boredom,
        SkippedMeal.None, [Trigger.Slump], CravingType.Sweet, NudgeStyle.Mix, "UTC");

    private static async Task<HttpClient> SignedIn(SalusApi api, string email)
    {
        var client = api.CreateClient();
        (await client.PostAsJsonAsync("/api/auth/register", new AuthRequest(email, Password))).EnsureSuccessStatusCode();
        var login = await client.PostAsJsonAsync("/api/auth/login", new AuthRequest(email, Password));
        login.EnsureSuccessStatusCode();
        var tokens = await login.Content.ReadFromJsonAsync<TokenResponse>(Json);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        return client;
    }

    [Fact]
    public async Task Personal_endpoints_require_sign_in()
    {
        using var api = new SalusApi();
        var anonymous = api.CreateClient();

        foreach (var url in new[] { "/api/me", "/api/me/forecast", "/api/me/nudges?days=2", "/api/me/feedback/pending" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/me/profile", Quiz, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/me/checkins", new CheckInRequest(5, null, null), Json)).StatusCode);
    }

    [Fact]
    public async Task Sign_up_take_the_quiz_and_get_a_forecast()
    {
        using var api = new SalusApi();
        var client = await SignedIn(api, "ana@example.com");

        var before = await client.GetFromJsonAsync<MeResponse>("/api/me", Json);
        Assert.Equal("ana@example.com", before!.Email);
        Assert.False(before.HasProfile);

        var created = await client.PostAsJsonAsync("/api/me/profile", Quiz, Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var forecast = await client.GetFromJsonAsync<ForecastDto>("/api/me/forecast", Json);
        Assert.NotEmpty(forecast!.Hours);
        Assert.True((await client.GetFromJsonAsync<MeResponse>("/api/me", Json))!.HasProfile);
    }

    [Fact]
    public async Task One_account_cannot_reach_another_accounts_data()
    {
        using var api = new SalusApi();
        var ana = await SignedIn(api, "ana@example.com");
        var created = await (await ana.PostAsJsonAsync("/api/me/profile", Quiz, Json)).Content.ReadFromJsonAsync<OnboardingResponse>(Json);
        (await ana.PostAsJsonAsync("/api/me/checkins", new CheckInRequest(8, [Trigger.Stress], null), Json)).EnsureSuccessStatusCode();

        var ben = await SignedIn(api, "ben@example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await ben.GetAsync("/api/me/forecast")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ben.PostAsJsonAsync("/api/me/checkins", new CheckInRequest(3, null, null), Json)).StatusCode);

        // The old id-in-the-address routes are gone, so knowing someone's id gets you nothing.
        Assert.Equal(HttpStatusCode.NotFound, (await ben.GetAsync($"/api/users/{created!.UserId}/forecast")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ana.GetAsync("/api/me/forecast")).StatusCode);
    }

    [Fact]
    public async Task The_quiz_can_only_be_taken_once()
    {
        using var api = new SalusApi();
        var client = await SignedIn(api, "ana@example.com");
        (await client.PostAsJsonAsync("/api/me/profile", Quiz, Json)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/me/profile", Quiz, Json)).StatusCode);
    }

    [Fact]
    public async Task Short_passwords_and_wrong_passwords_are_refused()
    {
        using var api = new SalusApi();
        var client = api.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/register", new AuthRequest("ana@example.com", "short"))).StatusCode);

        (await client.PostAsJsonAsync("/api/auth/register", new AuthRequest("ana@example.com", Password))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", new AuthRequest("ana@example.com", "not the password"))).StatusCode);
    }

    [Fact]
    public async Task Data_is_kept_in_the_database_between_requests()
    {
        using var api = new SalusApi();
        var client = await SignedIn(api, "ana@example.com");
        (await client.PostAsJsonAsync("/api/me/profile", Quiz, Json)).EnsureSuccessStatusCode();

        var today = (await client.GetFromJsonAsync<ForecastDto>("/api/me/forecast", Json))!.Date;
        await client.PutAsJsonAsync($"/api/me/context/{today:yyyy-MM-dd}", new ContextRequest(SleepQuality.Poor, null, null), Json);

        var after = await client.GetFromJsonAsync<ForecastDto>("/api/me/forecast", Json);
        Assert.Contains(after!.ContextFactors, f => f.Label.Contains("sleep", StringComparison.OrdinalIgnoreCase));
    }
}
