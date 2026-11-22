using CravingForecast.Core.Contracts;
using Microsoft.JSInterop;

namespace CravingForecast.Web.Services;

/// <summary>What this browser can do about notifications right now.</summary>
public sealed record PushStatus(bool Supported, bool Ios, bool Installed, string Permission, bool Subscribed)
{
    /// <summary>iPhones and iPads only allow web notifications from Salus once it's on the Home Screen.</summary>
    public bool NeedsHomeScreen => Ios && !Installed;
    public bool Blocked => Permission == "denied";
    public bool CanAsk => Supported && !NeedsHomeScreen && !Blocked && !Subscribed;
}

/// <summary>Turns notifications on and off for this browser, keeping the server in step.</summary>
public sealed class PushService(IJSRuntime js, Api api) : IAsyncDisposable
{
    private Task<IJSObjectReference>? _module;
    private Task<IJSObjectReference> Module => _module ??= js.InvokeAsync<IJSObjectReference>("import", "./js/push.js").AsTask();

    public async Task<PushStatus> StatusAsync() => await (await Module).InvokeAsync<PushStatus>("status");

    /// <returns>True when notifications are now on.</returns>
    public async Task<bool> EnableAsync()
    {
        var key = await api.GetPushPublicKeyAsync();
        var sub = await (await Module).InvokeAsync<PushSubscriptionRequest?>("enable", key);
        if (sub is null) return false;
        await api.SubscribePushAsync(sub);
        return true;
    }

    /// <summary>After signing in: if this browser already allows notifications, attach it to the new account.</summary>
    public async Task ReattachAsync()
    {
        try
        {
            if (await (await Module).InvokeAsync<PushSubscriptionRequest?>("current") is { } sub)
                await api.SubscribePushAsync(sub);
        }
        catch (JSException) { /* notifications not available here */ }
    }

    public async Task DisableAsync()
    {
        var endpoint = await (await Module).InvokeAsync<string?>("disable");
        if (endpoint is not null) await api.UnsubscribePushAsync(endpoint);
    }

    public Task<int> SendTestAsync() => api.SendTestPushAsync();

    public async ValueTask DisposeAsync()
    {
        if (_module is not null) await (await _module).DisposeAsync();
    }
}
