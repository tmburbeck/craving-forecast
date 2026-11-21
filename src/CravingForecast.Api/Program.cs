using System.Text.Json;
using System.Text.Json.Serialization;
using CravingForecast.Api;
using CravingForecast.Core;
using CravingForecast.Core.Contracts;
using Microsoft.AspNetCore.Http.Json;

var builder = WebApplication.CreateBuilder(args);

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
json.Converters.Add(new JsonStringEnumConverter());

builder.Services.Configure<JsonOptions>(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ForecastService>();
builder.Services.AddSingleton<IUserStore>(_ =>
    new JsonFileUserStore(builder.Configuration["DataDirectory"] ?? Path.Combine(AppContext.BaseDirectory, "data"), json));

var app = builder.Build();

var api = app.MapGroup("/api");

api.MapGet("/archetypes", () => ArchetypeCatalog.All.Select(a => new { a.Id, a.Name, a.Description }));

// ---------- Onboarding (Layer 1) ----------
api.MapPost("/users", async (OnboardingRequest r, IUserStore store, ForecastService svc) =>
{
    if (r.WakeHour is < 0 or > 23 || r.BedHour is < 0 or > 23)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["hours"] = ["Wake and bed hours must be 0–23."] });

    var tzId = string.IsNullOrWhiteSpace(r.TimeZoneId) ? "America/Chicago" : r.TimeZoneId;
    if (!TimeZoneInfo.TryFindSystemTimeZoneById(tzId, out var tz))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["timeZoneId"] = [$"Unknown time zone '{tzId}'."] });

    var start = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(svc.Now, tz).DateTime);
    var profile = new OnboardingProfile(r.WakeHour, r.BedHour, r.Schedule, r.LargestMeal, r.Graze, r.Skip,
        r.Triggers ?? [], r.CravingType, r.NudgeStyle, start, tzId);

    var user = new UserState { Id = Guid.NewGuid(), Profile = profile };
    await store.CreateAsync(user);
    return Results.Created($"/api/users/{user.Id}", new OnboardingResponse(user.Id, svc.Dto(user, start)));
});

// ---------- Forecast ----------
api.MapGet("/users/{id:guid}/forecast", async (Guid id, DateOnly? date, IUserStore store, ForecastService svc) =>
    await store.GetAsync(id) is { } u
        ? Results.Ok(svc.Dto(u, date ?? svc.Today(u.Profile)))
        : Results.NotFound());

// ---------- Check-ins (Layer 3 input) ----------
api.MapPost("/users/{id:guid}/checkins", async (Guid id, CheckInRequest r, IUserStore store, ForecastService svc) =>
{
    if (r.Level is < 1 or > 10)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["level"] = ["Level must be 1–10."] });

    var dto = await store.UpdateAsync(id, u =>
    {
        u.CheckIns.Add(new CheckIn(r.At ?? svc.Now, r.Level, r.Context ?? []));
        return svc.Dto(u, svc.Today(u.Profile));
    });
    return dto is null ? Results.NotFound() : Results.Ok(dto);
});

// ---------- Daily context (Layer 2): sleep tap / HealthKit / Health Connect / calendar ----------
api.MapPut("/users/{id:guid}/context/{date}", async (Guid id, DateOnly date, ContextRequest r, IUserStore store, ForecastService svc) =>
{
    var dto = await store.UpdateAsync(id, u =>
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
api.MapGet("/users/{id:guid}/feedback/pending", async (Guid id, IUserStore store, ForecastService svc) =>
    await store.GetAsync(id) is { } u
        ? Results.Ok(svc.PendingFeedback(u).Select(Map.Zone))
        : Results.NotFound());

api.MapPost("/users/{id:guid}/feedback", async (Guid id, FeedbackRequest r, IUserStore store, ForecastService svc) =>
{
    if (r.Verdict == FeedbackVerdict.DifferentTime && r.ActualHour is not (>= 0 and <= 23))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["actualHour"] = ["Give the hour (0–23) the craving hit."] });

    string? error = null;
    var dto = await store.UpdateAsync(id, u =>
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
api.MapGet("/users/{id:guid}/nudges", async (Guid id, int? days, IUserStore store, ForecastService svc) =>
    await store.GetAsync(id) is { } u
        ? Results.Ok(svc.UpcomingNudges(u, days ?? 2).Select(Map.Nudge))
        : Results.NotFound());

app.Run();

public partial class Program;
