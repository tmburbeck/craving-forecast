using System.Net.Http.Json;
using System.Text.Json;
using CravingForecast.Core.Contracts;

namespace CravingForecast.Mobile.Services;

public sealed class ApiClient(HttpClient http, JsonSerializerOptions json)
{
    private static string D(DateOnly d) => d.ToString("yyyy-MM-dd");

    // Every call is about the signed-in user; AuthHandler adds their token.
    public async Task<MeResponse> GetMeAsync() =>
        (await http.GetFromJsonAsync<MeResponse>("api/me", json))!;

    public async Task<OnboardingResponse> CreateProfileAsync(OnboardingRequest request)
    {
        var res = await http.PostAsJsonAsync("api/me/profile", request, json);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<OnboardingResponse>(json))!;
    }

    public Task<ForecastDto?> GetForecastAsync() =>
        http.GetFromJsonAsync<ForecastDto>("api/me/forecast", json);

    public Task<ForecastDto?> PutContextAsync(DateOnly date, ContextRequest request) =>
        SendAsync<ForecastDto>(HttpMethod.Put, $"api/me/context/{D(date)}", request);

    public Task<ForecastDto?> CheckInAsync(CheckInRequest request) =>
        SendAsync<ForecastDto>(HttpMethod.Post, "api/me/checkins", request);

    public async Task<IReadOnlyList<ZoneDto>> GetPendingFeedbackAsync() =>
        await http.GetFromJsonAsync<List<ZoneDto>>("api/me/feedback/pending", json) ?? [];

    public Task<ForecastDto?> SendFeedbackAsync(FeedbackRequest request) =>
        SendAsync<ForecastDto>(HttpMethod.Post, "api/me/feedback", request);

    public async Task<IReadOnlyList<NudgeDto>> GetNudgesAsync(int days = 2) =>
        await http.GetFromJsonAsync<List<NudgeDto>>($"api/me/nudges?days={days}", json) ?? [];

    private async Task<T?> SendAsync<T>(HttpMethod method, string url, object body)
    {
        using var req = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body, options: json) };
        var res = await http.SendAsync(req);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<T>(json);
    }
}
