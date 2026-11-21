using CravingForecast.Core;
using CravingForecast.Core.Contracts;
using CravingForecast.Mobile.Services;

namespace CravingForecast.Mobile.Pages;

public sealed class ForecastPage : ContentPage
{
    private readonly ApiClient _api;
    private readonly MorningSync _sync;
    private readonly NudgeScheduler _nudges;
    private readonly RefreshView _refresh = new();

    private ForecastDto? _forecast;
    private IReadOnlyList<ZoneDto> _pending = [];
    private bool _needsSleepTap;
    private int _level;
    private readonly HashSet<Trigger> _ctx = [];
    private bool _pickingHour;

    public ForecastPage(ApiClient api, MorningSync sync, NudgeScheduler nudges)
    {
        _api = api;
        _sync = sync;
        _nudges = nudges;
        Title = "Craving Forecast";
        _refresh.RefreshColor = Theme.Tomato;
        _refresh.Command = new Command(async () => { await LoadAsync(force: true); _refresh.IsRefreshing = false; });
        Content = _refresh;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
    }

    private static string TodayKey => $"sleepTap:{DateTime.Now:yyyy-MM-dd}";

    private async Task LoadAsync(bool force = false)
    {
        try
        {
            var result = await _sync.RunAsync(force);
            _forecast = result.Forecast ?? await _api.GetForecastAsync();
            _needsSleepTap = !result.SleepFromHealth && !Preferences.ContainsKey(TodayKey);
            _pending = await _api.GetPendingFeedbackAsync();
        }
        catch (Exception ex)
        {
            _refresh.Content = new VerticalStackLayout { Padding = 20, Children = { UI.H("Couldn't load your forecast"), UI.P(ex.Message, muted: true) } };
            return;
        }
        Render();
    }

    private async Task Apply(Func<Task<ForecastDto?>> call, string toast)
    {
        try
        {
            _forecast = await call() ?? _forecast;
            _pending = await _api.GetPendingFeedbackAsync();
            await _nudges.RescheduleAsync(await _api.GetNudgesAsync(2));
            Render();
            await DisplayAlert("", toast, "OK");
        }
        catch (Exception ex) { await DisplayAlert("Something went wrong", ex.Message, "OK"); }
    }

    private void Render()
    {
        var f = _forecast;
        if (f is null) return;
        var page = new VerticalStackLayout { Padding = 20, Spacing = 0 };

        page.Children.Add(UI.P($"Day {f.Day} · {f.Phase}", muted: true, size: 13));

        // Morning check-in (fallback when Health has no sleep data)
        if (_needsSleepTap)
            page.Children.Add(UI.Card(UI.Stack(
                UI.H("Good morning. How did you sleep?"),
                UI.Wrap(new[] { (SleepQuality.Good, "Well"), (SleepQuality.Ok, "Okay"), (SleepQuality.Poor, "Poorly") }
                    .Select(o => (View)UI.Chip(o.Item2, false, async () =>
                    {
                        Preferences.Set(TodayKey, o.Item1.ToString());
                        _needsSleepTap = false;
                        await Apply(() => _api.PutContextAsync(DateOnly.FromDateTime(DateTime.Now), new ContextRequest(o.Item1, null, null)),
                            o.Item1 == SleepQuality.Poor ? "Flagged as a higher-risk day." : "Thanks. Forecast updated.");
                    })))), hot: true));

        // "Was this accurate?"
        if (_pending.FirstOrDefault() is { } z)
        {
            var card = UI.Stack(
                UI.H("Was this accurate?"),
                UI.P($"I predicted a {Labels.Triggers[z.Context].ToLower()} window from {z.Label}."),
                UI.Wrap(
                [
                    UI.Chip("Yes, it hit", false, () => SendFeedback(z, FeedbackVerdict.Accurate, null)),
                    UI.Chip("No, I was fine", false, () => SendFeedback(z, FeedbackVerdict.Missed, null)),
                    UI.Chip("It hit at a different time", _pickingHour, () => { _pickingHour = !_pickingHour; Render(); })
                ]));
            if (_pickingHour)
                card.Children.Add(UI.Wrap(Enumerable.Range(6, 18).Select(h => (View)UI.Chip(UI.Hour(h), false,
                    () => SendFeedback(z, FeedbackVerdict.DifferentTime, h % 24)))));
            card.Children.Add(UI.P("A missed prediction counts more than a regular check-in.", muted: true, size: 13));
            page.Children.Add(UI.Card(card, hot: true));
        }

        // Next nudge
        var n = f.NextNudge;
        page.Children.Add(UI.Card(n is null
            ? UI.Stack(UI.H("No more danger zones today"), UI.P("Check in anytime. Tomorrow's nudges are already scheduled.", muted: true))
            : UI.Stack(
                UI.P("Nudge scheduled", muted: true, size: 13),
                new Label { Text = n.SendAt.LocalDateTime.ToString("h:mm tt"), FontSize = 34, FontAttributes = FontAttributes.Bold, TextColor = Theme.Tomato },
                UI.P($"30 min before your {n.Zone.Label} window · {Labels.Triggers[n.Zone.Context]}"),
                UI.Stack(n.Tips.Select(t => (View)UI.P("• " + t)).ToArray())), hot: true));

        // Check-in
        var levels = new Grid { ColumnSpacing = 4 };
        for (var i = 0; i < 10; i++)
        {
            levels.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var lvl = i + 1;
            var b = new Button
            {
                Text = lvl.ToString(), Padding = 0, CornerRadius = 8, MinimumHeightRequest = 44,
                BackgroundColor = _level == lvl ? Theme.Tomato : Colors.White,
                TextColor = _level == lvl ? Colors.White : Colors.Black,
                BorderColor = Color.FromArgb("#EADFD8"), BorderWidth = 1
            };
            b.Clicked += (_, _) => { _level = lvl; Render(); };
            levels.Add(b, i);
        }
        page.Children.Add(UI.Card(UI.Stack(
            UI.H("Check in"),
            UI.P("How strong is the craving or hunger right now?", muted: true, size: 13),
            levels,
            UI.P("What's going on? (optional)", muted: true, size: 13),
            UI.Wrap(Labels.Triggers.Select(t => (View)UI.Chip(t.Value, _ctx.Contains(t.Key), () => { if (!_ctx.Remove(t.Key)) _ctx.Add(t.Key); Render(); }))),
            UI.Primary("Log it", async () =>
            {
                var req = new CheckInRequest(_level, [.. _ctx], null);
                _level = 0; _ctx.Clear();
                await Apply(() => _api.CheckInAsync(req), "Logged. Your forecast just updated.");
            }, _level > 0))));

        // Zones
        page.Children.Add(UI.Card(UI.Stack(
            new View[] { UI.H("Danger zones today") }
                .Concat(f.Zones.Select(zz => (View)UI.P($"{zz.Label}  ·  {Labels.Triggers[zz.Context]}  ·  {zz.Peak:P0}")))
                .ToArray())));

        // How the forecast was built
        var built = UI.Stack(
            UI.H("How today's forecast was built"),
            new Label { Text = $"{f.PersonalShare:P0} personal", FontSize = 28, FontAttributes = FontAttributes.Bold },
            UI.P($"Ceiling today: {f.Ceiling:P0} (0% day 1 → 50% day 7 → 100% day 14). Each hour reaches it only with enough of your data.", muted: true, size: 13),
            new ProgressBar { Progress = f.PersonalShare, ProgressColor = Theme.Tomato },
            UI.H("1 · Archetypes"));
        foreach (var a in f.Archetypes) built.Children.Add(UI.P($"{a.Name}: {a.Description}", size: 14));
        built.Children.Add(UI.H("2 · Today's context"));
        foreach (var x in f.ContextFactors) built.Children.Add(UI.P("• " + x.Label, size: 14));
        if (f.ContextFactors.Count == 0) built.Children.Add(UI.P("No signals yet today.", muted: true, size: 14));
        built.Children.Add(UI.H("3 · Learned rules"));
        foreach (var r in f.Rules)
            built.Children.Add(UI.P($"{r.Name} · {r.StartsAt} · {(r.PhasedOut ? "phased out" : $"{r.Trust:P0} trust")}", muted: r.PhasedOut, size: 14));
        page.Children.Add(UI.Card(built));

        page.Children.Add(UI.P("Baseline rules are general patterns, not medical advice.", muted: true, size: 12));
        _refresh.Content = new ScrollView { Content = page };
    }

    private async void SendFeedback(ZoneDto z, FeedbackVerdict verdict, int? hour)
    {
        _pickingHour = false;
        var today = DateOnly.FromDateTime(DateTime.Now);
        await Apply(() => _api.SendFeedbackAsync(new FeedbackRequest(today, z.StartHour, verdict, hour)),
            verdict == FeedbackVerdict.Accurate ? "Confirmed. Those rules gained trust." : "Got it. The forecast will adjust.");
    }
}
