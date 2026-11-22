using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CravingForecast.Core.Contracts;

namespace CravingForecast.Web.Services;

/// <summary>The API said the session is gone; the app sends the person to sign in.</summary>
public sealed class SignedOutException() : Exception("Not signed in.");

/// <summary>Calls to the Salus API. The browser sends the sign-in cookie with each one.</summary>
public sealed class Api(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private const string TooMany = "Too many attempts. Wait a minute and try again.";
    private const string LockedOut = "Too many wrong passwords, so this account is locked for 15 minutes to keep it safe. Try again after that.";

    // ---------- Accounts ----------

    /// <returns>Null on success, otherwise a message to show.</returns>
    public async Task<string?> SignInAsync(string email, string password)
    {
        var res = await http.PostAsJsonAsync("api/auth/login?useCookies=true", new AuthRequest(email, password), Json);
        if (res.StatusCode == HttpStatusCode.Unauthorized)
            return await ProblemAsync(res) == "LockedOut" ? LockedOut : "That email and password don't match.";
        if (res.StatusCode == HttpStatusCode.TooManyRequests) return TooMany;
        return res.IsSuccessStatusCode ? null : await ProblemAsync(res) ?? "Couldn't sign in.";
    }

    /// <returns>Null on success (and signed in), otherwise a message to show.</returns>
    public async Task<string?> RegisterAsync(string email, string password)
    {
        var res = await http.PostAsJsonAsync("api/auth/register", new AuthRequest(email, password), Json);
        if (res.StatusCode == HttpStatusCode.TooManyRequests) return TooMany;
        if (!res.IsSuccessStatusCode) return await ProblemAsync(res) ?? "Couldn't create the account.";
        return await SignInAsync(email, password);
    }

    /// <summary>Always succeeds from the user's point of view: the server never says whether the account exists.</summary>
    public async Task RequestPasswordResetAsync(string email)
    {
        var res = await http.PostAsJsonAsync("api/auth/forgot", new ForgotPasswordRequest(email), Json);
        if (res.StatusCode == HttpStatusCode.TooManyRequests) throw new HttpRequestException(TooMany);
        res.EnsureSuccessStatusCode();
    }

    /// <returns>Null on success, otherwise a message to show.</returns>
    public async Task<string?> ResetPasswordAsync(string email, string code, string newPassword)
    {
        var res = await http.PostAsJsonAsync("api/auth/reset", new ResetPasswordRequest(email, code, newPassword), Json);
        if (res.StatusCode == HttpStatusCode.TooManyRequests) return TooMany;
        return res.IsSuccessStatusCode ? null : await ProblemAsync(res) ?? "Couldn't change the password.";
    }

    public async Task SignOutAsync() => await http.PostAsync("api/auth/logout", null);

    /// <returns>Null when nobody is signed in.</returns>
    public async Task<MeResponse?> GetMeAsync()
    {
        var res = await http.GetAsync("api/me");
        if (res.StatusCode == HttpStatusCode.Unauthorized) return null;
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<MeResponse>(Json);
    }

    // ---------- The signed-in person's forecast ----------

    public Task<OnboardingResponse> CreateProfileAsync(OnboardingRequest r) => SendAsync<OnboardingResponse>(HttpMethod.Post, "api/me/profile", r);
    public Task<ForecastDto> GetForecastAsync() => GetAsync<ForecastDto>("api/me/forecast");
    public Task<List<ZoneDto>> GetPendingFeedbackAsync() => GetAsync<List<ZoneDto>>("api/me/feedback/pending");
    public Task<ForecastDto> PutContextAsync(DateOnly date, ContextRequest r) => SendAsync<ForecastDto>(HttpMethod.Put, $"api/me/context/{date:yyyy-MM-dd}", r);
    public Task<ForecastDto> CheckInAsync(CheckInRequest r) => SendAsync<ForecastDto>(HttpMethod.Post, "api/me/checkins", r);
    public Task<ForecastDto> SendFeedbackAsync(FeedbackRequest r) => SendAsync<ForecastDto>(HttpMethod.Post, "api/me/feedback", r);

    // ---------- Notifications ----------

    public async Task<string> GetPushPublicKeyAsync() => (await GetAsync<PushPublicKeyResponse>("api/push/public-key")).PublicKey;

    public async Task SubscribePushAsync(PushSubscriptionRequest r) => Check(await http.PostAsJsonAsync("api/me/push/subscriptions", r, Json));

    public async Task UnsubscribePushAsync(string endpoint) => Check(await http.PostAsJsonAsync("api/me/push/unsubscribe", new PushUnsubscribeRequest(endpoint), Json));

    public async Task<int> SendTestPushAsync() => (await ReadAsync<PushTestResponse>(await http.PostAsync("api/me/push/test", null))).Sent;

    private static void Check(HttpResponseMessage res)
    {
        if (res.StatusCode == HttpStatusCode.Unauthorized) throw new SignedOutException();
        res.EnsureSuccessStatusCode();
    }

    private async Task<T> GetAsync<T>(string url) => await ReadAsync<T>(await http.GetAsync(url));

    private async Task<T> SendAsync<T>(HttpMethod method, string url, object body) =>
        await ReadAsync<T>(await http.SendAsync(new HttpRequestMessage(method, url) { Content = JsonContent.Create(body, options: Json) }));

    private static async Task<T> ReadAsync<T>(HttpResponseMessage res)
    {
        if (res.StatusCode == HttpStatusCode.Unauthorized) throw new SignedOutException();
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<T>(Json))!;
    }

    /// <summary>Identity returns validation problems as { errors: { code: [messages] } }.</summary>
    private static async Task<string?> ProblemAsync(HttpResponseMessage res)
    {
        try
        {
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                var messages = errors.EnumerateObject()
                    .SelectMany(e => e.Value.ValueKind == JsonValueKind.Array ? e.Value.EnumerateArray().Select(v => v.GetString()) : [])
                    .Where(m => !string.IsNullOrWhiteSpace(m)).Distinct().ToList();
                if (messages.Count > 0) return string.Join(" ", messages);
            }
            return doc.RootElement.TryGetProperty("detail", out var d) ? d.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}
