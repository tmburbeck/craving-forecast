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

            // Claim the due nudges under the user's lock, then send outside it.
            var claimed = await store.UpdateAsync(devices.Key, u =>
            {
                var due = Due(u);
                foreach (var n in due) u.NudgesSent.Add(Key(n));
                var cutoff = svc.Now.AddDays(-2);
                u.NudgesSent.RemoveWhere(k => DateTimeOffset.TryParse(k, out var t) && t < cutoff);
                return due;
            }) ?? [];

            foreach (var n in claimed)
                foreach (var device in devices)
                {
                    var result = await sender.SendAsync(device, new PushMessage(n.Title, n.Body, "/today", $"nudge-{Key(n)}"), ct);
                    if (result == PushResult.Sent) delivered++;
                    if (result == PushResult.Gone) gone.Add(device.Id);
                }
        }

        if (gone.Count > 0)
            await db.PushSubscriptions.Where(s => gone.Contains(s.Id)).ExecuteDeleteAsync(ct);
        return delivered;
    }

    /// <summary>Today's and yesterday's nudges (a zone just after midnight is planned from yesterday) that are due and unsent.</summary>
    private List<Nudge> Due(UserState u)
    {
        var now = svc.Now;
        var today = svc.Today(u.Profile);
        return new[] { today.AddDays(-1), today }
            .SelectMany(d => NudgePlanner.Plan(u.Profile, svc.ForecastFor(u, d)))
            .Where(n => n.SendAt <= now && n.SendAt > now - CatchUp && !u.NudgesSent.Contains(Key(n)))
            .GroupBy(Key).Select(g => g.First())
            .ToList();
    }

    private static string Key(Nudge n) => n.SendAt.UtcDateTime.ToString("O");
}
