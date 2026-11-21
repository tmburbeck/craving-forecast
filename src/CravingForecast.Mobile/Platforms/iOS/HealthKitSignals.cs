using CravingForecast.Core;
using CravingForecast.Mobile.Services;
using Foundation;
using HealthKit;

namespace CravingForecast.Mobile.Health;

/// <summary>Reads last night's sleep and yesterday's steps from Apple Health (read-only).</summary>
public sealed class HealthKitSignals : IHealthSignals
{
    private readonly HKHealthStore _store = new();
    private bool _asked;

    public bool IsAvailable => HKHealthStore.IsHealthDataAvailable;

    private static HKCategoryType SleepType => HKCategoryType.Create(HKCategoryTypeIdentifier.SleepAnalysis)!;
    private static HKQuantityType StepType => HKQuantityType.Create(HKQuantityTypeIdentifier.StepCount)!;

    private async Task<bool> AuthorizeAsync()
    {
        if (!IsAvailable) return false;
        if (_asked) return true;
        // HealthKit never says whether *read* access was granted; a denied user simply returns no samples,
        // which this class reports as null so the app falls back to the morning tap.
        var result = await _store.RequestAuthorizationToShareAsync(new NSSet(), new NSSet(SleepType, StepType));
        _asked = result.Item1;
        return _asked;
    }

    public async Task<SleepQuality?> GetLastNightSleepAsync()
    {
        if (!await AuthorizeAsync()) return null;

        var from = DateTime.Today.AddHours(-6); // 6 PM yesterday
        var predicate = HKQuery.GetPredicateForSamples((NSDate)from.ToUniversalTime(), (NSDate)DateTime.UtcNow, HKQueryOptions.None);
        var tcs = new TaskCompletionSource<HKSample[]>();
        var query = new HKSampleQuery(SleepType, predicate, 0, null, (_, results, _) => tcs.TrySetResult(results ?? []));
        _store.ExecuteQuery(query);

        var asleep = (await tcs.Task)
            .OfType<HKCategorySample>()
            .Where(s =>
            {
                var v = (HKCategoryValueSleepAnalysis)(long)s.Value;
                return v != HKCategoryValueSleepAnalysis.InBed && v != HKCategoryValueSleepAnalysis.Awake;
            })
            .Select(s => (Start: new DateTimeOffset((DateTime)s.StartDate), End: new DateTimeOffset((DateTime)s.EndDate)))
            .ToList();

        return asleep.Count == 0 ? null : SignalClassifier.Sleep(SignalClassifier.HoursAsleep(asleep));
    }

    public async Task<ActivityLevel?> GetYesterdayActivityAsync()
    {
        if (!await AuthorizeAsync()) return null;

        var start = DateTime.Today.AddDays(-1);
        var predicate = HKQuery.GetPredicateForSamples((NSDate)start.ToUniversalTime(), (NSDate)DateTime.Today.ToUniversalTime(), HKQueryOptions.StrictStartDate);
        var tcs = new TaskCompletionSource<double?>();
        var query = new HKStatisticsQuery(StepType, predicate, HKStatisticsOptions.CumulativeSum,
            (_, result, _) => tcs.TrySetResult(result?.SumQuantity()?.GetDoubleValue(HKUnit.Count)));
        _store.ExecuteQuery(query);

        var steps = await tcs.Task;
        return steps is null ? null : SignalClassifier.Activity(steps.Value);
    }
}
