using System.Net.Http.Json;
using System.Text.Json;
using CravingForecast.Core.Contracts;

namespace CravingForecast.Mobile.Services;

public sealed class ApiClient(HttpClient http, JsonSerializerOptions json)
{
    private static Guid Me => Session.UserId ?? throw new InvalidOperationException("Not onboarded yet.");
    private static string D(DateOnly d) => d.ToString("yyyy-MM-dd");

    public async Task<OnboardingResponse> CreateUserAsync(OnboardingRequest request)
    {
        var res = await http.PostAsJsonAsync("api/users", request, json);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<OnboardingResponse>(json))!;
    }

    public Task<ForecastDto?> GetForecastAsync() =>
        http.GetFromJsonAsync<ForecastDto>($"api/users/{Me}/forecast", json);

    public Task<ForecastDto?> PutContextAsync(DateOnly date, ContextRequest request) =>
        SendAsync<ForecastDto>(HttpMethod.Put, $"api/users/{Me}/context/{D(date)}", request);

    public Task<ForecastDto?> CheckInAsync(CheckInRequest request) =>
        SendAsync<ForecastDto>(HttpMethod.Post, $"api/users/{Me}/checkins", request);

    public async Task<IReadOnlyList<ZoneDto>> GetPendingFeedbackAsync() =>
        await http.GetFromJsonAsync<List<ZoneDto>>($"api/users/{Me}/feedback/pending", json) ?? [];

    public Task<ForecastDto?> SendFeedbackAsync(FeedbackRequest request) =>
        SendAsync<ForecastDto>(HttpMethod.Post, $"api/users/{Me}/feedback", request);

    public async Task<IReadOnlyList<NudgeDto>> GetNudgesAsync(int days = 2) =>
        await http.GetFromJsonAsync<List<NudgeDto>>($"api/users/{Me}/nudges?days={days}", json) ?? [];

    private async Task<T?> SendAsync<T>(HttpMethod method, string url, object body)
    {
        using var req = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body, options: json) };
        var res = await http.SendAsync(req);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<T>(json);
    }
}
