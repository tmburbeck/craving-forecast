using CravingForecast.Core;
using CravingForecast.Core.Contracts;

namespace CravingForecast.Api;

public static class Map
{
    public static ZoneDto Zone(DangerZone z) => new(z.StartHour, z.EndHour, z.Label, z.Context, Math.Round(z.Peak, 3));

    public static NudgeDto Nudge(Core.Nudge n) => new(n.SendAt, n.Title, n.Body, n.Tips, Zone(n.Zone));

    public static ForecastDto Forecast(Forecast f, Core.Nudge? next) => new(
        f.Date, f.Day, f.Phase,
        Math.Round(f.Ceiling, 3), Math.Round(f.PersonalShare, 3),
        f.Archetypes.Select(a => new ArchetypeDto(a.Archetype.Id, a.Archetype.Name, a.Archetype.Description, Math.Round(a.Strength, 2))).ToList(),
        f.Factors.Select(x => new FactorDto(x.Label, x.Multiplier)).ToList(),
        f.Rules.Select(r => new RuleDto(r.Window.Id, r.Window.Name, Hours.Format(r.Window.Hours[0]), Math.Round(Math.Min(1, r.Weight), 2), r.PhasedOut)).ToList(),
        f.Zones.Select(Zone).ToList(),
        f.Hours.Select(h => new HourDto(h.Hour, Math.Round(h.Risk, 3), Math.Round(h.Baseline, 3), Math.Round(h.PersonalWeight, 3), h.Context)).ToList(),
        next is null ? null : Nudge(next));
}
