using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrecisionScoresDesktop.Sync;

// Thin typed HttpClient wrapper around the cloud SmarterASP backend.
// Only methods the sync workflows need; we don't speak the whole API
// surface. Takes a JWT in the constructor; swap-and-retry on expiry
// is left for the caller.
public sealed class CloudClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _jwt;

    public CloudClient(string baseUrl, string jwt, TimeSpan? timeout = null)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
        _http.Timeout = timeout ?? TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwt);
        _jwt = jwt;
    }

    public static async Task<LoginResult> LoginAsync(
        string baseUrl, string email, string password, CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
        var resp = await http.PostAsJsonAsync("profiles/authenticate",
            new { email, password }, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            return new LoginResult(false, null, null, $"login failed ({(int)resp.StatusCode}): {body}");
        }
        var raw = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(raw);
        var token = doc.RootElement.TryGetProperty("token", out var t) ? t.GetString() : null;
        if (string.IsNullOrEmpty(token))
        {
            return new LoginResult(false, null, null, "login response missing token");
        }
        return new LoginResult(true, token, raw, null);
    }

    // Returns the raw JSON body so the caller stores it verbatim and
    // avoids schema drift.
    public async Task<string> GetMatchesRawAsync(CancellationToken ct)
    {
        using var resp = await _http.GetAsync("match/GetAllMatches", ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    public async Task<string> GetMatchRawAsync(Guid matchId, CancellationToken ct)
    {
        using var resp = await _http.GetAsync($"match/GetMatch?id={matchId}", ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    public async Task<string> GetRegisteredShootersRawAsync(Guid matchId, CancellationToken ct)
    {
        using var resp = await _http.GetAsync($"match/GetRegisteredShooters?matchId={matchId}", ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    public async Task SaveScoreCardAsync(object payload, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync("match/SaveScoreCard", payload, ct);
        if (resp.IsSuccessStatusCode) return;
        if ((int)resp.StatusCode is 409 or 400 or 500)
        {
            // Mirror the frontend's upsert pattern — EditScoreCard on conflict.
            using var editResp = await _http.PutAsJsonAsync("match/EditScoreCard", payload, ct);
            editResp.EnsureSuccessStatusCode();
            return;
        }
        resp.EnsureSuccessStatusCode();
    }

    public void Dispose() => _http.Dispose();

    public sealed record LoginResult(bool Ok, string? Jwt, string? ProfileJson, string? Error);
}
