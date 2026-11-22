using CravingForecast.Mobile.Services;
using Microsoft.Maui.Controls.Shapes;

namespace CravingForecast.Mobile.Pages;

/// <summary>Create an account or sign in. One screen, two modes, switched by the link under the button.</summary>
public sealed class AccountPage : ContentPage
{
    private readonly AuthService _auth;
    private readonly ApiClient _api;
    private readonly ForecastStore _store;

    private readonly Label _title = UI.Heading("");
    private readonly Label _intro = UI.Text("", 15, Weight.Regular, Theme.Body);
    private readonly Entry _email = Field("you@example.com", Keyboard.Email);
    private readonly Entry _password = Field("At least 10 characters", Keyboard.Default, password: true);
    private readonly Label _passwordHint = UI.Text("At least 10 characters. A short phrase is easy to remember.", 12, Weight.Regular, Theme.Muted);
    private readonly Label _error = UI.Text("", 14, Weight.SemiBold, Theme.DangerText);
    private readonly Button _submit;
    private readonly Button _switch;
    private readonly Button _forgot;
    private bool _signingIn;
    private bool _busy;

    public AccountPage(AuthService auth, ApiClient api, ForecastStore store)
    {
        _auth = auth;
        _api = api;
        _store = store;
        _error.IsVisible = false;
        _email.ReturnType = ReturnType.Next;
        _email.Completed += (_, _) => _password.Focus();
        _password.ReturnType = ReturnType.Go;
        _password.Completed += (_, _) => Submit();
        _submit = UI.Primary("", Submit);
        _switch = UI.Plain("", () => { _signingIn = !_signingIn; Update(); });
        _switch.HorizontalOptions = LayoutOptions.Center;
        // Resetting happens on the web page; the email's link opens there too.
        _forgot = UI.Plain("Forgot your password?", async () => await Launcher.OpenAsync(new Uri(new Uri(ApiConfig.BaseUrl), "forgot")));
        _forgot.HorizontalOptions = LayoutOptions.Center;

        var brand = new HorizontalStackLayout
        {
            Spacing = 8,
            Children = { UI.Tile(Icons.Sprout, Theme.Green, Theme.Ground, 30, 10, 18), UI.Text("Salus", 17, Weight.ExtraBold).Also(l => l.VerticalOptions = LayoutOptions.Center) }
        };

        var form = UI.Stack(22,
            brand,
            UI.Stack(8, _title, _intro),
            UI.Stack(8, UI.Text("Email", 14, Weight.Bold), Box(_email)),
            UI.Stack(8, UI.Text("Password", 14, Weight.Bold), Box(_password), _passwordHint),
            _error,
            UI.Stack(8, _submit, _forgot, _switch));
        form.Padding = new Thickness(20, 24, 20, 32);
        form.MaximumWidthRequest = 480;
        Content = new ScrollView { Content = form };
        Update();
    }

    private static Entry Field(string placeholder, Keyboard keyboard, bool password = false) => new()
    {
        Placeholder = placeholder,
        PlaceholderColor = Theme.Muted,
        Keyboard = keyboard,
        IsPassword = password,
        IsSpellCheckEnabled = false,
        IsTextPredictionEnabled = false,
        FontFamily = UI.Font(Weight.Regular),
        FontSize = 16,
        TextColor = Theme.Ink,
        BackgroundColor = Colors.Transparent
    };

    private static Border Box(View field) => new()
    {
        Content = field,
        Padding = new Thickness(14, 4),
        MinimumHeightRequest = 52,
        BackgroundColor = Theme.Card,
        Stroke = Theme.Border,
        StrokeThickness = 1,
        StrokeShape = new RoundRectangle { CornerRadius = 14 }
    };

    private void Update()
    {
        _title.Text = _signingIn ? "Welcome back" : "Create your account";
        _intro.Text = _signingIn
            ? "Sign in to pick up your forecast where you left off."
            : "Your forecast is saved to your account, so it's there on any device you sign in on.";
        _passwordHint.IsVisible = !_signingIn;
        _password.Placeholder = _signingIn ? "Your password" : "At least 10 characters";
        _submit.Text = _busy ? (_signingIn ? "Signing in…" : "Creating your account…") : (_signingIn ? "Sign in" : "Create account");
        _submit.IsEnabled = !_busy;
        _submit.BackgroundColor = _busy ? Theme.Disabled : Theme.Green;
        _switch.Text = _signingIn ? "New here? Create an account" : "Already have an account? Sign in";
        _forgot.IsVisible = _signingIn;
        _switch.IsEnabled = !_busy;
    }

    private void ShowError(string? message)
    {
        _error.Text = message ?? "";
        _error.IsVisible = !string.IsNullOrEmpty(message);
    }

    private async void Submit()
    {
        if (_busy) return;
        var email = _email.Text?.Trim() ?? "";
        var password = _password.Text ?? "";
        if (!email.Contains('@')) { ShowError("Enter your email address."); return; }
        if (password.Length == 0) { ShowError("Enter your password."); return; }

        ShowError(null);
        _busy = true;
        Update();
        try
        {
            var error = _signingIn ? await _auth.SignInAsync(email, password) : await _auth.RegisterAsync(email, password);
            if (error is not null) { ShowError(error); return; }

            _password.Text = "";
            var me = await _api.GetMeAsync();
            if (!me.HasProfile) { App.Instance.Show<OnboardingPage>(); return; }
            await _store.LoadAsync();
            App.Instance.Show<HomePage>();
        }
        catch (Exception ex)
        {
            ShowError($"Couldn't reach Salus. Check your connection and try again.\n({ex.Message})");
        }
        finally
        {
            _busy = false;
            Update();
        }
    }
}
