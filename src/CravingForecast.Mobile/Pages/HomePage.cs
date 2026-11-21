using CravingForecast.Mobile.Services;
using Microsoft.Maui.Controls.Shapes;
using ShapePath = Microsoft.Maui.Controls.Shapes.Path;

namespace CravingForecast.Mobile.Pages;

/// <summary>The main screen: Today and Insights tabs with the check-in button between them.</summary>
public sealed class HomePage : ContentPage
{
    private readonly ForecastStore _store;
    private readonly TodayView _today;
    private readonly InsightsView _insights;
    private readonly ContentView _host = new();
    private readonly TabButton _todayTab;
    private readonly TabButton _insightsTab;
    private bool _showingInsights;

    public HomePage(ForecastStore store, AuthService auth, NudgeScheduler nudges)
    {
        _store = store;
        _today = new TodayView(store);
        _insights = new InsightsView(store, auth, nudges);
        _todayTab = new TabButton(Icons.Chart, "Today", () => Select(false));
        _insightsTab = new TabButton(Icons.Clock, "Insights", () => Select(true));

        var plus = UI.Tile(Icons.Plus, Theme.Green, Colors.White, 60, 20, 26, 2.4);
        plus.TranslationY = -22;
        plus.Shadow = new Shadow { Brush = new SolidColorBrush(Theme.GreenText), Opacity = 0.32f, Radius = 20, Offset = new Point(0, 8) };
        SemanticProperties.SetDescription(plus, "Check in");
        plus.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(async () => await Navigation.PushModalAsync(App.Instance.Create<CheckInPage>())) });

        var tabs = new Grid { Padding = new Thickness(24, 8, 24, 10), HeightRequest = 64 };
        tabs.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        tabs.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        tabs.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        tabs.Add(_todayTab, 0);
        tabs.Add(plus, 1);
        tabs.Add(_insightsTab, 2);

        var nav = new VerticalStackLayout
        {
            BackgroundColor = Theme.Card,
            Children = { new BoxView { HeightRequest = 1, Color = Theme.Border }, tabs }
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.Add(_host, 0, 0);
        grid.Add(nav, 0, 1);
        Content = grid;
        Select(false);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        // Coming straight from the quiz, only the forecast is known; run the morning sync for nudges and prompts.
        if (_store.Synced) return;
        try { await _store.LoadAsync(); }
        catch { /* keep showing the forecast from onboarding; pull to refresh retries */ }
    }

    protected override bool OnBackButtonPressed()
    {
        if (!_showingInsights) return base.OnBackButtonPressed();
        Select(false);
        return true;
    }

    private void Select(bool insights)
    {
        _showingInsights = insights;
        _host.Content = insights ? _insights : _today;
        _todayTab.SetActive(!insights);
        _insightsTab.SetActive(insights);
    }

    private sealed class TabButton : VerticalStackLayout
    {
        private readonly ShapePath _icon;
        private readonly Label _label;
        private readonly string _name;

        public TabButton(string icon, string label, Action onTap)
        {
            _name = label;
            _icon = UI.Icon(icon, Theme.Muted, 22);
            _label = UI.Text(label, 11, Weight.SemiBold, Theme.Muted);
            _label.HorizontalOptions = LayoutOptions.Center;
            Spacing = 4;
            MinimumWidthRequest = 64;
            MinimumHeightRequest = 44;
            HorizontalOptions = LayoutOptions.Center;
            VerticalOptions = LayoutOptions.Center;
            Children.Add(_icon);
            Children.Add(_label);
            GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(onTap) });
        }

        public void SetActive(bool active)
        {
            var color = active ? Theme.GreenText : Theme.Muted;
            _icon.Stroke = new SolidColorBrush(color);
            _label.TextColor = color;
            _label.FontFamily = UI.Font(active ? Weight.Bold : Weight.SemiBold);
            SemanticProperties.SetDescription(this, active ? $"{_name} tab, selected" : $"{_name} tab");
        }
    }
}
