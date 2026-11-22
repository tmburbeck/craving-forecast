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

public sealed class ContactApi(string? forwardTo = "owner@example.com", int perHour = 100) : WebApplicationFactory<Program>
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"salus-contact-tests-{Guid.NewGuid():N}.db");
    public FakeMailbox Mail { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Salus"] = $"Data Source={_db}",
            ["Push:DispatchEnabled"] = "false",
            ["Contact:ForwardTo"] = forwardTo,
            ["Contact:MessagesPerHour"] = perHour.ToString()
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

public class ContactTests
{
    private const string Hello = "Hi, could I get a copy of my data please?";

    [Fact]
    public async Task A_message_reaches_the_owners_inbox_and_a_reply_goes_to_the_writer()
    {
        using var api = new ContactApi();
        var res = await api.CreateClient().PostAsJsonAsync("/api/contact", new ContactRequest("ana@example.com", Hello));

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        var mail = Assert.Single(api.Mail.Sent);
        Assert.Equal("owner@example.com", mail.To);
        Assert.Equal("ana@example.com", mail.ReplyTo);
        Assert.Contains(Hello, mail.Text);
    }

    [Fact]
    public async Task Markup_in_a_message_is_shown_as_text_not_run()
    {
        using var api = new ContactApi();
        await api.CreateClient().PostAsJsonAsync("/api/contact", new ContactRequest("ana@example.com", "<script>alert(1)</script> hello there"));
        var html = Assert.Single(api.Mail.Sent).Html;
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Theory]
    [InlineData("not an email", Hello, "email")]
    [InlineData("ana@example.com\nBcc: everyone@example.com", Hello, "email")]
    [InlineData("ana@example.com", "hi", "message")]
    public async Task Bad_input_is_refused_and_nothing_is_sent(string email, string message, string field)
    {
        using var api = new ContactApi();
        var res = await api.CreateClient().PostAsJsonAsync("/api/contact", new ContactRequest(email, message));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains($"\"{field}\"", await res.Content.ReadAsStringAsync());
        Assert.Empty(api.Mail.Sent);
    }

    [Fact]
    public async Task A_bot_filling_the_hidden_field_is_quietly_ignored()
    {
        using var api = new ContactApi();
        var res = await api.CreateClient().PostAsJsonAsync("/api/contact", new ContactRequest("bot@example.com", Hello, "https://spam.example"));
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.Empty(api.Mail.Sent);
    }

    [Fact]
    public async Task Too_many_messages_in_an_hour_are_turned_away()
    {
        using var api = new ContactApi(perHour: 2);
        var c = api.CreateClient();
        for (var i = 0; i < 2; i++)
            Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsJsonAsync("/api/contact", new ContactRequest("ana@example.com", Hello))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await c.PostAsJsonAsync("/api/contact", new ContactRequest("ana@example.com", Hello))).StatusCode);
        Assert.Equal(2, api.Mail.Sent.Count);
    }

    [Fact]
    public async Task Without_an_inbox_set_up_the_form_says_so_instead_of_pretending()
    {
        using var api = new ContactApi(forwardTo: null);
        var res = await api.CreateClient().PostAsJsonAsync("/api/contact", new ContactRequest("ana@example.com", Hello));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.Empty(api.Mail.Sent);
    }
}
