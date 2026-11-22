using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CravingForecast.Api;
using CravingForecast.Core;
using CravingForecast.Core.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CravingForecast.Tests;

/// <summary>A clock the test moves by hand.</summary>
public sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Records notifications instead of sending them.</summary>
public sealed class FakePushSender : IPushSender
{
    public List<(string Endpoint, PushMessage Message)> Sent { get; } = [];
    public HashSet<string> GoneEndpoints { get; } = [];

    public Task<PushResult> SendAsync(PushSubscriptionRow s, PushMessage m, CancellationToken ct = default)
    {
        if (GoneEndpoints.Contains(s.Endpoint)) return Task.FromResult(PushResult.Gone);
        lock (Sent) Sent.Add((s.Endpoint, m));
        return Task.FromResult(PushResult.Sent);
    }
}

/// <summary>The API with a hand-moved clock, a fake push service and no background dispatcher.</summary>
public sealed class PushApi : WebApplicationFactory<Program>
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"salus-push-tests-{Guid.NewGuid():N}.db");
    public ManualClock Clock { get; } = new(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
    public FakePushSender Push { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Salus"] = $"Data Source={_db}",
            ["Auth:RequestsPerMinute"] = "1000",
            ["Push:DispatchEnabled"] = "false" // tests run the dispatcher by hand
        }));
        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(Clock);
            s.RemoveAll<IPushSender>();
            s.AddSingleton<IPushSender>(Push);
        });
    }

    public Task<int> DispatchAsync() => Services.GetRequiredService<NudgeDispatcher>().RunOnceAsync();

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        File.Delete(_db);
    }
}

public class PushTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static readonly OnboardingRequest Quiz = new(7, 23, WorkSchedule.Office, MealTime.Dinner, GrazeDriver.Boredom,
        SkippedMeal.None, [Trigger.Slump, Trigger.Tv], CravingType.Sweet, NudgeStyle.Mix, "UTC");

    private static async Task<HttpClient> Onboarded(PushApi api, string email)
    {
        var c = api.CreateClient();
        (await c.PostAsJsonAsync("/api/auth/register", new AuthRequest(email, "correct horse battery"))).EnsureSuccessStatusCode();
        var tokens = await (await c.PostAsJsonAsync("/api/auth/login", new AuthRequest(email, "correct horse battery"))).Content.ReadFromJsonAsync<TokenResponse>(Json);
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        (await c.PostAsJsonAsync("/api/me/profile", Quiz, Json)).EnsureSuccessStatusCode();
        return c;
    }

    /// <summary>Signs in again: moving the clock forward hours also expires earlier one-hour tokens.</summary>
    private static async Task<HttpClient> SignIn(PushApi api, string email)
    {
        var c = api.CreateClient();
        var tokens = await (await c.PostAsJsonAsync("/api/auth/login", new AuthRequest(email, "correct horse battery"))).Content.ReadFromJsonAsync<TokenResponse>(Json);
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        return c;
    }

    private static Task<HttpResponseMessage> Subscribe(HttpClient c, string endpoint) =>
        c.PostAsJsonAsync("/api/me/push/subscriptions", new PushSubscriptionRequest(endpoint, new PushKeys("p256dh-key", "auth-secret")), Json);

    private static async Task<NudgeDto> FirstNudge(HttpClient c) =>
        (await c.GetFromJsonAsync<List<NudgeDto>>("/api/me/nudges?days=1", Json))!.First();

    [Fact]
    public async Task The_public_key_is_made_once_and_kept()
    {
        using var api = new PushApi();
        var c = api.CreateClient();
        var first = await c.GetFromJsonAsync<PushPublicKeyResponse>("/api/push/public-key", Json);
        var second = await c.GetFromJsonAsync<PushPublicKeyResponse>("/api/push/public-key", Json);
        Assert.False(string.IsNullOrWhiteSpace(first!.PublicKey));
        Assert.Equal(first.PublicKey, second!.PublicKey);
    }

    [Fact]
    public async Task Subscribing_needs_sign_in_and_a_real_https_endpoint()
    {
        using var api = new PushApi();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Subscribe(api.CreateClient(), "https://push.example.com/a")).StatusCode);

        var c = await Onboarded(api, "ana@example.com");
        Assert.Equal(HttpStatusCode.BadRequest, (await Subscribe(c, "http://push.example.com/a")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Subscribe(c, "https://push.example.com/a")).StatusCode);
    }

    [Fact]
    public async Task A_nudge_is_pushed_once_when_its_time_comes()
    {
        using var api = new PushApi();
        var c = await Onboarded(api, "ana@example.com");
        await Subscribe(c, "https://push.example.com/ana-phone");
        var nudge = await FirstNudge(c);

        api.Clock.Now = nudge.SendAt.AddMinutes(-5);
        Assert.Equal(0, await api.DispatchAsync());

        api.Clock.Now = nudge.SendAt.AddMinutes(1);
        Assert.Equal(1, await api.DispatchAsync());
        Assert.Equal(nudge.Title, api.Push.Sent.Single().Message.Title);
        Assert.Equal("/today", api.Push.Sent.Single().Message.Url);

        api.Clock.Now = nudge.SendAt.AddMinutes(2);
        Assert.Equal(0, await api.DispatchAsync());
        Assert.Single(api.Push.Sent);
    }

    [Fact]
    public async Task A_server_that_slept_through_a_nudge_catches_up_but_not_hours_later()
    {
        using var api = new PushApi();
        var c = await Onboarded(api, "ana@example.com");
        await Subscribe(c, "https://push.example.com/ana-phone");
        var nudge = await FirstNudge(c);

        api.Clock.Now = nudge.SendAt + NudgeDispatcher.CatchUp + TimeSpan.FromMinutes(5);
        Assert.Equal(0, await api.DispatchAsync());

        using var api2 = new PushApi();
        var c2 = await Onboarded(api2, "ben@example.com");
        await Subscribe(c2, "https://push.example.com/ben-phone");
        var nudge2 = await FirstNudge(c2);
        api2.Clock.Now = nudge2.SendAt.AddMinutes(12);
        Assert.Equal(1, await api2.DispatchAsync());
    }

    [Fact]
    public async Task Nudges_only_reach_their_owners_devices_and_dead_subscriptions_are_dropped()
    {
        using var api = new PushApi();
        var ana = await Onboarded(api, "ana@example.com");
        var ben = await Onboarded(api, "ben@example.com");
        await Subscribe(ana, "https://push.example.com/ana-phone");
        await Subscribe(ana, "https://push.example.com/ana-laptop");
        await Subscribe(ben, "https://push.example.com/ben-phone");
        api.Push.GoneEndpoints.Add("https://push.example.com/ana-laptop");

        var nudge = await FirstNudge(ana);
        api.Clock.Now = nudge.SendAt.AddMinutes(1);
        await api.DispatchAsync();

        Assert.Contains(api.Push.Sent, s => s.Endpoint == "https://push.example.com/ana-phone");
        Assert.DoesNotContain(api.Push.Sent, s => s.Endpoint == "https://push.example.com/ana-laptop");
        // Ben has the same quiz answers, so he gets his own copy of the same nudge, on his own device only.
        Assert.All(api.Push.Sent.Where(s => s.Endpoint.Contains("ben")), s => Assert.Equal("https://push.example.com/ben-phone", s.Endpoint));

        // The laptop said it's gone, so it's removed and the test notification only reaches the phone.
        ana = await SignIn(api, "ana@example.com");
        var test = await (await ana.PostAsync("/api/me/push/test", null)).Content.ReadFromJsonAsync<PushTestResponse>(Json);
        Assert.Equal(1, test!.Sent);
    }

    [Fact]
    public async Task Unsubscribing_stops_nudges_and_a_shared_browser_follows_whoever_signs_in()
    {
        using var api = new PushApi();
        var ana = await Onboarded(api, "ana@example.com");
        var ben = await Onboarded(api, "ben@example.com");
        const string sharedBrowser = "https://push.example.com/family-ipad";

        await Subscribe(ana, sharedBrowser);
        await Subscribe(ben, sharedBrowser); // Ben signs in on the same iPad afterwards
        Assert.Equal(0, (await (await ana.PostAsync("/api/me/push/test", null)).Content.ReadFromJsonAsync<PushTestResponse>(Json))!.Sent);
        Assert.Equal(1, (await (await ben.PostAsync("/api/me/push/test", null)).Content.ReadFromJsonAsync<PushTestResponse>(Json))!.Sent);

        (await ben.PostAsJsonAsync("/api/me/push/unsubscribe", new PushUnsubscribeRequest(sharedBrowser), Json)).EnsureSuccessStatusCode();
        Assert.Equal(0, (await (await ben.PostAsync("/api/me/push/test", null)).Content.ReadFromJsonAsync<PushTestResponse>(Json))!.Sent);
    }
}

public class WebPushSenderTests
{
    private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public async Task The_real_sender_posts_a_signed_encrypted_message_and_spots_dead_subscriptions()
    {
        using var api = new PushApi();
        var keys = api.Services.GetRequiredService<VapidKeyStore>();
        var config = api.Services.GetRequiredService<IConfiguration>();
        var sender = new WebPushSender(keys, config, Microsoft.Extensions.Logging.Abstractions.NullLogger<WebPushSender>.Instance);

        // A real browser-style key pair for the subscription, so encryption actually runs.
        using var browserKey = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var p = browserKey.ExportParameters(false);
        var p256dh = B64Url([0x04, .. p.Q.X!, .. p.Q.Y!]);
        var auth = B64Url(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

        // A stand-in push service on this machine.
        // Let the OS pick a free port, so this never collides with anything else listening.
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var requests = new List<(string Path, string? Auth, string? Encoding, string? Ttl, int Length)>();
        var serve = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                var ctx = await listener.GetContextAsync();
                using var ms = new MemoryStream();
                await ctx.Request.InputStream.CopyToAsync(ms);
                requests.Add((ctx.Request.Url!.AbsolutePath, ctx.Request.Headers["Authorization"], ctx.Request.Headers["Content-Encoding"], ctx.Request.Headers["TTL"], (int)ms.Length));
                ctx.Response.StatusCode = ctx.Request.Url.AbsolutePath.EndsWith("gone") ? 410 : 201;
                ctx.Response.Close();
            }
        });

        var message = new PushMessage("Afternoon slump ahead", "Greek yogurt with berries now.", "/today", "t1");
        var ok = await sender.SendAsync(new PushSubscriptionRow { Endpoint = $"http://127.0.0.1:{port}/live", P256dh = p256dh, Auth = auth }, message);
        var gone = await sender.SendAsync(new PushSubscriptionRow { Endpoint = $"http://127.0.0.1:{port}/gone", P256dh = p256dh, Auth = auth }, message);
        await serve;

        Assert.Equal(PushResult.Sent, ok);
        Assert.Equal(PushResult.Gone, gone);
        var first = requests[0];
        Assert.StartsWith("vapid ", first.Auth ?? "", StringComparison.OrdinalIgnoreCase); // signed with this server's key
        Assert.Equal("aes128gcm", first.Encoding);                                          // encrypted for the browser
        Assert.False(string.IsNullOrEmpty(first.Ttl));
        Assert.True(first.Length > 100, "an encrypted body was sent");
    }
}
