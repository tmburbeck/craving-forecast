using System.Collections.Concurrent;
using System.Text.Json;
using CravingForecast.Core;

namespace CravingForecast.Api;

/// <summary>Everything the engine needs for one user.</summary>
public sealed class UserState
{
    public Guid Id { get; init; }
    public required OnboardingProfile Profile { get; set; }
    public List<CheckIn> CheckIns { get; set; } = [];

    /// <summary>Keyed by yyyy-MM-dd in the user's time zone.</summary>
    public Dictionary<string, DailyContext> Contexts { get; set; } = [];
    public Dictionary<string, RuleStats> RuleStats { get; set; } = [];

    /// <summary>"yyyy-MM-dd@startHour" for zones the user already rated.</summary>
    public HashSet<string> FeedbackGiven { get; set; } = [];

    public DailyContext? ContextFor(DateOnly date) => Contexts.GetValueOrDefault(Key(date));
    public static string Key(DateOnly d) => d.ToString("yyyy-MM-dd");
}

public interface IUserStore
{
    Task<UserState?> GetAsync(Guid id);
    Task CreateAsync(UserState state);

    /// <summary>Read-modify-write under a lock so concurrent check-ins aren't lost.</summary>
    Task<T?> UpdateAsync<T>(Guid id, Func<UserState, T> mutate);
}

/// <summary>
/// One JSON file per user. Fine for a prototype and easy to inspect; swap for EF Core + SQLite
/// or Postgres by implementing <see cref="IUserStore"/>.
/// </summary>
public sealed class JsonFileUserStore : IUserStore
{
    private readonly string _dir;
    private readonly JsonSerializerOptions _json;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public JsonFileUserStore(string directory, JsonSerializerOptions json)
    {
        _dir = directory;
        _json = json;
        Directory.CreateDirectory(_dir);
    }

    private string PathFor(Guid id) => Path.Combine(_dir, $"{id:N}.json");
    private SemaphoreSlim LockFor(Guid id) => _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));

    public async Task<UserState?> GetAsync(Guid id)
    {
        var path = PathFor(id);
        if (!File.Exists(path)) return null;
        await using var fs = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<UserState>(fs, _json);
    }

    public async Task CreateAsync(UserState state)
    {
        var gate = LockFor(state.Id);
        await gate.WaitAsync();
        try { await WriteAsync(state); }
        finally { gate.Release(); }
    }

    public async Task<T?> UpdateAsync<T>(Guid id, Func<UserState, T> mutate)
    {
        var gate = LockFor(id);
        await gate.WaitAsync();
        try
        {
            var state = await GetAsync(id);
            if (state is null) return default;
            var result = mutate(state);
            await WriteAsync(state);
            return result;
        }
        finally { gate.Release(); }
    }

    private async Task WriteAsync(UserState state)
    {
        var tmp = PathFor(state.Id) + ".tmp";
        await using (var fs = File.Create(tmp))
            await JsonSerializer.SerializeAsync(fs, state, _json);
        File.Move(tmp, PathFor(state.Id), overwrite: true);
    }
}
