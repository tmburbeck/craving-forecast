using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CravingForecast.Core.Contracts;

namespace CravingForecast.Mobile.Services;

/// <summary>
/// Signs the user in and out and keeps their tokens. Access tokens last about an hour; the refresh token
/// renews them quietly, so people stay signed in until they sign out or the refresh token is revoked.
/// </summary>
public sealed class AuthService
{
    private const string StorageKey = "salus.tokens";

    private sealed record Tokens(string Access, string Refresh, DateTimeOffset ExpiresAt);

    private readonly HttpClient _http = new() { BaseAddress = new Uri(ApiConfig.BaseUrl), Timeout = TimeSpan.FromSeconds(15) };
    private readonly JsonSerializerOptions _json;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private Tokens? _tokens;
    private bool _loaded;

    public AuthService(JsonSerializerOptions json) => _json = json;

    public bool IsSignedIn => _tokens is not null;

    /// <summary>Raised when the session ends, by choice or because the server no longer accepts it.</summary>
    public event Action? SignedOut;

    public async Task InitializeAsync()
    {
        if (_loaded) return;
        _loaded = true;
        var raw = await ReadAsync();
        if (raw is null) return;
        try { _tokens = JsonSerializer.Deserialize<Tokens>(raw); }
        catch (JsonException) { await DeleteAsync(); }
    }

    /// <returns>Null on success, otherwise a message to show the user.</returns>
    public async Task<string?> RegisterAsync(string email, string password)
    {
        var res = await _http.PostAsJsonAsync("api/auth/register", new AuthRequest(email, password), _json);
        if (res.StatusCode == HttpStatusCode.TooManyRequests) return TooMany;
        if (!res.IsSuccessStatusCode) return await ProblemMessageAsync(res) ?? "Couldn't create the account.";
        return await SignInAsync(email, password);
    }

    /// <returns>Null on success, otherwise a message to show the user.</returns>
    public async Task<string?> SignInAsync(string email, string password)
    {
        var res = await _http.PostAsJsonAsync("api/auth/login", new AuthRequest(email, password), _json);
        if (res.StatusCode == HttpStatusCode.Unauthorized)
            return await ProblemMessageAsync(res) == "LockedOut"
                ? "Too many wrong passwords, so this account is locked for 15 minutes to keep it safe. Try again after that."
                : "That email and password don't match.";
        if (res.StatusCode == HttpStatusCode.TooManyRequests) return TooMany;
        if (!res.IsSuccessStatusCode) return await ProblemMessageAsync(res) ?? "Couldn't sign in.";
        await SaveAsync((await res.Content.ReadFromJsonAsync<TokenResponse>(_json))!);
        return null;
    }

    private const string TooMany = "Too many attempts. Wait a minute and try again.";

    /// <summary>A current access token, renewed first if it's about to expire. Null when signed out.</summary>
    public async Task<string?> GetAccessTokenAsync()
    {
        var t = _tokens;
        if (t is null) return null;
        if (t.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return t.Access;
        return await RefreshAsync(t.Access) ? _tokens?.Access : null;
    }

    /// <summary>
    /// Swaps the refresh token for new tokens. Signs out when the server refuses it; a network error is
    /// thrown instead, so being offline never signs anyone out.
    /// </summary>
    public async Task<bool> RefreshAsync(string staleAccessToken)
    {
        await _refreshGate.WaitAsync();
        try
        {
            if (_tokens is null) return false;
            if (_tokens.Access != staleAccessToken) return true; // another request already renewed it

            var res = await _http.PostAsJsonAsync("api/auth/refresh", new RefreshRequest(_tokens.Refresh), _json);
            if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest or HttpStatusCode.Forbidden)
            {
                await SignOutAsync();
                return false;
            }
            res.EnsureSuccessStatusCode();
            await SaveAsync((await res.Content.ReadFromJsonAsync<TokenResponse>(_json))!);
            return true;
        }
        finally { _refreshGate.Release(); }
    }

    public async Task SignOutAsync()
    {
        _tokens = null;
        await DeleteAsync();
        Session.LastSync = null;
        SignedOut?.Invoke();
    }

    private async Task SaveAsync(TokenResponse t)
    {
        _tokens = new Tokens(t.AccessToken, t.RefreshToken, DateTimeOffset.UtcNow.AddSeconds(t.ExpiresIn));
        await WriteAsync(JsonSerializer.Serialize(_tokens));
    }

    /// <summary>Identity returns validation problems as { errors: { code: [messages] } }.</summary>
    private static async Task<string?> ProblemMessageAsync(HttpResponseMessage res)
    {
        try
        {
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                var messages = errors.EnumerateObject()
                    .SelectMany(e => e.Value.ValueKind == JsonValueKind.Array ? e.Value.EnumerateArray().Select(v => v.GetString()) : [])
                    .Where(m => !string.IsNullOrWhiteSpace(m))
                    .Distinct()
                    .ToList();
                if (messages.Count > 0) return string.Join("\n", messages);
            }
            return doc.RootElement.TryGetProperty("detail", out var detail) ? detail.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    // Tokens go in the platform's secure store. Where that isn't available (some unpackaged Windows
    // setups), fall back to app preferences so the Windows preview still works.
    private static async Task<string?> ReadAsync()
    {
        try { return await SecureStorage.Default.GetAsync(StorageKey); }
        catch { return Preferences.Get(StorageKey, null); }
    }

    private static async Task WriteAsync(string value)
    {
        try { await SecureStorage.Default.SetAsync(StorageKey, value); }
        catch { Preferences.Set(StorageKey, value); }
    }

    private static Task DeleteAsync()
    {
        try { SecureStorage.Default.Remove(StorageKey); } catch { /* not available on this platform */ }
        Preferences.Remove(StorageKey);
        return Task.CompletedTask;
    }
}

/// <summary>Adds the access token to API calls and, if the server says it has expired, renews it and retries once.</summary>
public sealed class AuthHandler(AuthService auth) : DelegatingHandler(new HttpClientHandler())
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await auth.GetAccessTokenAsync();
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized || token is null) return response;
        if (!await auth.RefreshAsync(token)) return response;

        var retry = new HttpRequestMessage(request.Method, request.RequestUri) { Content = request.Content, Version = request.Version };
        retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await auth.GetAccessTokenAsync());
        response.Dispose();
        return await base.SendAsync(retry, cancellationToken);
    }
}
