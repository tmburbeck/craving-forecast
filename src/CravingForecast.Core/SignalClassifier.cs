namespace CravingForecast.Core;

/// <summary>Turns raw device data (HealthKit, Health Connect, calendar) into Layer 2 signals.</summary>
public static class SignalClassifier
{
    public static SleepQuality Sleep(double hoursAsleep) => hoursAsleep switch
    {
        < 6 => SleepQuality.Poor,
        < 7 => SleepQuality.Ok,
        _ => SleepQuality.Good
    };

    public static ActivityLevel Activity(double steps) => steps switch
    {
        < 4000 => ActivityLevel.Low,
        > 10000 => ActivityLevel.High,
        _ => ActivityLevel.Normal
    };

    /// <summary>
    /// Total time asleep from possibly overlapping samples (a watch and a phone often both record).
    /// Overlaps are merged so the same hour isn't counted twice.
    /// </summary>
    public static double HoursAsleep(IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> intervals)
    {
        var sorted = intervals.Where(i => i.End > i.Start).OrderBy(i => i.Start).ToList();
        if (sorted.Count == 0) return 0;
        var total = TimeSpan.Zero;
        var (s, e) = sorted[0];
        foreach (var (start, end) in sorted.Skip(1))
        {
            if (start <= e) { if (end > e) e = end; continue; }
            total += e - s;
            (s, e) = (start, end);
        }
        return (total + (e - s)).TotalHours;
    }

    /// <summary>Back-to-back day: a run of 3+ meetings with gaps of 15 minutes or less, or 5+ hours of meetings.</summary>
    public static bool IsBackToBack(IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> meetings)
    {
        var list = meetings.Where(m => m.End > m.Start).OrderBy(m => m.Start).ToList();
        if (list.Count == 0) return false;
        if (list.Sum(m => (m.End - m.Start).TotalHours) >= 5) return true;

        int run = 1, longest = 1;
        for (var i = 1; i < list.Count; i++)
        {
            run = list[i].Start - list[i - 1].End <= TimeSpan.FromMinutes(15) ? run + 1 : 1;
            longest = Math.Max(longest, run);
        }
        return longest >= 3;
    }
}
