using CravingForecast.Mobile.Services;

namespace CravingForecast.Mobile.Pages;

/// <summary>
/// Shown at launch after the system splash. New users move on to the welcome slides;
/// returning users wait here while today's forecast loads.
/// </summary>
public sealed class SplashPage : ContentPage
{
    private readonly ForecastStore _store;
    private readonly Label _status = UI.Text("Getting today's forecast", 12, Weight.SemiBold, Theme.Muted);
    private readonly BoxView _sweep = new() { Color = Theme.Green, CornerRadius = 2, WidthRequest = 60, HorizontalOptions = LayoutOptions.Start };
    private readonly VerticalStackLayout _footer;
    private bool _running;

    public SplashPage(ForecastStore store)
    {
        _store = store;
        BackgroundColor = Theme.Oak;
        _status.HorizontalTextAlignment = TextAlignment.Center;

        var logo = UI.Tile(Icons.Sprout, Theme.Green, Theme.Ground, 104, 32, 54, 1.6);
        logo.Shadow = new Shadow { Brush = new SolidColorBrush(Theme.GreenText), Opacity = 0.28f, Radius = 30, Offset = new Point(0, 14) };

        var name = UI.Text("Salus", 46, Weight.ExtraBold);
        name.HorizontalTextAlignment = TextAlignment.Center;
        var tagline = UI.Text("Know your cravings before they arrive.", 16, Weight.Regular, Theme.Body);
        tagline.HorizontalTextAlignment = TextAlignment.Center;

        var track = new Grid
        {
            WidthRequest = 160, HeightRequest = 4, IsClippedToBounds = true,
            Children = { new BoxView { Color = Theme.Track, CornerRadius = 2 }, _sweep }
        };
        _footer = UI.Stack(12, track, _status);
        _footer.HorizontalOptions = LayoutOptions.Center;

        var grid = new Grid { Padding = new Thickness(32, 0, 32, 64) };
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var center = UI.Stack(22, logo, UI.Stack(8, name, tagline));
        center.VerticalOptions = LayoutOptions.Center;
        logo.HorizontalOptions = LayoutOptions.Center;
        grid.Add(center, 0, 0);
        grid.Add(_footer, 0, 1);
        Content = grid;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_running) return;
        _running = true;
        Sweep();
        await StartAsync();
    }

    private async Task StartAsync()
    {
        if (Session.UserId is null)
        {
            _status.Text = "";
            await Task.Delay(1200);
            App.Instance.Show<WelcomePage>();
            return;
        }

        try
        {
            await _store.LoadAsync();
            App.Instance.Show<HomePage>();
        }
        catch (Exception ex)
        {
            _running = false;
            this.AbortAnimation("sweep");
            _status.Text = $"Couldn't reach the server at {ApiConfig.BaseUrl}.\n{ex.Message}";
            if (_footer.Children.Count == 2)
                _footer.Children.Add(UI.Primary("Try again", async () =>
                {
                    _status.Text = "Getting today's forecast";
                    _running = true;
                    Sweep();
                    await StartAsync();
                }));
        }
    }

    /// <summary>An indeterminate loading bar: a short segment sliding across the track.</summary>
    private void Sweep() =>
        new Animation(v => _sweep.TranslationX = v, -60, 160)
            .Commit(this, "sweep", length: 1100, easing: Easing.SinInOut, repeat: () => _running);
}
