using System.Net;
using System.Net.Http.Json;
using CravingForecast.Api;
using CravingForecast.Core.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CravingForecast.Tests;

/// <summary>Keeps emails instead of sending them.</summary>
public sealed class FakeMailbox : IEmailSender
{
    public List<OutgoingEmail> Sent { get; } = [];

    public Task SendAsync(OutgoingEmail email, CancellationToken ct = default)
    {
        lock (Sent) Sent.Add(email);
        return Task.CompletedTask;
    }

    /// <summary>The reset link in the last email to <paramref name="to"/>, as (email, code) query values.</summary>
    public (string Email, string Code, string Link) LastResetLink(string to)
    {
        var text = Sent.Last(m => m.To == to).Text;
        var link = text.Split('\n').Select(l => l.Trim()).Single(l => l.Contains("/reset-password?"));
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(link).Query);
        return (query["email"]!, query["code"]!, link);
    }
}

public sealed class ResetApi : WebApplicationFactory<Program>
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"salus-reset-tests-{Guid.NewGuid():N}.db");
    public FakeMailbox Mail { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Salus"] = $"Data Source={_db}",
            ["Auth:RequestsPerMinute"] = "1000",
            ["Push:DispatchEnabled"] = "false",
            ["App:BaseUrl"] = "https://salus.example.org"
        }));
        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<IEmailSender>();
            s.AddSingleton<IEmailSender>(Mail);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        File.Delete(_db);
    }
}

public class PasswordResetTests
{
    private const string Old = "correct horse battery";
    private const string New = "a brand new passphrase";

    private static async Task<HttpClient> Registered(ResetApi api, string email)
    {
        var c = api.CreateClient();
        (await c.PostAsJsonAsync("/api/auth/register", new AuthRequest(email, Old))).EnsureSuccessStatusCode();
        return c;
    }

    private static async Task<HttpStatusCode> Login(HttpClient c, string email, string password) =>
        (await c.PostAsJsonAsync("/api/auth/login", new AuthRequest(email, password))).StatusCode;

    [Fact]
    public async Task Asking_for_a_reset_looks_the_same_whether_or_not_the_account_exists()
    {
        using var api = new ResetApi();
        var c = await Registered(api, "ana@example.com");

        var known = await c.PostAsJsonAsync("/api/auth/forgot", new ForgotPasswordRequest("ana@example.com"));
        var unknown = await c.PostAsJsonAsync("/api/auth/forgot", new ForgotPasswordRequest("nobody@example.com"));

        Assert.Equal(HttpStatusCode.OK, known.StatusCode);
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
        Assert.Equal(await known.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
        Assert.Single(api.Mail.Sent);
        Assert.Equal("ana@example.com", api.Mail.Sent[0].To);
    }

    [Fact]
    public async Task The_emailed_link_sets_a_new_password_and_works_only_once()
    {
        using var api = new ResetApi();
        var c = await Registered(api, "ana@example.com");
        await c.PostAsJsonAsync("/api/auth/forgot", new ForgotPasswordRequest("ana@example.com"));
        var (email, code, link) = api.Mail.LastResetLink("ana@example.com");
        Assert.StartsWith("https://salus.example.org/reset-password?", link);

        var reset = await c.PostAsJsonAsync("/api/auth/reset", new ResetPasswordRequest(email, code, New));
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await Login(c, "ana@example.com", Old));
        Assert.Equal(HttpStatusCode.OK, await Login(c, "ana@example.com", New));

        var again = await c.PostAsJsonAsync("/api/auth/reset", new ResetPasswordRequest(email, code, "yet another passphrase"));
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Contains("expired or was already used", await again.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_tampered_or_borrowed_code_is_refused()
    {
        using var api = new ResetApi();
        var c = await Registered(api, "ana@example.com");
        await Registered(api, "ben@example.com");
        await c.PostAsJsonAsync("/api/auth/forgot", new ForgotPasswordRequest("ana@example.com"));
        var (_, code, _) = api.Mail.LastResetLink("ana@example.com");

        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/auth/reset", new ResetPasswordRequest("ana@example.com", code[..^4] + "AAAA", New))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/auth/reset", new ResetPasswordRequest("ana@example.com", "not-base64!!", New))).StatusCode);
        // Ana's code can't reset Ben's password.
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/auth/reset", new ResetPasswordRequest("ben@example.com", code, New))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, await Login(c, "ben@example.com", Old));
    }

    [Fact]
    public async Task A_short_new_password_is_refused_and_the_link_still_works_after()
    {
        using var api = new ResetApi();
        var c = await Registered(api, "ana@example.com");
        await c.PostAsJsonAsync("/api/auth/forgot", new ForgotPasswordRequest("ana@example.com"));
        var (email, code, _) = api.Mail.LastResetLink("ana@example.com");

        var weak = await c.PostAsJsonAsync("/api/auth/reset", new ResetPasswordRequest(email, code, "short"));
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.Contains("at least 10", await weak.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsJsonAsync("/api/auth/reset", new ResetPasswordRequest(email, code, New))).StatusCode);
    }

    [Fact]
    public async Task Resetting_unlocks_an_account_locked_by_wrong_guesses()
    {
        using var api = new ResetApi();
        var c = await Registered(api, "ana@example.com");
        for (var i = 0; i < 5; i++) await Login(c, "ana@example.com", "wrong guess number " + i);
        Assert.Equal(HttpStatusCode.Unauthorized, await Login(c, "ana@example.com", Old)); // locked, even with the right password

        await c.PostAsJsonAsync("/api/auth/forgot", new ForgotPasswordRequest("ana@example.com"));
        var (email, code, _) = api.Mail.LastResetLink("ana@example.com");
        (await c.PostAsJsonAsync("/api/auth/reset", new ResetPasswordRequest(email, code, New))).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.OK, await Login(c, "ana@example.com", New));
    }

    [Fact]
    public async Task The_link_points_at_the_real_site_even_if_the_request_claims_another_host()
    {
        using var api = new ResetApi();
        var c = await Registered(api, "ana@example.com");
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/forgot") { Content = JsonContent.Create(new ForgotPasswordRequest("ana@example.com")) };
        request.Headers.Host = "attacker.example";
        await c.SendAsync(request);

        var (_, _, link) = api.Mail.LastResetLink("ana@example.com");
        Assert.StartsWith("https://salus.example.org/", link);
        Assert.DoesNotContain("attacker", api.Mail.Sent.Last().Html);
    }
}
