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
        builder.UseMauiApp<App>().UseLocalNotification().ConfigureFonts(fonts =>
        {
            fonts.AddFont("PlusJakartaSans-Regular.ttf", "Jakarta");
            fonts.AddFont("PlusJakartaSans-SemiBold.ttf", "JakartaSemiBold");
            fonts.AddFont("PlusJakartaSans-Bold.ttf", "JakartaBold");
            fonts.AddFont("PlusJakartaSans-ExtraBold.ttf", "JakartaExtraBold");
        });

        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        json.Converters.Add(new JsonStringEnumConverter());
        builder.Services.AddSingleton(json);
        builder.Services.AddSingleton(new HttpClient { BaseAddress = new Uri(ApiConfig.BaseUrl), Timeout = TimeSpan.FromSeconds(15) });
        builder.Services.AddSingleton<ApiClient>();

        builder.Services.AddSingleton(CalendarStore.Default);
        builder.Services.AddSingleton<ICalendarSignals, CalendarSignals>();
#if IOS
        builder.Services.AddSingleton<IHealthSignals, CravingForecast.Mobile.Health.HealthKitSignals>();
#elif ANDROID
        builder.Services.AddSingleton<IHealthSignals, CravingForecast.Mobile.Health.HealthConnectSignals>();
#endif
        builder.Services.AddSingleton<NudgeScheduler>();
        builder.Services.AddSingleton<MorningSync>();
        builder.Services.AddSingleton<ForecastStore>();

        builder.Services.AddTransient<SplashPage>();
        builder.Services.AddTransient<WelcomePage>();
        builder.Services.AddTransient<OnboardingPage>();
        builder.Services.AddTransient<HomePage>();
        builder.Services.AddTransient<CheckInPage>();
        return builder.Build();
    }
}

public static class ApiConfig
{
    // Android emulators reach the host machine at 10.0.2.2. On a physical phone, use your PC's LAN IP
    // and run the API with: dotnet run --urls http://0.0.0.0:5077
    public static string BaseUrl => DeviceInfo.Platform == DevicePlatform.Android
        ? "http://10.0.2.2:5077/"
        : "http://localhost:5077/";
}

public static class Session
{
    public static Guid? UserId
    {
        get => Guid.TryParse(Preferences.Get("userId", ""), out var id) ? id : null;
        set => Preferences.Set("userId", value?.ToString() ?? "");
    }

    public static DateOnly? LastSync
    {
        get => DateOnly.TryParse(Preferences.Get("lastSync", ""), out var d) ? d : null;
        set => Preferences.Set("lastSync", value?.ToString("yyyy-MM-dd") ?? "");
    }
}
