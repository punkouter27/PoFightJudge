using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using PoFightJudge.Shared;
using PoFightJudge.Shared.Identifiers;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Mobile.Services;

public sealed class PoFightJudgeClient(
    HttpClient http,
    MobileConfig config,
    SecureSessionStore sessionStore,
    OfflineVerdictCache cache) : IAsyncDisposable
{
    private HubConnection? _hub;

    public event Action<CaptionDto>? CaptionReceived;
    public event Action<string>? HubNotice;

    private Uri BuildUri(string relativePath) => new(new Uri(config.BaseUrl + "/"), relativePath.TrimStart('/'));

    public async Task<AuthMeDto?> GetAuthMeAsync(CancellationToken ct = default)
        => await http.GetFromJsonAsync<AuthMeDto>(BuildUri(ApiRoutes.Auth.Me), ct).ConfigureAwait(false);

    public async Task<MatchId> StartFightAsync(string player1Tag, string player2Tag, string topic, CancellationToken ct = default)
    {
        var req = new CreateFightRequest(
            HostPersonaId.Referee,
            player1Tag.Trim().ToUpperInvariant(),
            player2Tag.Trim().ToUpperInvariant(),
            topic);
        using var resp = await http.PostAsJsonAsync(BuildUri(ApiRoutes.Fights.Base), req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var created = await resp.Content.ReadFromJsonAsync<CreateFightResponse>(ct).ConfigureAwait(false);
        return created?.MatchId ?? throw new InvalidOperationException("Missing MatchId in response.");
    }

    public async Task EndFightAsync(MatchId matchId, CancellationToken ct = default)
    {
        using var resp = await http.PostAsync(BuildUri(ApiRoutes.Fights.End(matchId)), content: null, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<MatchDto>> GetMatchesAsync(CancellationToken ct = default)
    {
        if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
        {
            return await cache.LoadMatchesAsync(ct).ConfigureAwait(false);
        }

        try
        {
            var matches = await http.GetFromJsonAsync<List<MatchDto>>(BuildUri(ApiRoutes.Matches.Base), ct).ConfigureAwait(false) ?? [];
            await cache.SaveMatchesAsync(matches, ct).ConfigureAwait(false);
            return matches;
        }
        catch (HttpRequestException)
        {
            return await cache.LoadMatchesAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<LeaderboardRowDto>> GetLeaderboardAsync(CancellationToken ct = default)
        => await http.GetFromJsonAsync<List<LeaderboardRowDto>>(BuildUri(ApiRoutes.Leaderboard.Url), ct).ConfigureAwait(false) ?? [];

    public async Task ConnectAndJoinFightAsync(MatchId matchId, CancellationToken ct = default)
    {
        var fakeUser = await sessionStore.GetFakeUserAsync().ConfigureAwait(false);
        if (_hub is not null)
        {
            await _hub.DisposeAsync().ConfigureAwait(false);
        }

        _hub = new HubConnectionBuilder()
            .WithUrl(BuildUri(ApiRoutes.Hubs.Live), options =>
            {
                options.AccessTokenProvider = () => sessionStore.GetBearerTokenAsync();
                if (!string.IsNullOrWhiteSpace(fakeUser))
                {
                    options.Headers["X-Fake-User"] = fakeUser;
                }
            })
            .WithAutomaticReconnect()
            .Build();

        _hub.On<CaptionDto>(LiveHubContract.Caption, dto => CaptionReceived?.Invoke(dto));
        _hub.On<string>(LiveHubContract.Error, msg => HubNotice?.Invoke($"Hub error: {msg}"));
        _hub.On(LiveHubContract.Ended, () => HubNotice?.Invoke("Fight ended by referee."));

        await _hub.StartAsync(ct).ConfigureAwait(false);
        await _hub.InvokeAsync(LiveHubContract.JoinFight, matchId.Value, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub is not null)
        {
            await _hub.DisposeAsync().ConfigureAwait(false);
        }
    }
}
