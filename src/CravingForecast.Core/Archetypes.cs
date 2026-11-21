namespace CravingForecast.Core;

public static class Hours
{
    /// <summary>Inclusive hour range that wraps past midnight, e.g. Range(22, 25) = 22, 23, 0, 1.</summary>
    public static int[] Range(int from, int to)
    {
        var list = new List<int>();
        for (var h = from; h <= to; h++) list.Add(((h % 24) + 24) % 24);
        return list.ToArray();
    }

    public static string Format(int hour)
    {
        hour = ((hour % 24) + 24) % 24;
        var h12 = hour % 12 == 0 ? 12 : hour % 12;
        return $"{h12} {(hour < 12 ? "AM" : "PM")}";
    }
}

/// <summary>A baseline rule: "this person is at elevated risk during these hours, for this reason".</summary>
public sealed record RuleWindow(
    string Id,
    string Name,
    int[] Hours,
    double Strength,
    Trigger Context,
    DayOfWeek[]? Days = null);

public sealed record Archetype(
    string Id,
    string Name,
    string Description,
    Func<OnboardingProfile, double> Score,
    Func<OnboardingProfile, IEnumerable<RuleWindow>> Windows);

public sealed record ArchetypeMatch(Archetype Archetype, double Score, double Strength);

/// <summary>Layer 1: rule-based archetypes built from patterns seen in counseling sessions.</summary>
public static class ArchetypeCatalog
{
    private static int B(bool x) => x ? 1 : 0;

    public static readonly IReadOnlyList<Archetype> All =
    [
        new("slump", "Afternoon Slumper",
            "Energy dips 2–3 hours after lunch; reaches for quick sugar or caffeine.",
            p => 1 + B(p.LargestMeal == MealTime.Lunch) + B(p.Skip == SkippedMeal.Breakfast) + 2 * B(p.Has(Trigger.Slump)) + B(p.Has(Trigger.Sleep)),
            p => [new("slump", "Afternoon Slumper", Hours.Range(p.WakeHour + 7, p.WakeHour + 8), 0.40, Trigger.Slump)]),

        new("revenge", "Revenge Bedtime Snacker",
            "Decision fatigue peaks in the last two hours before bed; snacking feels like reclaimed time.",
            p => 1 + 2 * B(p.Graze == GrazeDriver.Fatigue) + 2 * B(p.Has(Trigger.Late)) + B(p.Has(Trigger.Tv)) + 2 * B(p.LargestMeal == MealTime.Late),
            p => [new("revenge", "Revenge Bedtime Snacker", Hours.Range(p.BedHourExtended - 2, p.BedHourExtended - 1), 0.44, Trigger.Late)]),

        new("decompress", "Post-Work Decompressor",
            "The commute home or end of shift is when stress turns into eating.",
            p => 2 * B(p.Graze == GrazeDriver.Stress) + 2 * B(p.Has(Trigger.Stress))
                 + B(p.Schedule is WorkSchedule.Office or WorkSchedule.Early or WorkSchedule.Evening),
            p => [new("decompress", "Post-Work Decompressor", Hours.Range(p.WorkEndHour, p.WorkEndHour + 2), 0.40, Trigger.Stress)]),

        new("backload", "Backloader",
            "Light or skipped early meals push hunger into the afternoon and evening.",
            p => 2 * B(p.Skip == SkippedMeal.Breakfast) + B(p.LargestMeal is MealTime.Dinner or MealTime.Late) + 2 * B(p.Has(Trigger.Gap)),
            p =>
            [
                new("backload-gap", "Backloader: midday gap", Hours.Range(p.WakeHour + 4, p.WakeHour + 6), 0.32, Trigger.Gap),
                new("backload-eve", "Backloader: evening", Hours.Range(p.BedHourExtended - 5, p.BedHourExtended - 4), 0.22, Trigger.Gap)
            ]),

        new("grazer", "Boredom Grazer",
            "Unstructured time at home leads to repeated small trips to the kitchen.",
            p => 2 * B(p.Graze == GrazeDriver.Boredom) + 2 * B(p.Has(Trigger.Bored)) + 2 * B(p.Schedule == WorkSchedule.Remote) + 0.5 * B(p.Has(Trigger.Tv)),
            p =>
            [
                new("graze-pm", "Boredom Grazer: afternoon", Hours.Range(p.WakeHour + 8, p.WakeHour + 10), 0.30, Trigger.Bored),
                new("graze-eve", "Boredom Grazer: evening", Hours.Range(p.BedHourExtended - 4, p.BedHourExtended - 3), 0.24, Trigger.Tv)
            ]),

        new("weekend", "Weekend Drifter",
            "Structure loosens Friday to Sunday; social meals and long afternoons add up.",
            p => 0.5 + 2 * B(p.Has(Trigger.Social)),
            p => [new("weekend", "Weekend Drifter", Hours.Range(12, 21), 0.24, Trigger.Social,
                      [DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday])])
    ];

    public static readonly RuleWindow Circadian =
        new("circadian", "Evening appetite rise", Hours.Range(19, 22), 0.12, Trigger.Late);
}

public static class ArchetypeMatcher
{
    /// <summary>Returns up to three archetypes scoring at least 2 and at least half of the top score.</summary>
    public static IReadOnlyList<ArchetypeMatch> Match(OnboardingProfile profile)
    {
        var scored = ArchetypeCatalog.All
            .Select(a => (a, s: a.Score(profile)))
            .OrderByDescending(x => x.s)
            .ToList();
        var top = Math.Max(scored[0].s, 1);
        return scored
            .Where(x => x.s >= 2 && x.s >= 0.5 * top)
            .Take(3)
            .Select(x => new ArchetypeMatch(x.a, x.s, x.s / top))
            .ToList();
    }
}

/// <summary>
/// Trust in a rule, from "Was this accurate?" answers. Beta(3 + accurate, 1 + 1.5 × missed),
/// normalized so a rule with no feedback has weight 1. Misses count 1.5× because a wrong
/// prediction carries more information than a confirmed one.
/// </summary>
public static class RuleTrust
{
    public const double PriorAlpha = 3, PriorBeta = 1, MissWeight = 1.5, PhaseOutBelow = 0.4;

    public static double Weight(RuleStats? stats)
    {
        stats ??= new RuleStats();
        var a = PriorAlpha + stats.Accurate;
        var b = PriorBeta + stats.Missed * MissWeight;
        var priorMean = PriorAlpha / (PriorAlpha + PriorBeta);
        return Math.Min(1.3, a / (a + b) / priorMean);
    }

    public static bool IsPhasedOut(RuleStats? stats) => Weight(stats) < PhaseOutBelow;
}

public sealed record ActiveRule(RuleWindow Window, double Weight, bool PhasedOut);

public sealed record Baseline(IReadOnlyList<ActiveRule> Rules, double[] Risk, Trigger?[] Context);

public static class BaselineBuilder
{
    public const double AwakeFloor = 0.18;

    public static Baseline Build(OnboardingProfile profile, DayOfWeek day, IReadOnlyDictionary<string, RuleStats> stats)
    {
        var windows = new List<RuleWindow> { ArchetypeCatalog.Circadian };
        foreach (var m in ArchetypeMatcher.Match(profile))
            foreach (var w in m.Archetype.Windows(profile))
                if (w.Days is null || w.Days.Contains(day))
                    windows.Add(w with { Strength = w.Strength * m.Strength });

        var rules = windows.Select(w =>
        {
            stats.TryGetValue(w.Id, out var s);
            return new ActiveRule(w, RuleTrust.Weight(s), RuleTrust.IsPhasedOut(s));
        }).ToList();

        var risk = new double[24];
        var ctx = new Trigger?[24];
        var best = new double[24];
        for (var h = 0; h < 24; h++) risk[h] = profile.IsAwake(h) ? AwakeFloor : 0;

        foreach (var r in rules.Where(r => !r.PhasedOut))
            foreach (var h in r.Window.Hours)
            {
                var v = r.Window.Strength * r.Weight;
                risk[h] += v;
                if (v > best[h]) { best[h] = v; ctx[h] = r.Window.Context; }
            }

        for (var h = 0; h < 24; h++)
            risk[h] = profile.IsAwake(h) ? Math.Min(1, risk[h]) : 0;

        return new Baseline(rules, risk, ctx);
    }
}
