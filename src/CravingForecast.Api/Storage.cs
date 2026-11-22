using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using CravingForecast.Core;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

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

    /// <summary>Send times (UTC, round-trip format) of nudges already pushed, so each goes out once.</summary>
    public HashSet<string> NudgesSent { get; set; } = [];

    /// <summary>Send times (UTC, round-trip format) of "Was this accurate?" reminders already pushed.</summary>
    public HashSet<string> RemindersSent { get; set; } = [];

    /// <summary>Send times (UTC, round-trip format) of mid-zone "How are you doing?" prompts already pushed.</summary>
    public HashSet<string> CheckInPromptsSent { get; set; } = [];

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

/// <summary>A signed-in person. The id is also the key of their forecast state.</summary>
public sealed class AppUser : IdentityUser<Guid>;

/// <summary>One row per user holding their engine state as JSON, deleted with the account.</summary>
public sealed class StoredState
{
    public Guid UserId { get; set; }
    public string Json { get; set; } = "";
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>One browser or device that has said yes to notifications.</summary>
public sealed class PushSubscriptionRow
{
    public int Id { get; set; }
    public Guid UserId { get; set; }
    public string Endpoint { get; set; } = "";
    public string P256dh { get; set; } = "";
    public string Auth { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
}

/// <summary>Small values the server makes once and keeps, such as the keys that sign notifications.</summary>
public sealed class AppSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>Accounts (Identity), forecast state and the data-protection keys that sign sign-in tokens.</summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options), IDataProtectionKeyContext
{
    public DbSet<StoredState> States => Set<StoredState>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
    public DbSet<PushSubscriptionRow> PushSubscriptions => Set<PushSubscriptionRow>();
    public DbSet<AppSetting> Settings => Set<AppSetting>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<StoredState>(e =>
        {
            e.HasKey(s => s.UserId);
            e.HasOne<AppUser>().WithOne().HasForeignKey<StoredState>(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<PushSubscriptionRow>(e =>
        {
            e.HasIndex(s => s.Endpoint).IsUnique();
            e.HasOne<AppUser>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<AppSetting>().HasKey(s => s.Key);
    }
}

/// <summary>
/// Stores each user's engine state as a JSON document in the database. Writes are serialized per user
/// in-process, which is correct for a single server instance (SQLite); a multi-instance deployment
/// would add a concurrency token instead.
/// </summary>
public sealed class DbUserStore(AppDbContext db) : IUserStore
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Locks = new();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static SemaphoreSlim LockFor(Guid id) => Locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));

    public async Task<UserState?> GetAsync(Guid id)
    {
        var row = await db.States.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == id);
        return row is null ? null : JsonSerializer.Deserialize<UserState>(row.Json, Json);
    }

    public async Task CreateAsync(UserState state)
    {
        db.States.Add(new StoredState { UserId = state.Id, Json = JsonSerializer.Serialize(state, Json), UpdatedUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    public async Task<T?> UpdateAsync<T>(Guid id, Func<UserState, T> mutate)
    {
        var gate = LockFor(id);
        await gate.WaitAsync();
        try
        {
            var row = await db.States.FirstOrDefaultAsync(s => s.UserId == id);
            if (row is null) return default;
            var state = JsonSerializer.Deserialize<UserState>(row.Json, Json)!;
            var result = mutate(state);
            row.Json = JsonSerializer.Serialize(state, Json);
            row.UpdatedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return result;
        }
        finally { gate.Release(); }
    }
}

/// <summary>Lets `dotnet ef migrations add` build the context without starting the API.</summary>
public sealed class DesignTimeDbContextFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
