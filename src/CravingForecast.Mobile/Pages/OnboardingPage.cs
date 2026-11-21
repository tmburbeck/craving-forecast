using CravingForecast.Core;
using CravingForecast.Core.Contracts;
using CravingForecast.Core.Presentation;
using CravingForecast.Mobile.Services;
using Microsoft.Maui.Controls.Shapes;

namespace CravingForecast.Mobile.Pages;

/// <summary>Layer 1: the ~60 second quiz that picks the user's archetypes.</summary>
public sealed class OnboardingPage : ContentPage
{
    private readonly ApiClient _api;
    private readonly ForecastStore _store;
    private readonly Dictionary<string, object> _answers = [];
    private readonly HashSet<Trigger> _triggers = [];
    private int _step;
    private bool _busy;

    private static IReadOnlyList<QuizQuestion> Quiz => OnboardingQuiz.Questions;

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
        bool last = _step == Quiz.Count - 1;

        // Back button + one segment per question
        var segments = new Grid { ColumnSpacing = 4, VerticalOptions = LayoutOptions.Center };
        for (var i = 0; i < Quiz.Count; i++)
        {
            segments.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            segments.Add(new BoxView { HeightRequest = 6, CornerRadius = 3, Color = i < _step ? Theme.Forest : i == _step ? Theme.Green : Theme.Track }, i);
        }
        var bar = new Grid { ColumnSpacing = 12 };
        bar.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(44)));
        bar.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        if (_step > 0) bar.Add(UI.IconButton(Icons.Back, "Back", () => { _step--; Render(); }), 0);
        bar.Add(segments, 1);
        SemanticProperties.SetDescription(segments, $"Question {_step + 1} of {Quiz.Count}");

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
        grid.Add(UI.Stack(14, bar, UI.Text($"Question {_step + 1} of {Quiz.Count} · about a minute", 13, Weight.SemiBold, Theme.Muted))
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

    private bool IsSelected(QuizQuestion q, object v) =>
        q.Multi ? _triggers.Contains((Trigger)v) : _answers.TryGetValue(q.Key, out var cur) && cur.Equals(v);

    private void Pick(QuizQuestion q, object v)
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
            if (_step < Quiz.Count - 1) _step++;
        }
        Render();
    }

    private async void Next()
    {
        if (_step < Quiz.Count - 1) { _step++; Render(); return; }

        _busy = true; Render();
        try
        {
            var req = OnboardingQuiz.BuildRequest(_answers, _triggers, TimeZoneInfo.Local.Id);
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

/// <summary>Kept so existing screens read naturally; the wording lives in Core so every front end shares it.</summary>
public static class Labels
{
    public static IReadOnlyDictionary<Trigger, string> Triggers => TriggerLabels.All;
}
