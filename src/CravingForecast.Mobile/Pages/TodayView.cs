using CravingForecast.Core;
using CravingForecast.Core.Contracts;
using CravingForecast.Core.Presentation;
using CravingForecast.Mobile.Services;
using Microsoft.Maui.Controls.Shapes;

namespace CravingForecast.Mobile.Pages;

/// <summary>The Today tab: a plain-language outlook, the hourly risk chart, the next nudge and today's prompts.</summary>
public sealed class TodayView : ContentView
{
    private readonly ForecastStore _store;
    private readonly RefreshView _refresh = new() { RefreshColor = Theme.Green };
    private bool _pickingHour;
    private bool _busy;
    private string? _thanks;

    public TodayView(ForecastStore store)
    {
        _store = store;
        _refresh.Command = new Command(async () =>
        {
            _thanks = null;
            try { await _store.LoadAsync(force: true); }
            catch (Exception ex) { await UI.Alert("Couldn't refresh", ex.Message); }
            finally { _refresh.IsRefreshing = false; }
        });
        Content = _refresh;
        store.Changed += () => MainThread.BeginInvokeOnMainThread(Render);
        Render();
    }

    private void Render()
    {
        var f = _store.Forecast;
        if (f is null)
        {
            _refresh.Content = new ScrollView
            {
                Content = UI.Stack(8, UI.Text("No forecast yet", 18, Weight.Bold), UI.Text("Pull down to try again.", 14, Weight.Regular, Theme.Muted))
                    .WithPadding(new Thickness(20, 24))
            };
            return;
        }

        var page = UI.Stack(16);
        page.Padding = new Thickness(20, 16, 20, 28);
        page.Children.Add(Header(f));
        page.Children.Add(Outlook(f));
        page.Children.Add(Chart(f));
        page.Children.Add(Nudge(f.NextNudge));
        if (_store.NeedsSleepTap) page.Children.Add(SleepPrompt());
        if (_thanks is not null) page.Children.Add(Thanks(_thanks));
        if (_store.Pending.FirstOrDefault() is { } pending) page.Children.Add(FeedbackPrompt(pending));
        page.Children.Add(Zones(f.Zones));
        var note = UI.Text("General patterns, not medical advice.", 11, Weight.Regular, Theme.Muted);
        note.HorizontalTextAlignment = TextAlignment.Center;
        page.Children.Add(note);

        _refresh.Content = new ScrollView { Content = page };
    }

    private static bool IsToday(ForecastDto f) => f.Date == DateOnly.FromDateTime(DateTime.Now);

    private static View Header(ForecastDto f)
    {
        var g = new Grid { ColumnSpacing = 12 };
        g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        g.Add(UI.Stack(2,
            UI.Text($"{f.Date.ToDateTime(TimeOnly.MinValue):dddd} · Day {f.Day}", 13, Weight.SemiBold, Theme.Muted),
            UI.Text(f.Phase, 13, Weight.Regular, Theme.Muted)), 0);

        if (ForecastStore.SleepTapToday is { } sleep)
        {
            var text = sleep switch { SleepQuality.Good => "Slept well", SleepQuality.Ok => "Slept okay", _ => "Slept poorly" };
            g.Add(new Border
            {
                BackgroundColor = Theme.Chip,
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 14 },
                Padding = new Thickness(10, 6),
                VerticalOptions = LayoutOptions.Center,
                Content = new HorizontalStackLayout
                {
                    Spacing = 6,
                    Children = { UI.Icon(Icons.Moon, Theme.Body, 14), UI.Text(text, 12, Weight.SemiBold, Theme.Body).Also(l => l.VerticalOptions = LayoutOptions.Center) }
                }
            }, 1);
        }
        return g;
    }

    /// <summary>The weather-style headline: are we in a zone now, and when is the next one?</summary>
    private static View Outlook(ForecastDto f)
    {
        var o = OutlookBuilder.For(f, DateTime.Now);
        var color = o.Now switch { RiskLevel.Danger => Theme.DangerText, RiskLevel.Watch => Theme.WatchText, _ => Theme.GreenText };
        return UI.Stack(4,
            UI.Text(o.Status, 14, Weight.SemiBold, color),
            UI.Heading(o.Headline, 34),
            UI.Text(o.Detail, 15, Weight.Regular, Theme.Body));
    }

    /// <summary>Hourly bars from 6 AM to 11 PM, extended past midnight when late hours carry risk.</summary>
    private static View Chart(ForecastDto f)
    {
        var risk = f.Hours.ToDictionary(h => h.Hour, h => h.Risk);
        var order = Enumerable.Range(0, 24).Select(i => (6 + i) % 24).ToList();
        var lastIndex = 17;
        for (var i = 18; i < 24; i++)
            if (risk.GetValueOrDefault(order[i]) >= UI.WatchThreshold) lastIndex = i;
        var hours = order.Take(lastIndex + 1).ToList();
        var nowIndex = IsToday(f) ? hours.IndexOf(DateTime.Now.Hour) : -1;

        var chart = new Grid { ColumnSpacing = 4, RowSpacing = 4 };
        chart.RowDefinitions.Add(new RowDefinition(new GridLength(14)));
        chart.RowDefinitions.Add(new RowDefinition(new GridLength(116)));
        chart.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var i = 0; i < hours.Count; i++)
        {
            chart.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var r = risk.GetValueOrDefault(hours[i]);
            chart.Add(new Border
            {
                HeightRequest = 14 + r * 100,
                VerticalOptions = LayoutOptions.End,
                BackgroundColor = UI.RiskColor(r),
                Opacity = nowIndex >= 0 && i < nowIndex ? 0.45 : 1,
                Stroke = Theme.Ink,
                StrokeThickness = i == nowIndex ? 2 : 0,
                StrokeShape = new RoundRectangle { CornerRadius = 5 }
            }, i, 1);
        }
        // Labels span three columns so they can be wider than one bar.
        void Label(int index, int row, string text, Weight weight, Color color)
        {
            var start = Math.Max(0, index - 1);
            var label = UI.Text(text, 10, weight, color);
            label.LineBreakMode = LineBreakMode.NoWrap;
            label.HorizontalOptions = index == 0 ? LayoutOptions.Start : LayoutOptions.Center;
            chart.Add(label, start, row);
            Grid.SetColumnSpan(label, Math.Min(index == 0 ? 2 : 3, hours.Count - start));
        }
        for (var i = 0; i < hours.Count; i++)
            if (hours[i] % 3 == 0) Label(i, 2, UI.ShortHour(hours[i]), Weight.Regular, Theme.Muted);
        if (nowIndex >= 0) Label(nowIndex, 0, "Now", Weight.Bold, Theme.Ink);

        var peak = f.Hours.MaxBy(h => h.Risk);
        SemanticProperties.SetDescription(chart, peak is null ? "Craving risk by hour" : $"Craving risk by hour. Highest is {peak.Risk:P0} at {UI.Hour(peak.Hour)}.");

        var title = new Grid();
        title.Add(UI.Text("Today's craving risk", 15, Weight.Bold));
        title.Add(UI.Text("by hour", 12, Weight.Regular, Theme.Muted).Also(l => { l.HorizontalOptions = LayoutOptions.End; l.VerticalOptions = LayoutOptions.Center; }));

        var legend = new HorizontalStackLayout { Spacing = 14 };
        foreach (var (color, text) in new[] { (Theme.Calm, "Calm"), (Theme.Watch, "Watch"), (Theme.Danger, "Danger zone") })
            legend.Children.Add(new HorizontalStackLayout
            {
                Spacing = 6,
                Children =
                {
                    new BoxView { WidthRequest = 10, HeightRequest = 10, CornerRadius = 3, Color = color, VerticalOptions = LayoutOptions.Center },
                    UI.Text(text, 12, Weight.Regular, Theme.Body)
                }
            });

        return UI.Card(UI.Stack(12, title, chart, legend), padding: new Thickness(16, 18, 16, 14));
    }

    private static View Nudge(NudgeDto? n)
    {
        if (n is null)
            return UI.Card(UI.Stack(6,
                UI.Text("No more danger zones today", 16, Weight.Bold, Colors.White),
                UI.Text("Check in anytime. Tomorrow's nudges are already scheduled.", 14, Weight.Regular, Theme.OnForestBody)),
                Theme.Forest, outline: false);

        var head = new Grid { ColumnSpacing = 10 };
        head.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        head.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        head.Add(UI.Tile(Icons.Bell, Theme.ForestIcon, Colors.White, 36, 12, 18), 0);
        head.Add(UI.Stack(0,
            UI.Text($"Next nudge · {n.SendAt.LocalDateTime:h:mm tt}", 12, Weight.Regular, Theme.OnForestMuted),
            UI.Text($"{Labels.Triggers[n.Zone.Context]}, {n.Zone.Label}", 16, Weight.Bold, Colors.White)), 1);

        var card = UI.Stack(12, head, UI.Text(n.Body, 14, Weight.Regular, Theme.OnForestBody));
        foreach (var tip in n.Tips)
            card.Children.Add(UI.Card(UI.Text(tip, 14, Weight.Regular, Colors.White), Theme.ForestRaised, outline: false, radius: 12, padding: new Thickness(12, 10)));
        return UI.Card(card, Theme.Forest, outline: false);
    }

    private View SleepPrompt() => UI.Card(UI.Stack(12,
        UI.Stack(4,
            UI.Text("Good morning. How did you sleep?", 16, Weight.Bold),
            UI.Text("No sleep data from Health today, so one tap fills it in.", 14, Weight.Regular, Theme.Body)),
        UI.Wrap(new[] { (SleepQuality.Good, "Well"), (SleepQuality.Ok, "Okay"), (SleepQuality.Poor, "Poorly") }
            .Select(o => (View)UI.Pill(o.Item2, false, () => Run(() => _store.SetSleepAsync(o.Item1),
                o.Item1 == SleepQuality.Poor ? "Flagged as a higher-risk day. Nudges adjusted." : "Thanks. Forecast updated."))))),
        Theme.Wood, outline: false);

    private View FeedbackPrompt(ZoneDto z)
    {
        var card = UI.Stack(12,
            UI.Stack(4,
                UI.Text("Was this accurate?", 16, Weight.Bold),
                UI.Text(TriggerLabels.FeedbackPrompt(z), 14, Weight.Regular, Theme.Body)),
            UI.Wrap(
            [
                UI.Pill("Yes, it hit", false, () => Feedback(z, FeedbackVerdict.Accurate, null)),
                UI.Pill("I was fine", false, () => Feedback(z, FeedbackVerdict.Missed, null)),
                UI.Pill("Different time", _pickingHour, () => { _pickingHour = !_pickingHour; Render(); })
            ]));
        if (_pickingHour)
        {
            card.Children.Add(UI.Text("When did it hit?", 13, Weight.SemiBold, Theme.Muted));
            card.Children.Add(UI.Wrap(Enumerable.Range(6, 18).Select(h => (View)UI.Pill(UI.Hour(h), false,
                () => Feedback(z, FeedbackVerdict.DifferentTime, h % 24)))));
        }
        card.Children.Add(UI.Text("A missed prediction teaches the forecast more than a regular check-in.", 12, Weight.Regular, Theme.Muted));
        return UI.Card(card, Theme.Wood, outline: false);
    }

    private void Feedback(ZoneDto z, FeedbackVerdict verdict, int? hour)
    {
        _pickingHour = false;
        Run(() => _store.FeedbackAsync(z, verdict, hour), verdict switch
        {
            FeedbackVerdict.Accurate => "Confirmed. Those rules gained trust.",
            FeedbackVerdict.Missed => "Got it. That window will cool down.",
            _ => "Thanks. I'll shift that window."
        });
    }

    private static View Thanks(string text)
    {
        var g = new Grid { ColumnSpacing = 10 };
        g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        g.Add(UI.Icon(Icons.Check, Theme.GreenText, 20, 2.2), 0);
        g.Add(UI.Text(text, 14, Weight.SemiBold, Theme.GreenText).Also(l => l.VerticalOptions = LayoutOptions.Center), 1);
        return UI.Card(g, Theme.GreenSoft, outline: false, padding: new Thickness(18, 16));
    }

    private static View Zones(IReadOnlyList<ZoneDto> zones)
    {
        var list = UI.Stack(10, UI.Text("Danger zones today", 15, Weight.Bold));
        if (zones.Count == 0)
            list.Children.Add(UI.Card(UI.Text("No danger zones forecast today.", 14, Weight.Regular, Theme.Muted), radius: 18, padding: new Thickness(16, 14)));
        foreach (var z in zones)
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            row.Add(new BoxView { WidthRequest = 10, HeightRequest = 36, CornerRadius = 5, Color = Theme.Danger, VerticalOptions = LayoutOptions.Center }, 0);
            row.Add(UI.Stack(0, UI.Text(z.Label, 15, Weight.Bold), UI.Text(Labels.Triggers[z.Context], 13, Weight.Regular, Theme.Muted)), 1);
            row.Add(UI.Text($"{z.Peak:P0}", 15, Weight.Bold, Theme.DangerText).Also(l => l.VerticalOptions = LayoutOptions.Center), 2);
            SemanticProperties.SetDescription(row, $"{z.Label}, {Labels.Triggers[z.Context]}, peak risk {z.Peak:P0}");
            list.Children.Add(UI.Card(row, radius: 18, padding: new Thickness(16, 14)));
        }
        return list;
    }

    private async void Run(Func<Task> action, string thanks)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            await action();
            _thanks = thanks;
        }
        catch (Exception ex) { await UI.Alert("Something went wrong", ex.Message); }
        finally
        {
            _busy = false;
            Render();
        }
    }
}
