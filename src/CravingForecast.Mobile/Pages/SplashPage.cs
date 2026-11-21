using CravingForecast.Mobile.Services;

namespace CravingForecast.Mobile.Pages;

/// <summary>
/// Shown at launch after the system splash. It greets the user, fades in, and stays up for at least
/// <see cref="MinimumOnScreen"/> while today's forecast loads, so it never flashes past.
/// New users then see the welcome slides; returning users go straight to Today.
/// </summary>
public sealed class SplashPage : ContentPage
{
    private static readonly TimeSpan MinimumOnScreen = TimeSpan.FromSeconds(2.6);

    private readonly ForecastStore _store;
    private readonly AuthService _auth;
    private readonly ApiClient _api;
    private readonly Border _logo;
    private readonly VerticalStackLayout _words;
    private readonly Label _greeting = UI.Text("", 17, Weight.SemiBold, Theme.GreenText);
    private readonly Label _status = UI.Text("Getting today's forecast", 12, Weight.SemiBold, Theme.Muted);
    private readonly BoxView _sweep = new() { Color = Theme.Green, CornerRadius = 2, WidthRequest = 60, HorizontalOptions = LayoutOptions.Start };
    private readonly VerticalStackLayout _footer;
    private readonly Grid _root;
    private bool _running;

    public SplashPage(ForecastStore store, AuthService auth, ApiClient api)
    {
        _store = store;
        _auth = auth;
        _api = api;
        BackgroundColor = Theme.Oak;

        _logo = UI.Tile(Icons.Sprout, Theme.Green, Theme.Ground, 104, 32, 54, 1.6);
        _logo.HorizontalOptions = LayoutOptions.Center;
        _logo.Shadow = new Shadow { Brush = new SolidColorBrush(Theme.GreenText), Opacity = 0.28f, Radius = 30, Offset = new Point(0, 14) };

        var name = UI.Text("Salus", 46, Weight.ExtraBold);
        var tagline = UI.Text("Know your cravings before they arrive.", 16, Weight.Regular, Theme.Body);
        foreach (var l in new[] { _greeting, name, tagline, _status }) l.HorizontalTextAlignment = TextAlignment.Center;
        _words = UI.Stack(8, _greeting, name, tagline);

        var track = new Grid
        {
            WidthRequest = 160, HeightRequest = 4, IsClippedToBounds = true,
            Children = { new BoxView { Color = Theme.Track, CornerRadius = 2 }, _sweep }
        };
        _footer = UI.Stack(12, track, _status);
        _footer.HorizontalOptions = LayoutOptions.Center;

        _root = new Grid { Padding = new Thickness(32, 0, 32, 64) };
        _root.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        _root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var center = UI.Stack(22, _logo, _words);
        center.VerticalOptions = LayoutOptions.Center;
        _root.Add(center, 0, 0);
        _root.Add(_footer, 0, 1);
        Content = _root;

        // Start hidden; OnAppearing brings everything in.
        _logo.Opacity = 0;
        _logo.Scale = 0.86;
        _words.Opacity = 0;
        _words.TranslationY = 12;
        _footer.Opacity = 0;
    }

    private static string Greeting() => DateTime.Now.Hour switch
    {
        < 5 => "Hello, night owl",
        < 12 => "Good morning",
        < 17 => "Good afternoon",
        _ => "Good evening"
    };

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_running) return;
        _running = true;
        await _auth.InitializeAsync();
        _greeting.Text = _auth.IsSignedIn ? Greeting() : "Welcome";
        _status.Text = _auth.IsSignedIn ? "Getting today's forecast" : "";
        await Task.WhenAll(FadeInAsync(), StartAsync());
    }

    private async Task FadeInAsync()
    {
        await Task.WhenAll(
            _logo.FadeToAsync(1, 600, Easing.CubicOut),
            _logo.ScaleToAsync(1, 700, Easing.SpringOut));
        await Task.WhenAll(
            _words.FadeToAsync(1, 500, Easing.CubicOut),
            _words.TranslateToAsync(0, 0, 500, Easing.CubicOut));
        Sweep();
        await _footer.FadeToAsync(1, 300);
    }

    private async Task StartAsync()
    {
        var minimum = Task.Delay(MinimumOnScreen);
        if (!_auth.IsSignedIn)
        {
            await minimum;
            await LeaveAsync<WelcomePage>();
            return;
        }

        try
        {
            var me = await _api.GetMeAsync();
            if (!me.HasProfile)
            {
                await minimum;
                await LeaveAsync<OnboardingPage>();
                return;
            }
            await Task.WhenAll(_store.LoadAsync(), minimum);
            await LeaveAsync<HomePage>();
        }
        catch (Exception) when (!_auth.IsSignedIn)
        {
            // The server no longer accepts the saved sign-in.
            await minimum;
            await LeaveAsync<AccountPage>();
        }
        catch (Exception ex)
        {
            await minimum;
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

    /// <summary>A soft fade out so the hand-off to the next screen isn't a hard cut.</summary>
    private async Task LeaveAsync<T>() where T : Page
    {
        _running = false;
        await _root.FadeToAsync(0, 350, Easing.CubicIn);
        App.Instance.Show<T>();
    }

    /// <summary>An indeterminate loading bar: a short segment sliding across the track.</summary>
    private void Sweep() =>
        new Animation(v => _sweep.TranslationX = v, -60, 160)
            .Commit(this, "sweep", length: 1100, easing: Easing.SinInOut, repeat: () => _running);
}
