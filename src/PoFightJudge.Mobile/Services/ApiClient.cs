using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using PoFightJudge.Shared;
using PoFightJudge.Shared.Diagnostics;
using PoFightJudge.Shared.Identifiers;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Mobile.Services;

/// <summary>Source-generated JSON with web defaults (enums carry their own string converters in Shared).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(AuthMeDto))]
[JsonSerializable(typeof(CreateFightRequest))]
[JsonSerializable(typeof(CreateFightResponse))]
[JsonSerializable(typeof(DebateSnapshotDto))]
[JsonSerializable(typeof(CaptionDto))]
[JsonSerializable(typeof(MatchPageDto))]
[JsonSerializable(typeof(MatchDto))]
[JsonSerializable(typeof(List<TurnDto>))]
[JsonSerializable(typeof(AnalysisResponse))]
[JsonSerializable(typeof(List<ProfileDto>))]
[JsonSerializable(typeof(GenerateRoundRequest))]
[JsonSerializable(typeof(GenerateRoundResponse))]
[JsonSerializable(typeof(RoundAudioRequest))]
[JsonSerializable(typeof(RoundAudioChunkDto))]
[JsonSerializable(typeof(VerdictRequest))]
[JsonSerializable(typeof(VerdictResponse))]
[JsonSerializable(typeof(TranscribeRequest))]
[JsonSerializable(typeof(TranscribeResponse))]
[JsonSerializable(typeof(List<LeaderboardRowDto>))]
[JsonSerializable(typeof(ShareResponse))]
[JsonSerializable(typeof(SharedMatchDto))]
[JsonSerializable(typeof(RecordedFightRequest))]
[JsonSerializable(typeof(DiagReportDto))]
[JsonSerializable(typeof(byte[]))]
[JsonSerializable(typeof(string))]
internal sealed partial class MobileJsonContext : JsonSerializerContext;

/// <summary>Every call the phone makes, on the routes in <see cref="ApiRoutes"/> (shared with the server, so they cannot drift).</summary>
public sealed class ApiClient(HttpClient http, MobileConfig config)
{
    private static readonly MobileJsonContext Json = MobileJsonContext.Default;

    public static bool Online => Connectivity.Current.NetworkAccess == NetworkAccess.Internet;

    public Uri U(string path) => new(new Uri(config.BaseUrl + "/"), path.TrimStart('/'));

    public HttpClient Http => http;

    // ── Auth ──
    public Task<AuthMeDto?> GetMeAsync() => ReadAsync(ct => http.GetAsync(U(ApiRoutes.Auth.Me), ct), Json.AuthMeDto);

    public Task<bool> GuestLoginAsync() => SendAsync(ct => http.PostAsync(U(ApiRoutes.Auth.Guest), null, ct));

    public Task<bool> LogoutAsync() => SendAsync(ct => http.PostAsync(U(ApiRoutes.Auth.Logout), null, ct));

    public Task<DiagReportDto?> GetDiagAsync() => ReadAsync(ct => http.GetAsync(U(ApiRoutes.Diag.Url), ct), Json.DiagReportDto);

    // ── FIGHT ──
    public async Task<(MatchId? Id, string? Problem)> StartFightAsync(CreateFightRequest request)
    {
        try
        {
            using HttpResponseMessage resp = await http.PostAsJsonAsync(U(ApiRoutes.Fights.Base), request, Json.CreateFightRequest);
            if (resp.IsSuccessStatusCode && await resp.Content.ReadFromJsonAsync(Json.CreateFightResponse) is { } made)
            {
                return (made.MatchId, null);
            }

            return (null, await ProblemAsync(resp, "The fight didn't start."));
        }
        catch (HttpRequestException)
        {
            return (null, "A live fight needs a connection. Record it offline instead.");
        }
    }

    public Task<bool> EndFightAsync(MatchId id) => SendAsync(ct => http.PostAsync(U(ApiRoutes.Fights.End(id)), null, ct));

    /// <summary>Posts a fight recorded offline. Null problem = stored (201) or already stored (200). Throws on an outage.</summary>
    public async Task<string?> UploadRecordedAsync(RecordedFightRequest meta, string wavPath)
    {
        using MultipartFormDataContent form = new();
        form.Add(new StringContent(JsonSerializer.Serialize(meta, Json.RecordedFightRequest)), "meta");
        StreamContent audio = new(File.OpenRead(wavPath));
        audio.Headers.ContentType = new("audio/wav");
        form.Add(audio, "audio", "fight.wav");
        using HttpResponseMessage resp = await http.PostAsync(U(ApiRoutes.Fights.RecordedUrl), form);
        if (resp.IsSuccessStatusCode)
        {
            return null;
        }

        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError)
        {
            throw new HttpRequestException($"Upload refused for now ({(int)resp.StatusCode}).", null, resp.StatusCode);
        }

        return await ProblemAsync(resp, "The server refused the recording.");
    }

    public Task<AnalysisResponse?> GetAnalysisAsync(MatchId id) => ReadAsync(ct => http.GetAsync(U(ApiRoutes.Fights.Analysis(id)), ct), Json.AnalysisResponse);

    public Task<bool> RetryAnalysisAsync(MatchId id) => SendAsync(ct => http.PostAsync(U(ApiRoutes.Fights.AnalysisRetry(id)), null, ct));

    // ── Records ──
    public Task<MatchPageDto?> ListMatchesAsync(int skip, int take, MatchMode? mode = null) =>
        ReadAsync(ct => http.GetAsync(U($"{ApiRoutes.Matches.Base}?skip={skip}&take={take}{(mode is { } m ? $"&mode={m}" : string.Empty)}"), ct), Json.MatchPageDto);

    public Task<MatchDto?> GetMatchAsync(MatchId id) => ReadAsync(ct => http.GetAsync(U(ApiRoutes.Matches.ById(id)), ct), Json.MatchDto);

    public Task<List<TurnDto>?> GetTurnsAsync(MatchId id) => ReadAsync(ct => http.GetAsync(U(ApiRoutes.Matches.Turns(id)), ct), Json.ListTurnDto);

    public Task<bool> DeleteMatchAsync(MatchId id) => SendAsync(ct => http.DeleteAsync(U(ApiRoutes.Matches.ById(id)), ct));

    public Task<ShareResponse?> ShareAsync(MatchId id) => ReadAsync(ct => http.PostAsync(U(ApiRoutes.Matches.Share(id)), null, ct), Json.ShareResponse);

    public Task<SharedMatchDto?> GetSharedAsync(string token) => ReadAsync(ct => http.GetAsync(U(ApiRoutes.Shares.ByToken(token)), ct), Json.SharedMatchDto);

    public Task<List<LeaderboardRowDto>?> GetLeaderboardAsync() => ReadAsync(ct => http.GetAsync(U(ApiRoutes.Leaderboard.Url), ct), Json.ListLeaderboardRowDto);

    public Task<List<ProfileDto>?> GetProfilesAsync() => ReadAsync(ct => http.GetAsync(U(ApiRoutes.Profiles.Base), ct), Json.ListProfileDto);

    // ── WATCH ──
    public Task<GenerateRoundResponse?> GenerateRoundAsync(GenerateRoundRequest request) =>
        ReadAsync(ct => http.PostAsJsonAsync(U(ApiRoutes.Watch.GenerateRoundUrl), request, Json.GenerateRoundRequest, ct), Json.GenerateRoundResponse);

    public Task<VerdictResponse?> VerdictAsync(VerdictRequest request) =>
        ReadAsync(ct => http.PostAsJsonAsync(U(ApiRoutes.Watch.VerdictUrl), request, Json.VerdictRequest, ct), Json.VerdictResponse);

    public Task<TranscribeResponse?> TranscribeAsync(byte[] wav) =>
        ReadAsync(ct => http.PostAsJsonAsync(U(ApiRoutes.Watch.TranscribeUrl), new TranscribeRequest(Convert.ToBase64String(wav)), Json.TranscribeRequest, ct), Json.TranscribeResponse);

    /// <summary>A line's voice, clause by clause, as the server streams it (NDJSON).</summary>
    public async IAsyncEnumerable<RoundAudioChunkDto> RoundAudioAsync(RoundAudioRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using HttpRequestMessage message = new(HttpMethod.Post, U(ApiRoutes.Watch.RoundAudioStreamUrl)) { Content = JsonContent.Create(request, Json.RoundAudioRequest) };
        using HttpResponseMessage resp = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            yield break;
        }

        await using Stream body = await resp.Content.ReadAsStreamAsync(ct);
        using StreamReader reader = new(body);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length > 0 && JsonSerializer.Deserialize(line, Json.RoundAudioChunkDto) is { } chunk)
            {
                yield return chunk;
                if (chunk.IsLast)
                {
                    yield break;
                }
            }
        }
    }

    private static async Task<string> ProblemAsync(HttpResponseMessage resp, string fallback)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return doc.RootElement.TryGetProperty("detail", out JsonElement d) && d.GetString() is { Length: > 0 } detail ? detail
                : doc.RootElement.TryGetProperty("title", out JsonElement t) && t.GetString() is { Length: > 0 } title ? title
                : fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    private static async Task<T?> ReadAsync<T>(Func<CancellationToken, Task<HttpResponseMessage>> send, JsonTypeInfo<T> type)
    {
        try
        {
            using HttpResponseMessage resp = await send(CancellationToken.None);
            return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync(type) : default;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            return default;
        }
    }

    private static async Task<bool> SendAsync(Func<CancellationToken, Task<HttpResponseMessage>> send)
    {
        try
        {
            using HttpResponseMessage resp = await send(CancellationToken.None);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}

/// <summary>Who is signed in; a stored Microsoft token keeps the app usable offline (recording still works).</summary>
public sealed class Session(ApiClient api, SecureSessionStore tokens)
{
    public AuthMeDto? Me { get; private set; }

    public bool SignedIn => Me?.Authenticated == true;

    public async Task<bool> RefreshAsync()
    {
        if (await api.GetMeAsync() is { } me)
        {
            Me = me;
            return SignedIn;
        }

        return SignedIn || !string.IsNullOrEmpty(await tokens.GetBearerTokenAsync()) || !ApiClient.Online;
    }

    public void Clear() => Me = null;
}
