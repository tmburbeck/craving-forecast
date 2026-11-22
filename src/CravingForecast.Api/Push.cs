using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using CravingForecast.Core;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.EntityFrameworkCore;
using LibPushMessage = Lib.Net.Http.WebPush.PushMessage;
using LibPushSubscription = Lib.Net.Http.WebPush.PushSubscription;

namespace CravingForecast.Api;

public sealed record PushMessage(string Title, string Body, string Url, string Tag);

public enum PushResult { Sent, Gone, Failed }

public interface IPushSender
{
    Task<PushResult> SendAsync(PushSubscriptionRow subscription, PushMessage message, CancellationToken ct = default);
}

/// <summary>
/// The VAPID key pair that identifies this server to browsers' push services. Made on first use and kept
/// in the database, so there's nothing to configure and subscriptions survive restarts and redeploys.
/// </summary>
public sealed class VapidKeyStore(IServiceScopeFactory scopes)
{
    private const string PublicKeyName = "vapid.public";
    private const string PrivateKeyName = "vapid.private";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (string Public, string Private)? _keys;

    public async Task<(string Public, string Private)> GetAsync()
    {
        if (_keys is { } cached) return cached;
        await _gate.WaitAsync();
        try
        {
            if (_keys is { } again) return again;
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var pub = await db.Settings.FindAsync(PublicKeyName);
            var priv = await db.Settings.FindAsync(PrivateKeyName);
            if (pub is null || priv is null)
            {
                var made = Generate();
                db.Settings.AddRange(new AppSetting { Key = PublicKeyName, Value = made.Public }, new AppSetting { Key = PrivateKeyName, Value = made.Private });
                await db.SaveChangesAsync();
                _keys = made;
            }
            else _keys = (pub.Value, priv.Value);
            return _keys.Value;
        }
        finally { _gate.Release(); }
    }

    /// <summary>A P-256 key pair in the VAPID format: the uncompressed public point and the private scalar, base64url.</summary>
    private static (string Public, string Private) Generate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = key.ExportParameters(includePrivateParameters: true);
        return (B64Url([0x04, .. p.Q.X!, .. p.Q.Y!]), B64Url(p.D!));
    }

    private static string B64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Sends notifications through each browser's push service (Apple, Google, Mozilla, Microsoft).</summary>
public sealed class WebPushSender(VapidKeyStore keys, IConfiguration config, ILogger<WebPushSender> log) : IPushSender
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly PushServiceClient _client = new(new HttpClient { Timeout = TimeSpan.FromSeconds(20) });
    private VapidAuthentication? _auth;

    public async Task<PushResult> SendAsync(PushSubscriptionRow s, PushMessage message, CancellationToken ct = default)
    {
        if (_auth is null)
        {
            var (publicKey, privateKey) = await keys.GetAsync();
            // Push services need a contact for the sender: a mailto: address or the site's https address.
            _auth = new VapidAuthentication(publicKey, privateKey)
            {
                Subject = config["Push:Subject"] ?? "https://salus-tmburbeck-bafcfcg0exfpduh6.westus3-01.azurewebsites.net"
            };
        }

        var subscription = new LibPushSubscription { Endpoint = s.Endpoint, Keys = new Dictionary<string, string> { ["p256dh"] = s.P256dh, ["auth"] = s.Auth } };
        var push = new LibPushMessage(JsonSerializer.Serialize(message, Json))
        {
            TimeToLive = 1800, // a heads-up for a zone 30 minutes away is no use after that
            Urgency = PushMessageUrgency.High
        };
        try
        {
            await _client.RequestPushMessageDeliveryAsync(subscription, push, _auth, VapidAuthenticationScheme.Vapid, ct);
            return PushResult.Sent;
        }
        catch (PushServiceClientException e) when (e.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound)
        {
            return PushResult.Gone; // the person turned notifications off or the browser dropped the subscription
        }
        catch (Exception e) when (e is PushServiceClientException or HttpRequestException or TaskCanceledException)
        {
            log.LogWarning(e, "Push to {Host} failed", new Uri(s.Endpoint).Host);
            return PushResult.Failed;
        }
    }
}

/// <summary>
/// Once a minute, pushes each nudge whose time has come to every device its owner turned notifications on for.
/// Nudges up to <see cref="CatchUp"/> late still go out, so a server that was asleep catches up when it wakes.
/// Each nudge is marked sent before it's pushed, so it's never sent twice.
/// </summary>
public sealed class NudgeDispatcher(IServiceScopeFactory scopes, ForecastService svc, IPushSender sender, IConfiguration config, ILogger<NudgeDispatcher> log)
    : BackgroundService
{
    public static readonly TimeSpan CatchUp = TimeSpan.FromMinutes(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Push:DispatchEnabled", true)) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested) { log.LogError(e, "Nudge dispatch failed"); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <returns>How many notifications were delivered.</returns>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IUserStore>();
        var subscriptions = await db.PushSubscriptions.AsNoTracking().ToListAsync(ct);
        var delivered = 0;
        var gone = new List<int>();

        foreach (var devices in subscriptions.GroupBy(s => s.UserId))
        {
            var state = await store.GetAsync(devices.Key);
            if (state is null || Due(state).Count == 0) continue;

            // Claim what's due under the user's lock, then send outside it.
            var claimed = await store.UpdateAsync(devices.Key, u =>
            {
                var due = Due(u);
                foreach (var d in due) SentSet(u, d.Kind).Add(d.Key);
                var cutoff = svc.Now.AddDays(-2);
                u.NudgesSent.RemoveWhere(k => DateTimeOffset.TryParse(k, out var t) && t < cutoff);
                u.RemindersSent.RemoveWhere(k => DateTimeOffset.TryParse(k, out var t) && t < cutoff);
                u.CheckInPromptsSent.RemoveWhere(k => DateTimeOffset.TryParse(k, out var t) && t < cutoff);
                return due;
            }) ?? [];

            foreach (var d in claimed)
                foreach (var device in devices)
                {
                    var result = await sender.SendAsync(device, d.Message, ct);
                    if (result == PushResult.Sent) delivered++;
                    if (result == PushResult.Gone) gone.Add(device.Id);
                }
        }

        if (gone.Count > 0)
            await db.PushSubscriptions.Where(s => gone.Contains(s.Id)).ExecuteDeleteAsync(ct);
        return delivered;
    }

    private enum Kind { Nudge, CheckInPrompt, Reminder }

    private sealed record DueMessage(string Key, Kind Kind, PushMessage Message);

    private static HashSet<string> SentSet(UserState u, Kind kind) => kind switch
    {
        Kind.Nudge => u.NudgesSent,
        Kind.CheckInPrompt => u.CheckInPromptsSent,
        _ => u.RemindersSent
    };

    /// <summary>
    /// Nudges, mid-zone check-in prompts and "Was this accurate?" reminders that are due and unsent. Nudges look at yesterday too (a zone
    /// just after midnight is planned from yesterday); reminders only cover today's zones, which is all Today
    /// asks about, and are skipped once the zone has been rated.
    /// </summary>
    private List<DueMessage> Due(UserState u)
    {
        var now = svc.Now;
        var today = svc.Today(u.Profile);
        bool InWindow(DateTimeOffset at) => at <= now && at > now - CatchUp;

        var nudges = new[] { today.AddDays(-1), today }
            .SelectMany(d => NudgePlanner.Plan(u.Profile, svc.ForecastFor(u, d)))
            .Where(n => InWindow(n.SendAt) && !u.NudgesSent.Contains(Key(n.SendAt)))
            .GroupBy(n => Key(n.SendAt)).Select(g => g.First())
            .Select(n => new DueMessage(Key(n.SendAt), Kind.Nudge, new PushMessage(n.Title, n.Body, "/today", $"nudge-{Key(n.SendAt)}")));

        var reminders = FeedbackReminders.Plan(u.Profile, svc.ForecastFor(u, today))
            .Where(r => InWindow(r.SendAt) && !u.RemindersSent.Contains(Key(r.SendAt)))
            .Where(r => !u.FeedbackGiven.Contains($"{UserState.Key(today)}@{r.Zone.StartHour}"))
            .Select(r => new DueMessage(Key(r.SendAt), Kind.Reminder, new PushMessage(r.Title, r.Body, "/today", $"feedback-{Key(r.SendAt)}")));

        // Halfway through a longer zone, unless they've already checked in since it started.
        var prompts = MidZoneCheckIns.Plan(u.Profile, svc.ForecastFor(u, today))
            .Where(p => InWindow(p.SendAt) && !u.CheckInPromptsSent.Contains(Key(p.SendAt)))
            .Where(p => !u.CheckIns.Any(c => c.Source == CheckInSource.User && c.At >= ZoneStart(p) && c.At <= now))
            .Select(p => new DueMessage(Key(p.SendAt), Kind.CheckInPrompt, new PushMessage(p.Title, p.Body, "/checkin", $"checkin-{Key(p.SendAt)}")));

        DateTimeOffset ZoneStart(ZonePrompt p)
        {
            var local = today.ToDateTime(new TimeOnly(p.Zone.StartHour, 0));
            return new DateTimeOffset(local, u.Profile.TimeZone.GetUtcOffset(local));
        }

        return nudges.Concat(prompts).Concat(reminders).ToList();
    }

    private static string Key(DateTimeOffset at) => at.UtcDateTime.ToString("O");
}

/// <summary>
/// Azure's free plan puts the app to sleep after about 20 minutes without visits, and a sleeping app can't send
/// nudges. When Push:KeepAwakeUrl is set (production only), the app visits its own public address every
/// 10 minutes, which resets that timer. A paid plan with "Always On" makes this unnecessary.
/// </summary>
public sealed class KeepAwake(IConfiguration config, ILogger<KeepAwake> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var url = config["Push:KeepAwakeUrl"];
        if (string.IsNullOrWhiteSpace(url)) return;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { (await http.GetAsync(new Uri(new Uri(url), "/api/health"), stoppingToken)).Dispose(); }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested) { log.LogWarning(e, "Keep-awake visit failed"); }
        }
    }
}
