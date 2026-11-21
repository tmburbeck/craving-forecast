using CravingForecast.Core;
using CravingForecast.Core.Contracts;
using CravingForecast.Mobile.Services;
using Microsoft.Maui.Controls.Shapes;

namespace CravingForecast.Mobile.Pages;

/// <summary>Layer 1: the ~60 second quiz that picks the user's archetypes.</summary>
public sealed class OnboardingPage : ContentPage
{
    private sealed record Question(string Key, string Text, (object Value, string Label)[] Options, bool Multi = false);

    private static readonly Question[] Quiz =
    [
        new("wake", "When do you usually wake up?", [(5, "5 AM"), (6, "6 AM"), (7, "7 AM"), (8, "8 AM"), (9, "9 AM or later")]),
        new("bed", "When do you usually go to bed?", [(21, "9 PM"), (22, "10 PM"), (23, "11 PM"), (0, "Midnight"), (1, "1 AM or later")]),
        new("sched", "What is your typical work schedule?", [(WorkSchedule.Office, "9–5, on-site"), (WorkSchedule.Early, "Early shift, done by 3"), (WorkSchedule.Evening, "Evening or night shift"), (WorkSchedule.Remote, "Remote or flexible"), (WorkSchedule.Rotating, "Rotating or irregular")]),
        new("meal", "When do you usually eat your largest meal?", [(MealTime.Breakfast, "Breakfast"), (MealTime.Lunch, "Lunch"), (MealTime.Dinner, "Dinner"), (MealTime.Late, "Late at night")]),
        new("graze", "Do you tend to graze? What drives it?", [(GrazeDriver.Boredom, "Boredom"), (GrazeDriver.Stress, "Stress"), (GrazeDriver.Fatigue, "Late-night fatigue"), (GrazeDriver.None, "I don't really graze")]),
        new("skip", "Which meal do you skip most often?", [(SkippedMeal.Breakfast, "Breakfast"), (SkippedMeal.Lunch, "Lunch"), (SkippedMeal.None, "I rarely skip")]),
        new("triggers", "When do cravings usually get you?", Labels.Triggers.Select(t => ((object)t.Key, t.Value)).ToArray(), Multi: true),
        new("type", "What do you usually reach for?", [(CravingType.Sweet, "Sweet"), (CravingType.Salty, "Salty or crunchy"), (CravingType.Both, "Anything, honestly")]),
        new("style", "What helps you more in the moment?", [(NudgeStyle.Snack, "A better snack ready to go"), (NudgeStyle.Distract, "A quick distraction"), (NudgeStyle.Mix, "A mix of both")])
    ];

    private readonly ApiClient _api;
    private readonly ForecastStore _store;
    private readonly Dictionary<string, object> _answers = [];
    private readonly HashSet<Trigger> _triggers = [];
    private int _step;
    private bool _busy;

    public OnboardingPage(ApiClient api, ForecastStore store)
    {
        _api = api;
        _store = store;
        Render();
    }

    protected override bool OnBackButtonPressed()
    {
        if (_step == 0) return base.OnBackButtonPressed();
        _step--;
        Render();
        return true;
    }

    private void Render()
    {
        var q = Quiz[_step];
        bool answered = q.Multi ? _triggers.Count > 0 : _answers.ContainsKey(q.Key);
        bool last = _step == Quiz.Length - 1;

        // Back button + one segment per question
        var segments = new Grid { ColumnSpacing = 4, VerticalOptions = LayoutOptions.Center };
        for (var i = 0; i < Quiz.Length; i++)
        {
            segments.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            segments.Add(new BoxView { HeightRequest = 6, CornerRadius = 3, Color = i < _step ? Theme.Forest : i == _step ? Theme.Green : Theme.Track }, i);
        }
        var bar = new Grid { ColumnSpacing = 12 };
        bar.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(44)));
        bar.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        if (_step > 0) bar.Add(UI.IconButton(Icons.Back, "Back", () => { _step--; Render(); }), 0);
        bar.Add(segments, 1);
        SemanticProperties.SetDescription(segments, $"Question {_step + 1} of {Quiz.Length}");

        var body = UI.Stack(18);
        if (_step == 0)
            body.Children.Add(UI.Text("Nine quick taps match you to a craving archetype, so your forecast works from today.", 15, Weight.Regular, Theme.Body));
        body.Children.Add(UI.Heading(q.Text));
        if (q.Multi) body.Children.Add(UI.Text("Pick all that apply", 13, Weight.SemiBold, Theme.Muted));
        var options = UI.Stack(10);
        foreach (var o in q.Options) options.Children.Add(Option(o.Label, IsSelected(q, o.Value), () => Pick(q, o.Value)));
        body.Children.Add(options);

        var action = UI.Primary(last ? (_busy ? "Building your forecast…" : "See my forecast") : "Next", Next, answered && !_busy);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.Add(UI.Stack(14, bar, UI.Text($"Question {_step + 1} of {Quiz.Length} · about a minute", 13, Weight.SemiBold, Theme.Muted))
            .WithPadding(new Thickness(20, 16, 20, 0)), 0, 0);
        grid.Add(new ScrollView { Content = body.WithPadding(new Thickness(20, 18, 20, 20)) }, 0, 1);
        grid.Add(UI.Footer(action), 0, 2);
        Content = grid;
    }

    /// <summary>A full-width answer card with a selection dot.</summary>
    private static View Option(string label, bool selected, Action onTap)
    {
        var dot = new Ellipse
        {
            WidthRequest = 22, HeightRequest = 22,
            Stroke = new SolidColorBrush(selected ? Theme.Green : Theme.WoodBorder), StrokeThickness = 2,
            Fill = new SolidColorBrush(selected ? Theme.Green : Theme.Card),
            VerticalOptions = LayoutOptions.Center
        };
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        row.Add(UI.Text(label, 16, Weight.SemiBold).Also(l => l.VerticalOptions = LayoutOptions.Center), 0);
        row.Add(dot, 1);

        var card = new Border
        {
            Content = row,
            MinimumHeightRequest = 56,
            Padding = new Thickness(18, 12),
            BackgroundColor = selected ? Theme.GreenSoft : Theme.Card,
            Stroke = selected ? Theme.Green : Theme.Border,
            StrokeThickness = 2,
            StrokeShape = new RoundRectangle { CornerRadius = 16 }
        };
        card.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(onTap) });
        SemanticProperties.SetDescription(card, selected ? $"{label}, selected" : label);
        return card;
    }

    private bool IsSelected(Question q, object v) =>
        q.Multi ? _triggers.Contains((Trigger)v) : _answers.TryGetValue(q.Key, out var cur) && cur.Equals(v);

    private void Pick(Question q, object v)
    {
        if (_busy) return;
        if (q.Multi)
        {
            var t = (Trigger)v;
            if (!_triggers.Remove(t)) _triggers.Add(t);
        }
        else
        {
            _answers[q.Key] = v;
            if (_step < Quiz.Length - 1) _step++;
        }
        Render();
    }

    private async void Next()
    {
        if (_step < Quiz.Length - 1) { _step++; Render(); return; }

        _busy = true; Render();
        try
        {
            var req = new OnboardingRequest(
                (int)_answers["wake"], (int)_answers["bed"], (WorkSchedule)_answers["sched"], (MealTime)_answers["meal"],
                (GrazeDriver)_answers["graze"], (SkippedMeal)_answers["skip"], [.. _triggers],
                (CravingType)_answers["type"], (NudgeStyle)_answers["style"], TimeZoneInfo.Local.Id);
            var res = await _api.CreateProfileAsync(req);
            _store.Seed(res.Forecast);
            App.Instance.Show<HomePage>();
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Couldn't save your answers", $"Check your connection and try again.\n\n{ex.Message}", "OK");
            _busy = false; Render();
        }
    }
}

public static class Labels
{
    public static readonly Dictionary<Trigger, string> Triggers = new()
    {
        [Trigger.Slump] = "Afternoon energy slump",
        [Trigger.Stress] = "Stressed after work",
        [Trigger.Tv] = "Watching TV / scrolling",
        [Trigger.Sleep] = "Slept poorly",
        [Trigger.Bored] = "Bored at home",
        [Trigger.Gap] = "Long gap since eating",
        [Trigger.Social] = "Social / eating out",
        [Trigger.Late] = "Late night, decision fatigue"
    };
}
