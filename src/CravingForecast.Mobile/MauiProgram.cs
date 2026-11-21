using System.Text.Json;
using System.Text.Json.Serialization;
using CravingForecast.Mobile.Pages;
using CravingForecast.Mobile.Services;
using Plugin.LocalNotification;
using Plugin.Maui.CalendarStore;

namespace CravingForecast.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>()
#if !WINDOWS
            .UseLocalNotification()
#endif
            .ConfigureFonts(fonts =>
        {
            fonts.AddFont("PlusJakartaSans-Regular.ttf", "Jakarta");
            fonts.AddFont("PlusJakartaSans-SemiBold.ttf", "JakartaSemiBold");
            fonts.AddFont("PlusJakartaSans-Bold.ttf", "JakartaBold");
            fonts.AddFont("PlusJakartaSans-ExtraBold.ttf", "JakartaExtraBold");
        });

        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        json.Converters.Add(new JsonStringEnumConverter());
        builder.Services.AddSingleton(json);
        builder.Services.AddSingleton<AuthService>();
        builder.Services.AddSingleton(sp => new HttpClient(new AuthHandler(sp.GetRequiredService<AuthService>()))
        {
            BaseAddress = new Uri(ApiConfig.BaseUrl),
            Timeout = TimeSpan.FromSeconds(15)
        });
        builder.Services.AddSingleton<ApiClient>();

        builder.Services.AddSingleton(CalendarStore.Default);
        builder.Services.AddSingleton<ICalendarSignals, CalendarSignals>();
#if IOS
        builder.Services.AddSingleton<IHealthSignals, CravingForecast.Mobile.Health.HealthKitSignals>();
#elif ANDROID
        builder.Services.AddSingleton<IHealthSignals, CravingForecast.Mobile.Health.HealthConnectSignals>();
#else
        builder.Services.AddSingleton<IHealthSignals, NoHealthSignals>();
#endif
        builder.Services.AddSingleton<NudgeScheduler>();
        builder.Services.AddSingleton<MorningSync>();
        builder.Services.AddSingleton<ForecastStore>();

        builder.Services.AddTransient<SplashPage>();
        builder.Services.AddTransient<WelcomePage>();
        builder.Services.AddTransient<AccountPage>();
        builder.Services.AddTransient<OnboardingPage>();
        builder.Services.AddTransient<HomePage>();
        builder.Services.AddTransient<CheckInPage>();
        return builder.Build();
    }
}

public static class ApiConfig
{
    /// <summary>The hosted API on Azure App Service.</summary>
    public const string Hosted = "https://salus-tmburbeck-bafcfcg0exfpduh6.westus3-01.azurewebsites.net/";

    /// <summary>
    /// The hosted API, unless SALUS_API_URL is set (for example http://localhost:5077/ to work against an API
    /// running on this PC). Android emulators reach the PC at http://10.0.2.2:5077/; a USB phone uses
    /// http://localhost:5077/ after: adb reverse tcp:5077 tcp:5077
    /// </summary>
    public static string BaseUrl { get; } = WithSlash(Environment.GetEnvironmentVariable("SALUS_API_URL") ?? Hosted);

    private static string WithSlash(string url) => url.EndsWith('/') ? url : url + "/";
}

public static class Session
{
    public static DateOnly? LastSync
    {
        get => DateOnly.TryParse(Preferences.Get("lastSync", ""), out var d) ? d : null;
        set => Preferences.Set("lastSync", value?.ToString("yyyy-MM-dd") ?? "");
    }
}
