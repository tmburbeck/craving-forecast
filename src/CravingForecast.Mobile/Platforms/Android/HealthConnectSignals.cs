using CravingForecast.Core;
using CravingForecast.Mobile.Services;

namespace CravingForecast.Mobile.Health;

/// <summary>
/// Health Connect placeholder. Returning null makes the app ask "How did you sleep?" each morning instead,
/// so Android users get the same forecast with one extra tap.
///
/// To wire up Health Connect:
///   1. Add the Xamarin.AndroidX.Health.Connect.ConnectClient NuGet package.
///   2. Request READ_SLEEP and READ_STEPS (already declared in AndroidManifest.xml).
///   3. Read SleepSessionRecord and StepsRecord for the same windows HealthKitSignals uses,
///      then pass the totals to SignalClassifier.Sleep / SignalClassifier.Activity.
/// The Health Connect API is Kotlin-coroutine based, so a small Kotlin wrapper library
/// (bound into .NET) is often simpler than calling it from C# directly.
/// </summary>
public sealed class HealthConnectSignals : IHealthSignals
{
    public bool IsAvailable => false;
    public Task<SleepQuality?> GetLastNightSleepAsync() => Task.FromResult<SleepQuality?>(null);
    public Task<ActivityLevel?> GetYesterdayActivityAsync() => Task.FromResult<ActivityLevel?>(null);
}
