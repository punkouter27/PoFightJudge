using System.Collections.Concurrent;
using PoFightJudge.Api.Common;
using PoFightJudge.Api.Features.Fighters;
using PoFightJudge.Api.Features.Profiles;
using PoFightJudge.Api.Features.Records;
using PoFightJudge.Api.Features.Storage;
using PoFightJudge.Api.Features.Voice;
using PoFightJudge.Api.Features.Watch;
using PoFightJudge.Shared.Identifiers;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.TestSupport;

/// <summary>
/// The stores the API host runs on under test (E2EAPI, and E2EUI when Docker is absent). They keep the repository
/// contracts honest — same null-for-missing, same replace-on-upsert — without a storage account. Each store grows
/// alongside its real counterpart.
/// </summary>
public sealed class InMemoryProfileRepository : IProfileRepository
{
    private readonly ConcurrentDictionary<string, Profile> _rows = new(StringComparer.Ordinal);

    public Task<Profile?> GetByIdAsync(ProfileId id, CancellationToken ct = default) =>
        Task.FromResult(_rows.GetValueOrDefault(id.Value));

    public Task<IReadOnlyList<Profile>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Profile>>([.. _rows.Values.OrderBy(p => p.Initials, StringComparer.Ordinal)]);

    public Task UpsertAsync(Profile profile, CancellationToken ct = default)
    {
        _rows[profile.Initials] = profile;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(ProfileId id, CancellationToken ct = default)
    {
        _rows.TryRemove(id.Value, out _);
        return Task.CompletedTask;
    }
}

/// <summary>Runs the real image check (so the 400 paths are exercised) and keeps the PNG bytes in memory.</summary>
public sealed class InMemoryProfileImageService : IProfileImageService
{
    private readonly ConcurrentDictionary<string, byte[]> _faces = new(StringComparer.Ordinal);

    public async Task<Outcome<string>> StoreFaceAsync(ProfileId id, Stream image, CancellationToken ct = default)
    {
        var png = await ProfileImageService.ReadPngAsync(image, ct);
        if (!png.IsSuccess)
        {
            return Outcome.Failure<string>(png.Error!);
        }

        var name = ProfileImageService.BlobName(id);
        _faces[name] = png.Value;
        return Outcome.Success(name);
    }

    public Task<FaceImage?> GetFaceAsync(ProfileId id, CancellationToken ct = default) =>
        Task.FromResult(_faces.TryGetValue(ProfileImageService.BlobName(id), out var bytes) ? new FaceImage(bytes, ProfileImageService.ContentType) : null);

    public Task DeleteFaceAsync(ProfileId id, CancellationToken ct = default)
    {
        _faces.TryRemove(ProfileImageService.BlobName(id), out _);
        return Task.CompletedTask;
    }
}

/// <summary>The roster, in memory. Ensure is idempotent and stamps the last-seen time, exactly as the table one does.</summary>
public sealed class InMemoryFighterRepository : IFighterRepository
{
    private readonly ConcurrentDictionary<string, Fighter> _rows = new(StringComparer.Ordinal);

    public Task<Fighter> EnsureAsync(FighterId id, DateTimeOffset now, ProfileRole? role, CancellationToken ct = default)
    {
        var fighter = _rows.GetOrAdd(id.Value, _ => Fighter.Create(id, now));
        fighter.Seen(now);
        if (role is { } chosen)
        {
            fighter.ArgueAs(chosen);
        }

        return Task.FromResult(fighter);
    }

    public Task<Fighter?> GetAsync(FighterId id, CancellationToken ct = default) => Task.FromResult(_rows.GetValueOrDefault(id.Value));

    public Task<IReadOnlyList<Fighter>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Fighter>>([.. _rows.Values.OrderByDescending(f => f.LastSeenAt)]);

    public Task<Fighter?> RenameAsync(FighterId id, string? displayName, CancellationToken ct = default)
    {
        if (!_rows.TryGetValue(id.Value, out var fighter))
        {
            return Task.FromResult<Fighter?>(null);
        }

        fighter.Rename(displayName);
        return Task.FromResult<Fighter?>(fighter);
    }

    public Task DeleteAsync(FighterId id, CancellationToken ct = default)
    {
        _rows.TryRemove(id.Value, out _);
        return Task.CompletedTask;
    }
}

/// <summary>A person's results, in memory, keyed the way the table keys them so re-analysing replaces rather than doubles.</summary>
public sealed class InMemoryFighterResultRepository : IFighterResultRepository
{
    private readonly ConcurrentDictionary<(string Tag, string Match), FighterResultDto> _rows = new();

    public Task SaveAsync(IEnumerable<FighterResultDto> results, CancellationToken ct = default)
    {
        foreach (var result in results)
        {
            _rows[(result.Tag, result.MatchId.Value)] = result;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<FighterResultDto>> ListForAsync(FighterId tag, string userId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<FighterResultDto>>(
        [
            .. _rows.Values
                .Where(r => string.Equals(r.Tag, tag.Value, StringComparison.Ordinal) && string.Equals(r.UserId, userId, StringComparison.Ordinal))
                .OrderByDescending(r => r.At),
        ]);

    public Task<IReadOnlyList<FighterResultDto>> ListAllAsync(string userId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<FighterResultDto>>(
            [.. _rows.Values.Where(r => string.Equals(r.UserId, userId, StringComparison.Ordinal)).OrderByDescending(r => r.At)]);

    public Task<IReadOnlyList<FighterResultDto>> ListForAnyoneAsync(FighterId tag, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<FighterResultDto>>(
            [.. _rows.Values.Where(r => string.Equals(r.Tag, tag.Value, StringComparison.Ordinal)).OrderByDescending(r => r.At)]);

    public Task DeleteForMatchAsync(MatchId matchId, IEnumerable<string> tags, CancellationToken ct = default)
    {
        foreach (var tag in tags)
        {
            _rows.TryRemove((tag, matchId.Value), out _);
        }

        return Task.CompletedTask;
    }

    public Task DeleteForFighterAsync(FighterId tag, CancellationToken ct = default)
    {
        foreach (var key in _rows.Keys.Where(k => string.Equals(k.Tag, tag.Value, StringComparison.Ordinal)).ToList())
        {
            _rows.TryRemove(key, out _);
        }

        return Task.CompletedTask;
    }
}

/// <summary>What each person said, in memory. Same shape as the table: one row per (tag, match).</summary>
public sealed class InMemoryFighterWordsRepository : IFighterWordsRepository
{
    private readonly ConcurrentDictionary<(string Tag, string Match), SpokenDebate> _rows = new();

    public Task SaveAsync(SpokenDebate debate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(debate);
        if (!debate.IsEmpty)
        {
            _rows[(debate.Tag, debate.MatchId.Value)] = debate;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SpokenDebate>> ListAsync(FighterId tag, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SpokenDebate>>(
            [.. _rows.Values.Where(d => string.Equals(d.Tag, tag.Value, StringComparison.Ordinal)).OrderByDescending(d => d.At)]);

    public Task DeleteForMatchAsync(MatchId matchId, IEnumerable<string> tags, CancellationToken ct = default)
    {
        foreach (var tag in tags)
        {
            _rows.TryRemove((tag, matchId.Value), out _);
        }

        return Task.CompletedTask;
    }

    public Task DeleteForFighterAsync(FighterId tag, CancellationToken ct = default)
    {
        foreach (var key in _rows.Keys.Where(k => string.Equals(k.Tag, tag.Value, StringComparison.Ordinal)).ToList())
        {
            _rows.TryRemove(key, out _);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Matches, turns and analyses in memory, keeping the cascade honest: deleting a match takes its children.</summary>
public sealed class InMemoryMatchRepository(IWatchResultRepository watchResults, IFighterResultRepository fighterResults, IFighterWordsRepository fighterWords) : IMatchRepository
{
    private readonly ConcurrentDictionary<(string User, string Match), MatchDto> _matches = new();
    private readonly ConcurrentDictionary<string, List<TurnDto>> _turns = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AnalysisRecordDto> _analyses = new(StringComparer.Ordinal);

    public Task UpsertAsync(MatchDto match, CancellationToken ct = default)
    {
        _matches[(match.UserId, match.Id.Value)] = match;
        return Task.CompletedTask;
    }

    public Task<MatchDto?> GetAsync(string userId, MatchId id, CancellationToken ct = default) =>
        Task.FromResult(_matches.GetValueOrDefault((userId, id.Value)));

    public Task<IReadOnlyList<MatchDto>> ListAsync(string userId, MatchMode? mode = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MatchDto>>(
        [
            .. _matches.Values
                .Where(m => string.Equals(m.UserId, userId, StringComparison.Ordinal) && (mode is null || m.Mode == mode))
                .OrderByDescending(m => m.StartedAt),
        ]);

    /// <summary>
    /// The same narrowing the real repository does, in the same order, so a test over this exercises the shape of
    /// the answer rather than a second implementation of the rules.
    /// </summary>
    public Task<MatchPageDto> PageAsync(string userId, MatchQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var sane = query.Sane();
        var found = sane.Order(_matches.Values
                .Where(m => string.Equals(m.UserId, userId, StringComparison.Ordinal))
                .Where(m => sane.Mode is null || m.Mode == sane.Mode)
                .Where(m => sane.From is null || m.StartedAt >= sane.From)
                .Where(m => sane.To is null || m.StartedAt <= sane.To)
                .Where(m => Matches(m, sane.Text)))
            .ToList();

        return Task.FromResult(new MatchPageDto([.. found.Skip(sane.Skip).Take(sane.Take)], found.Count, sane.Skip, sane.Take));
    }

    private static bool Matches(MatchDto match, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var needle = text.Trim();
        return match.Topic.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || match.Side1.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || match.Side2.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || match.Side1.Id.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || match.Side2.Id.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || match.Winner.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    public Task SaveTurnsAsync(MatchId id, IEnumerable<TurnDto> turns, CancellationToken ct = default)
    {
        var stored = _turns.GetOrAdd(id.Value, _ => []);
        lock (stored)
        {
            foreach (var turn in turns)
            {
                stored.RemoveAll(t => t.Index == turn.Index);
                stored.Add(turn);
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TurnDto>> GetTurnsAsync(MatchId id, CancellationToken ct = default)
    {
        var stored = _turns.GetValueOrDefault(id.Value) ?? [];
        lock (stored)
        {
            return Task.FromResult<IReadOnlyList<TurnDto>>([.. stored.OrderBy(t => t.Index)]);
        }
    }

    public Task SaveAnalysisAsync(AnalysisRecordDto analysis, CancellationToken ct = default)
    {
        _analyses[analysis.MatchId.Value] = analysis;
        return Task.CompletedTask;
    }

    public Task<AnalysisRecordDto?> GetAnalysisAsync(MatchId id, CancellationToken ct = default) =>
        Task.FromResult(_analyses.GetValueOrDefault(id.Value));

    /// <summary>Everything left mid-read, oldest first — the same order the table query returns.</summary>
    public Task<IReadOnlyList<MatchDto>> ListUnfinishedAnalysesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MatchDto>>(
            [.. _matches.Values.Where(m => m.Status is SessionStatus.Analyzing or SessionStatus.Live).OrderBy(m => m.StartedAt)]);

    public async Task<bool> DeleteAsync(string userId, MatchId id, CancellationToken ct = default)
    {
        if (!_matches.TryRemove((userId, id.Value), out var match))
        {
            return false;
        }

        _turns.TryRemove(id.Value, out _);
        _analyses.TryRemove(id.Value, out _);
        await watchResults.DeleteForMatchAsync(id, match.Side1 is { IsHuman: false } ? [match.Side1.Id] : [], ct);
        await watchResults.DeleteForMatchAsync(id, match.Side2 is { IsHuman: false } ? [match.Side2.Id] : [], ct);
        await fighterResults.DeleteForMatchAsync(id, match.HumanSides.Select(s => s.Id), ct);
        await fighterWords.DeleteForMatchAsync(id, match.HumanSides.Select(s => s.Id), ct);
        return true;
    }
}

/// <summary>Persona results in memory, keyed like the table so re-judging replaces rather than doubles.</summary>
public sealed class InMemoryWatchResultRepository : IWatchResultRepository
{
    private readonly ConcurrentDictionary<(string Initials, string Match), WatchResultDto> _rows = new();

    public Task SaveAsync(IEnumerable<WatchResultDto> results, CancellationToken ct = default)
    {
        foreach (var result in results)
        {
            _rows[(result.Initials, result.MatchId.Value)] = result;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<WatchResultDto>> ListForAsync(string initials, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<WatchResultDto>>([.. _rows.Values.Where(r => string.Equals(r.Initials, initials, StringComparison.Ordinal)).OrderByDescending(r => r.At)]);

    public Task<IReadOnlyList<WatchResultDto>> ListAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<WatchResultDto>>([.. _rows.Values.OrderByDescending(r => r.At)]);

    public Task DeleteForMatchAsync(MatchId matchId, IEnumerable<string> initials, CancellationToken ct = default)
    {
        foreach (var side in initials)
        {
            _rows.TryRemove((side, matchId.Value), out _);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Round audio in memory, so a match can be played and replayed without a blob account.</summary>
public sealed class InMemoryWatchAudioStore : IWatchAudioStore
{
    private readonly ConcurrentDictionary<string, TtsAudio> _clips = new(StringComparer.Ordinal);

    public Task<string> SaveRoundAsync(MatchId matchId, int index, TtsAudio audio, CancellationToken ct = default)
    {
        if (audio.IsEmpty)
        {
            return Task.FromResult(string.Empty);
        }

        var name = IAudioBlobStore.Round(matchId, index, TtsAudioFormats.Extension(audio.Format));
        _clips[name] = audio;
        return Task.FromResult(name);
    }

    public Task<TtsAudio?> GetRoundAsync(MatchId matchId, int index, string format, CancellationToken ct = default) =>
        Task.FromResult<TtsAudio?>(_clips.TryGetValue(IAudioBlobStore.Round(matchId, index, TtsAudioFormats.Extension(format)), out var clip) ? clip : null);
}


/// <summary>
/// The share index, in memory. The token is the key, exactly as the partition key is in storage, so a test that
/// resolves one exercises the same shape of lookup the real repository does.
/// </summary>
public sealed class InMemoryShareRepository : IShareRepository
{
    private readonly ConcurrentDictionary<string, ShareTarget> _shares = new(StringComparer.Ordinal);

    public Task<string> ShareAsync(string userId, MatchId matchId, string? existingToken, CancellationToken ct = default)
    {
        if (existingToken is { Length: > 0 })
        {
            return Task.FromResult(existingToken);
        }

        var token = ShareRepository.NewToken();
        _shares[token] = new ShareTarget(userId, matchId);
        return Task.FromResult(token);
    }

    public Task RevokeAsync(string token, CancellationToken ct = default)
    {
        _shares.TryRemove(token, out _);
        return Task.CompletedTask;
    }

    public Task<ShareTarget?> ResolveAsync(string token, CancellationToken ct = default) =>
        Task.FromResult(_shares.GetValueOrDefault(token));
}
