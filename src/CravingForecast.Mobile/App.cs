using CravingForecast.Mobile.Pages;

namespace CravingForecast.Mobile;

public class App : Application
{
    private readonly IServiceProvider _services;

    public App(IServiceProvider services) => _services = services;

    protected override Window CreateWindow(IActivationState? activationState) =>
        new(Session.UserId is null ? Wrap<OnboardingPage>() : Wrap<ForecastPage>());

    public void ShowForecast() => Windows[0].Page = Wrap<ForecastPage>();

    private NavigationPage Wrap<T>() where T : Page => new(_services.GetRequiredService<T>())
    {
        BarBackgroundColor = Theme.Tomato,
        BarTextColor = Colors.White
    };
}

public static class Theme
{
    public static readonly Color Tomato = Color.FromArgb("#E8472B");
    public static readonly Color Apricot = Color.FromArgb("#FFD2B5");
    public static readonly Color Soft = Color.FromArgb("#FFF1E7");
    public static readonly Color Muted = Color.FromArgb("#6F625C");
}
