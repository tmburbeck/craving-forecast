namespace CravingForecast.Core;

public sealed record HourAdjustment(int[] Hours, double Amount, Trigger Context);

public sealed record ContextFactor(string Label, double Multiplier, IReadOnlyList<HourAdjustment> Adjustments);

/// <summary>Layer 2: passive daily signals (sleep, activity, calendar, day of week).</summary>
public static class ContextSignals
{
    public static IReadOnlyList<ContextFactor> For(OnboardingProfile p, DayOfWeek day, DailyContext? c)
    {
        var f = new List<ContextFactor>();
        int wake = p.WakeHour, workEnd = p.WorkEndHour, bed = p.BedHourExtended;

        if (day == DayOfWeek.Friday)
            f.Add(new("Friday evening: end-of-week fatigue", 1, [new(Hours.Range(17, 22), 0.15, Trigger.Social)]));
        if (day == DayOfWeek.Sunday)
            f.Add(new("Sunday afternoon: low structure, week-ahead stress", 1, [new(Hours.Range(13, 17), 0.15, Trigger.Bored)]));

        if (c is null) return f;

        switch (c.Sleep)
        {
            case SleepQuality.Poor:
                f.Add(new("Poor sleep: higher ghrelin, lower leptin, weaker impulse control", 1.2,
                    [new(Hours.Range(wake + 7, wake + 8), 0.12, Trigger.Sleep)]));
                break;
            case SleepQuality.Good:
                f.Add(new("Good sleep: slightly lower risk all day", 0.92, []));
                break;
        }

        if (c.BackToBackMeetings)
            f.Add(new("Back-to-back meetings: stress now, decision fatigue tonight", 1,
                [new(Hours.Range(workEnd, workEnd + 1), 0.20, Trigger.Stress), new(Hours.Range(bed - 2, bed - 1), 0.10, Trigger.Late)]));

        switch (c.Activity)
        {
            case ActivityLevel.High:
                f.Add(new("High activity: genuine hunger later; plan a protein-heavy dinner", 1,
                    [new(Hours.Range(workEnd + 1, workEnd + 2), 0.10, Trigger.Gap)]));
                break;
            case ActivityLevel.Low:
                f.Add(new("Low activity: more idle time", 1, [new(Hours.Range(wake + 8, wake + 10), 0.06, Trigger.Bored)]));
                break;
        }
        return f;
    }
}

/// <summary>Ceiling on how much of the forecast personal data may drive: day 1 → 0, day 7 → 0.5, day 14 → 1.</summary>
public static class PersonalizationSchedule
{
    public static double Ceiling(int day) => day switch
    {
        <= 1 => 0,
        <= 7 => (day - 1) / 12.0,
        < 14 => 0.5 + (day - 7) / 14.0,
        _ => 1
    };

    public static string Phase(int day) => day switch
    {
        <= 1 => "Archetype baseline",
        <= 7 => "Baseline + daily context",
        < 14 => "Personalizing",
        _ => "Personal patterns"
    };

    /// <summary>Evidence confidence for one hour: n / (n + K). About 2 weighted check-ins = 50%.</summary>
    public const double K = 2;
    public static double Confidence(double n) => n / (n + K);
}

public sealed record PersonalEstimate(double EffectiveCount, double Mean, Trigger? TopContext);

public static class PersonalEstimator
{
    /// <summary>Weighted mean craving level (0–1) near an hour; check-ins one hour away count half.</summary>
    public static PersonalEstimate At(int hour, IEnumerable<CheckIn> checkIns, TimeZoneInfo tz)
    {
        double n = 0, sum = 0;
        var ctx = new Dictionary<Trigger, double>();
        foreach (var c in checkIns)
        {
            var h = TimeZoneInfo.ConvertTime(c.At, tz).Hour;
            var d = Math.Min((h - hour + 24) % 24, (hour - h + 24) % 24);
            var wt = (d == 0 ? 1 : d == 1 ? 0.5 : 0) * c.Weight;
            if (wt == 0) continue;
            n += wt;
            sum += wt * c.Level / 10.0;
            foreach (var t in c.Context) ctx[t] = ctx.GetValueOrDefault(t) + wt;
        }
        Trigger? top = ctx.Count == 0 ? null : ctx.MaxBy(kv => kv.Value).Key;
        return new PersonalEstimate(n, n > 0 ? sum / n : 0, top);
    }
}

public sealed record HourForecast(int Hour, double Risk, double Baseline, double PersonalWeight, double EffectiveCount, Trigger Context);

public sealed record DangerZone(int StartHour, int EndHour, Trigger Context, double Peak, int[] Hours)
{
    public string Label => $"{Core.Hours.Format(StartHour)}–{Core.Hours.Format(EndHour + 1)}";
}

public sealed record Forecast(
    DateOnly Date,
    int Day,
    string Phase,
    double Ceiling,
    double PersonalShare,
    IReadOnlyList<ArchetypeMatch> Archetypes,
    IReadOnlyList<ActiveRule> Rules,
    IReadOnlyList<ContextFactor> Factors,
    IReadOnlyList<HourForecast> Hours,
    IReadOnlyList<DangerZone> Zones);

/// <summary>Layer 3: blends the archetype baseline with personal data, then applies today's context.</summary>
public static class ForecastEngine
{
    public static Forecast Build(
        OnboardingProfile profile,
        DateOnly date,
        DailyContext? context,
        IReadOnlyList<CheckIn> checkIns,
        IReadOnlyDictionary<string, RuleStats> ruleStats)
    {
        var day = date.DayNumber - profile.StartDate.DayNumber + 1;
        var ceiling = PersonalizationSchedule.Ceiling(day);
        var dow = date.DayOfWeek;
        var tz = profile.TimeZone;

        var baseline = BaselineBuilder.Build(profile, dow, ruleStats);
        var factors = ContextSignals.For(profile, dow, context);
        var mult = factors.Aggregate(1.0, (m, f) => m * f.Multiplier);
        var add = new double[24];
        var addCtx = new Trigger?[24];
        foreach (var adj in factors.SelectMany(f => f.Adjustments))
            foreach (var h in adj.Hours)
            {
                add[h] += adj.Amount;
                // A day signal relabels the hour only if it's strong or the baseline had no reason of its own.
                if (adj.Amount >= 0.2 || (adj.Amount >= 0.12 && baseline.Context[h] is null)) addCtx[h] = adj.Context;
            }

        // Only check-ins before the end of this date can inform it.
        var endOfDay = new DateTimeOffset(date.AddDays(1).ToDateTime(TimeOnly.MinValue), tz.GetUtcOffset(date.ToDateTime(TimeOnly.MinValue)));
        var usable = checkIns.Where(c => c.At < endOfDay).ToList();

        var hours = new List<HourForecast>(24);
        for (var h = 0; h < 24; h++)
        {
            var p = PersonalEstimator.At(h, usable, tz);
            var wP = ceiling * PersonalizationSchedule.Confidence(p.EffectiveCount);
            var blended = (1 - wP) * baseline.Risk[h] + wP * p.Mean;
            var risk = profile.IsAwake(h) || p.EffectiveCount > 0 ? Math.Clamp(blended * mult + add[h], 0, 1) : 0;
            var ctx = addCtx[h]
                      ?? (wP > 0.5 && p.TopContext is { } pc ? pc : baseline.Context[h])
                      ?? (h >= 18 ? Trigger.Late : Trigger.Slump);
            hours.Add(new HourForecast(h, risk, baseline.Risk[h], wP, p.EffectiveCount, ctx));
        }

        var awake = hours.Where(x => profile.IsAwake(x.Hour)).ToList();
        var share = awake.Count == 0 ? 0 : awake.Average(x => x.PersonalWeight);

        return new Forecast(date, day, PersonalizationSchedule.Phase(day), ceiling, share,
            ArchetypeMatcher.Match(profile), baseline.Rules, factors, hours, ZoneDetector.Detect(hours));
    }
}

public static class ZoneDetector
{
    public const double Threshold = 0.55;

    public static IReadOnlyList<DangerZone> Detect(IReadOnlyList<HourForecast> hours)
    {
        var spans = new List<(int start, int end)>();
        for (var i = 0; i < 24; i++)
        {
            if (hours[i].Risk < Threshold) continue;
            if (spans.Count > 0 && spans[^1].end == i - 1) spans[^1] = (spans[^1].start, i);
            else spans.Add((i, i));
        }
        // Merge a zone that runs through midnight.
        if (spans.Count > 1 && spans[0].start == 0 && spans[^1].end == 23)
        {
            var first = spans[0];
            spans.RemoveAt(0);
            spans[^1] = (spans[^1].start, first.end + 24);
        }
        // Always show the single riskiest hour so day 1 is never empty.
        if (spans.Count == 0)
        {
            var top = hours.MaxBy(h => h.Risk)!;
            if (top.Risk > 0) spans.Add((top.Hour, top.Hour));
        }

        return spans.Select(s =>
        {
            var hs = Hours.Range(s.start, s.end);
            var ctx = hs.GroupBy(h => hours[h].Context)
                        .OrderByDescending(g => g.Sum(h => hours[h].Risk))
                        .First().Key;
            return new DangerZone(s.start, s.end, ctx, hs.Max(h => hours[h].Risk), hs);
        }).ToList();
    }
}
