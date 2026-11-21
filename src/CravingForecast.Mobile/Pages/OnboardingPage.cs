using CravingForecast.Core;
using CravingForecast.Core.Contracts;
using CravingForecast.Mobile.Services;

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
        new("triggers", "When do cravings usually get you? Pick all that apply.", Labels.Triggers.Select(t => ((object)t.Key, t.Value)).ToArray(), Multi: true),
        new("type", "What do you usually reach for?", [(CravingType.Sweet, "Sweet"), (CravingType.Salty, "Salty or crunchy"), (CravingType.Both, "Anything, honestly")]),
        new("style", "What helps you more in the moment?", [(NudgeStyle.Snack, "A better snack ready to go"), (NudgeStyle.Distract, "A quick distraction"), (NudgeStyle.Mix, "A mix of both")])
    ];

    private readonly ApiClient _api;
    private readonly Dictionary<string, object> _answers = [];
    private readonly HashSet<Trigger> _triggers = [];
    private int _step;
    private bool _busy;

    public OnboardingPage(ApiClient api)
    {
        _api = api;
        Title = "Craving Forecast";
        Render();
    }

    private void Render()
    {
        var q = Quiz[_step];
        bool answered = q.Multi ? _triggers.Count > 0 : _answers.ContainsKey(q.Key);

        var stack = UI.Stack(
            UI.P($"Question {_step + 1} of {Quiz.Length} · about a minute", muted: true, size: 13),
            new ProgressBar { Progress = (double)_step / Quiz.Length, ProgressColor = Theme.Tomato });
        if (_step == 0)
        {
            stack.Children.Add(UI.Title("Let's find your danger zones."));
            stack.Children.Add(UI.P("Nine quick taps match you to a craving archetype, so your forecast works today. Your own check-ins take over by day 14.", muted: true));
        }
        stack.Children.Add(new Label { Text = q.Text, FontSize = 20, FontAttributes = FontAttributes.Bold, Margin = new Thickness(0, 16, 0, 8) });
        stack.Children.Add(UI.Wrap(q.Options.Select(o => (View)UI.Chip(o.Label, IsSelected(q, o.Value), () => Pick(q, o.Value)))));

        var nav = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 20, 0, 0) };
        nav.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        nav.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        if (_step > 0) nav.Add(new Button { Text = "Back", Command = new Command(() => { _step--; Render(); }) }, 0);
        nav.Add(UI.Primary(_step == Quiz.Length - 1 ? (_busy ? "Building…" : "See my forecast") : "Next", Next, answered && !_busy), 1);
        stack.Children.Add(nav);

        Content = new ScrollView { Content = new VerticalStackLayout { Padding = 20, Children = { stack } } };
    }

    private bool IsSelected(Question q, object v) =>
        q.Multi ? _triggers.Contains((Trigger)v) : _answers.TryGetValue(q.Key, out var cur) && cur.Equals(v);

    private void Pick(Question q, object v)
    {
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
            var res = await _api.CreateUserAsync(req);
            Session.UserId = res.UserId;
            ((App)Application.Current!).ShowForecast();
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Couldn't reach the server", $"Is the API running at {ApiConfig.BaseUrl}?\n\n{ex.Message}", "OK");
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
