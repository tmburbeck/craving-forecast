using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using CravingForecast.Api;
using CravingForecast.Core;
using CravingForecast.Core.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<JsonOptions>(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ForecastService>();

// ---------- Database: accounts, forecast state and token keys in one SQLite file ----------
// Read when the context is first created (not at startup) so hosts and tests can point it elsewhere.
builder.Services.AddDbContext<AppDbContext>((sp, o) =>
    o.UseSqlite(sp.GetRequiredService<IConfiguration>().GetConnectionString("Salus") ?? "Data Source=salus.db"));
builder.Services.AddScoped<IUserStore, DbUserStore>();

// ---------- Accounts: email + password, bearer tokens for the apps, cookies for a future web front end ----------
builder.Services.AddAuthorization();
builder.Services.AddIdentityApiEndpoints<AppUser>(o =>
    {
        // Long passwords over composition rules (NIST SP 800-63B); lock out after repeated failures.
        o.Password.RequiredLength = 10;
        o.Password.RequireDigit = false;
        o.Password.RequireLowercase = false;
        o.Password.RequireUppercase = false;
        o.Password.RequireNonAlphanumeric = false;
        o.User.RequireUniqueEmail = true;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddEntityFrameworkStores<AppDbContext>();

// Tokens are signed with data-protection keys; keeping them in the database means a restart doesn't sign everyone out.
builder.Services.AddDataProtection().PersistKeysToDbContext<AppDbContext>().SetApplicationName("Salus");

// Slow down password guessing on the sign-in and sign-up endpoints, per client address.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = ctx.RequestServices.GetRequiredService<IConfiguration>().GetValue("Auth:RequestsPerMinute", 10),
            Window = TimeSpan.FromMinutes(1)
        }));
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();

app.UseRateLimiter();

var api = app.MapGroup("/api");

api.MapGet("/archetypes", () => ArchetypeCatalog.All.Select(a => new { a.Id, a.Name, a.Description }));

// /api/auth/register, /login, /refresh and the account-management endpoints.
api.MapGroup("/auth").MapIdentityApi<AppUser>().RequireRateLimiting("auth");

// Everything about a person lives under /api/me and is always the signed-in user's own data.
var me = api.MapGroup("/me").RequireAuthorization();

static Guid UserId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

me.MapGet("", async (ClaimsPrincipal user, IUserStore store) =>
    Results.Ok(new MeResponse(user.FindFirstValue(ClaimTypes.Email) ?? user.Identity?.Name ?? "", await store.GetAsync(UserId(user)) is not null)));

// ---------- Onboarding (Layer 1) ----------
me.MapPost("/profile", async (OnboardingRequest r, ClaimsPrincipal user, IUserStore store, ForecastService svc) =>
{
    if (r.WakeHour is < 0 or > 23 || r.BedHour is < 0 or > 23)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["hours"] = ["Wake and bed hours must be 0–23."] });

    var tzId = string.IsNullOrWhiteSpace(r.TimeZoneId) ? "America/Chicago" : r.TimeZoneId;
    if (!TimeZoneInfo.TryFindSystemTimeZoneById(tzId, out var tz))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["timeZoneId"] = [$"Unknown time zone '{tzId}'."] });

    var id = UserId(user);
    if (await store.GetAsync(id) is not null)
        return Results.Conflict(new { message = "This account has already done the quiz." });

    var start = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(svc.Now, tz).DateTime);
    var profile = new OnboardingProfile(r.WakeHour, r.BedHour, r.Schedule, r.LargestMeal, r.Graze, r.Skip,
        r.Triggers ?? [], r.CravingType, r.NudgeStyle, start, tzId);

    var state = new UserState { Id = id, Profile = profile };
    await store.CreateAsync(state);
    return Results.Created("/api/me", new OnboardingResponse(id, svc.Dto(state, start)));
});

// ---------- Forecast ----------
me.MapGet("/forecast", async (DateOnly? date, ClaimsPrincipal user, IUserStore store, ForecastService svc) =>
    await store.GetAsync(UserId(user)) is { } u
        ? Results.Ok(svc.Dto(u, date ?? svc.Today(u.Profile)))
        : Results.NotFound());

// ---------- Check-ins (Layer 3 input) ----------
me.MapPost("/checkins", async (CheckInRequest r, ClaimsPrincipal user, IUserStore store, ForecastService svc) =>
{
    if (r.Level is < 1 or > 10)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["level"] = ["Level must be 1–10."] });

    var dto = await store.UpdateAsync(UserId(user), u =>
    {
        u.CheckIns.Add(new CheckIn(r.At ?? svc.Now, r.Level, r.Context ?? []));
        return svc.Dto(u, svc.Today(u.Profile));
    });
    return dto is null ? Results.NotFound() : Results.Ok(dto);
});

// ---------- Daily context (Layer 2): sleep tap / HealthKit / Health Connect / calendar ----------
me.MapPut("/context/{date}", async (DateOnly date, ContextRequest r, ClaimsPrincipal user, IUserStore store, ForecastService svc) =>
{
    var dto = await store.UpdateAsync(UserId(user), u =>
    {
        var cur = u.ContextFor(date) ?? new DailyContext(date);
        u.Contexts[UserState.Key(date)] = cur with
        {
            Sleep = r.Sleep ?? cur.Sleep,
            BackToBackMeetings = r.BackToBackMeetings ?? cur.BackToBackMeetings,
            Activity = r.Activity ?? cur.Activity
        };
        return svc.Dto(u, date);
    });
    return dto is null ? Results.NotFound() : Results.Ok(dto);
});

// ---------- "Was this accurate?" ----------
me.MapGet("/feedback/pending", async (ClaimsPrincipal user, IUserStore store, ForecastService svc) =>
    await store.GetAsync(UserId(user)) is { } u
        ? Results.Ok(svc.PendingFeedback(u).Select(Map.Zone))
        : Results.NotFound());

me.MapPost("/feedback", async (FeedbackRequest r, ClaimsPrincipal user, IUserStore store, ForecastService svc) =>
{
    if (r.Verdict == FeedbackVerdict.DifferentTime && r.ActualHour is not (>= 0 and <= 23))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["actualHour"] = ["Give the hour (0–23) the craving hit."] });

    string? error = null;
    var dto = await store.UpdateAsync(UserId(user), u =>
    {
        var key = $"{UserState.Key(r.Date)}@{r.ZoneStartHour}";
        if (u.FeedbackGiven.Contains(key)) { error = "already"; return null; }

        var forecast = svc.ForecastFor(u, r.Date);
        var zone = forecast.Zones.FirstOrDefault(z => z.StartHour == r.ZoneStartHour);
        if (zone is null) { error = "zone"; return null; }

        var result = FeedbackProcessor.Apply(u.Profile, forecast, zone, r.Verdict, r.ActualHour, u.RuleStats);
        u.RuleStats = new Dictionary<string, RuleStats>(result.RuleStats);
        u.CheckIns.AddRange(result.NewCheckIns);
        u.FeedbackGiven.Add(key);
        return svc.Dto(u, svc.Today(u.Profile));
    });

    return error switch
    {
        "already" => Results.Conflict(new { message = "That zone was already rated." }),
        "zone" => Results.NotFound(new { message = "No zone starts at that hour on that date." }),
        _ => dto is null ? Results.NotFound() : Results.Ok(dto)
    };
});

// ---------- Nudges for the device to schedule as local notifications ----------
me.MapGet("/nudges", async (int? days, ClaimsPrincipal user, IUserStore store, ForecastService svc) =>
    await store.GetAsync(UserId(user)) is { } u
        ? Results.Ok(svc.UpcomingNudges(u, days ?? 2).Select(Map.Nudge))
        : Results.NotFound());

app.Run();

public partial class Program;
