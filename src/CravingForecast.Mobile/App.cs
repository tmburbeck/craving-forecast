using CravingForecast.Mobile.Pages;

namespace CravingForecast.Mobile;

public class App : Application
{
    private readonly IServiceProvider _services;

    public App(IServiceProvider services)
    {
        _services = services;
        UserAppTheme = AppTheme.Light; // the wood-and-green palette is designed for light mode
        Resources = UI.Styles();
    }

    public static App Instance => (App)Current!;

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(_services.GetRequiredService<SplashPage>());
#if WINDOWS
        // Phone-shaped window for previewing on a PC.
        window.Width = 420;
        window.Height = 880;
#endif
        return window;
    }

    /// <summary>Replaces the whole screen: splash → welcome → quiz → home are one-way steps, not a back stack.</summary>
    public void Show<T>() where T : Page => Windows[0].Page = _services.GetRequiredService<T>();

    public T Create<T>() where T : notnull => _services.GetRequiredService<T>();
}
