using System.Text;
using System.Text.Json;
using Carter;
using PoFightJudge.Api.Common;
using PoFightJudge.Api.Features.Auth;
using PoFightJudge.Api.Features.Fighters;
using PoFightJudge.Api.Features.Profiles;
using PoFightJudge.Api.Features.Records;
using PoFightJudge.Api.Features.Voice;
using PoFightJudge.Shared;
using PoFightJudge.Shared.Identifiers;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Api.Features.Watch;

/// <summary>
/// WATCH's round loop. The client drives it — it holds the transcript and asks for one line, then its audio, then
/// the next — so nothing here is a session. That keeps a refresh from losing a match and lets the client prefetch
/// the round after the one playing.
/// </summary>
public sealed class WatchEndpoints : ICarterModule
{

    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var watch = app.MapGroup(ApiRoutes.Watch.Base).WithTags("Watch").RequireRateLimiting(WatchServiceExtensions.AiPolicy);

        watch.MapPost(ApiRoutes.Watch.GenerateRoundSegment, GenerateRoundAsync).WithValidation<GenerateRoundRequest>().Produces<GenerateRoundResponse>().Produces(StatusCodes.Status404NotFound);
        watch.MapPost(ApiRoutes.Watch.GenerateRoundStreamSegment, StreamRoundAsync).WithValidation<GenerateRoundRequest>();
        watch.MapPost(ApiRoutes.Watch.RoundAudioStreamSegment, StreamRoundAudioAsync).WithValidation<RoundAudioRequest>();
        watch.MapPost(ApiRoutes.Watch.TranscribeSegment, TranscribeAsync).Produces<TranscribeResponse>().ProducesValidationProblem();
        watch.MapPost(ApiRoutes.Watch.VerdictSegment, VerdictAsync).WithValidation<VerdictRequest>().Produces<VerdictResponse>().Produces(StatusCodes.Status404NotFound);
    }

    /// <summary>One line for the persona whose turn it is. A person's turn never comes here — they say it themselves.</summary>
    private static async Task<IResult> GenerateRoundAsync(GenerateRoundRequest request, IProfileRepository profiles, IWatchAi ai, CancellationToken ct)
    {
        var sides = await ResolveAsync(request.Husband, request.Wife, profiles, ct);
        if (sides.Problem is { } problem)
        {
            return problem;
        }

        var (husband, wife) = sides.Value;
        if (!WatchTurns.IsSpeaker(request.Speaker))
        {
            return Problem("speaker", $"A watch has two sides: \"{WatchRound.Husband}\" or \"{WatchRound.Wife}\".");
        }

        var speakingHusband = string.Equals(request.Speaker, WatchRound.Husband, StringComparison.OrdinalIgnoreCase);
        var speaker = speakingHusband ? husband : wife;
        if (speaker.IsHuman)
        {
            return Problem("speaker", "That side is a person; they speak for themselves.");
        }

        if (request.History.Count >= WatchMatch.MaxLinesPerMatch)
        {
            return Problem("history", $"A match carries at most {WatchMatch.MaxLinesPerMatch} lines.");
        }

        var history = ToContext(request.History);
        var attitude = AttitudeSelector.Select(
            speaker,
            LastMoodOf(history, speakingHusband ? WatchRound.Wife : WatchRound.Husband),
            RoundNumber(history, speakingHusband ? WatchRound.Husband : WatchRound.Wife),
            request.WasSlapped);

        var result = await ai.GenerateArgumentAsync(husband, wife, history, request.Speaker, attitude, request.WasSlapped, request.Interjection, PoFightJudge.Shared.Models.Sides.Describe(request.Topic, request.HusbandStance, "the husband", "the wife"), ct);
        return Results.Ok(new GenerateRoundResponse(result.Text, result.Mood, attitude, ai.IsFake));
    }

    /// <summary>
    /// The same line, streamed as newline-delimited JSON: <c>{"delta":"…"}</c> per fragment, then one closing object
    /// with the finished line. NDJSON rather than server-sent events because the client reads it with the same typed
    /// HTTP client it uses everywhere else.
    /// </summary>
    private static async Task StreamRoundAsync(HttpContext http, GenerateRoundRequest request, IProfileRepository profiles, IWatchAi ai, CancellationToken ct)
    {
        var sides = await ResolveAsync(request.Husband, request.Wife, profiles, ct);
        if (sides.Problem is not null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var (husband, wife) = sides.Value;
        var speakingHusband = string.Equals(request.Speaker, WatchRound.Husband, StringComparison.OrdinalIgnoreCase);
        var speaker = speakingHusband ? husband : wife;

        // The same three refusals the plain route makes: not a side, a person's own turn, a match already full.
        if (!WatchTurns.IsSpeaker(request.Speaker) || speaker.IsHuman || request.History.Count >= WatchMatch.MaxLinesPerMatch)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var history = ToContext(request.History);
        var attitude = AttitudeSelector.Select(
            speaker,
            LastMoodOf(history, speakingHusband ? WatchRound.Wife : WatchRound.Husband),
            RoundNumber(history, speakingHusband ? WatchRound.Husband : WatchRound.Wife),
            request.WasSlapped);

        http.Response.ContentType = "application/x-ndjson";
        await foreach (var part in ai.StreamArgumentAsync(husband, wife, history, request.Speaker, attitude, request.WasSlapped, request.Interjection, PoFightJudge.Shared.Models.Sides.Describe(request.Topic, request.HusbandStance, "the husband", "the wife"), ct))
        {
            if (part.Delta is { } delta)
            {
                await WriteLineAsync(http, new RoundDeltaDto(delta), ct);
            }
            else if (part.Final is { } final)
            {
                await WriteLineAsync(http, new GenerateRoundResponse(final.Text, final.Mood, attitude, ai.IsFake), ct);
            }
        }
    }

    /// <summary>
    /// The judge's voice: belongs to neither side, and is not in either role's pool, so it never sounds like one of
    /// the two people it is ruling on. Gemini's "Orus" is its firm voice; Azure maps it into the lower register.
    /// </summary>
    internal static readonly TtsSettings JudgeVoice = new(Pitch: 1.0, Speed: 1.0, VoiceName: "Orus");

    /// <summary>
    /// The same audio, clause by clause as NDJSON, so playback can start on the first phrase instead of the whole
    /// line. Each object carries its own format: the provider chain can fall back mid-line.
    /// </summary>
    private static async Task StreamRoundAudioAsync(HttpContext http, RoundAudioRequest request, IProfileRepository profiles, CachingTtsService voice, CancellationToken ct)
    {
        TtsSettings settings;
        if (request.Speaker is not { } speaker)
        {
            settings = JudgeVoice;
        }
        else if (await profiles.GetByIdAsync(speaker, ct) is { } persona)
        {
            settings = persona.TtsSettings;
        }
        else
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        http.Response.ContentType = "application/x-ndjson";
        await foreach (var chunk in voice.GenerateTtsStreamAsync(request.Text, settings, ct))
        {
            await WriteLineAsync(http, new RoundAudioChunkDto(chunk.Index, chunk.Audio.Base64, chunk.Audio.Format, chunk.IsLast), ct);
        }
    }

    /// <summary>Turns one recorded turn into the words that were said. Silence comes back empty, which is a legitimate turn.</summary>
    private static async Task<IResult> TranscribeAsync(TranscribeRequest request, ITranscriptionService transcription, CancellationToken ct)
    {
        byte[] wav;
        try
        {
            wav = Convert.FromBase64String(request.WavBase64 ?? string.Empty);
        }
        catch (FormatException)
        {
            return Problem("wav", "The recording was not valid base64.");
        }

        if (wav.Length > Transcripts.MaxWavBytes)
        {
            return Problem("wav", $"A turn is limited to {Transcripts.MaxWavBytes / (1024 * 1024)} MB of audio.");
        }

        if (wav.Length > 0 && !Transcripts.LooksLikeWav(wav))
        {
            return Problem("wav", "That is not a WAV recording.");
        }

        var text = await transcription.TranscribeAsync(wav, ct);
        return Results.Ok(new TranscribeResponse(text, string.Equals(transcription.Name, "fake", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Ends the match: the judge rules, the analytics are computed from what was actually said, and the whole thing
    /// is persisted — the match, its turns, its audio, and a result row for each side. A persona's row goes to the
    /// watch results; a person's goes to their fighter results, so this debate feeds their profile too.
    /// </summary>
    private static async Task<IResult> VerdictAsync(
        VerdictRequest request,
        HttpContext http,
        IProfileRepository profiles,
        IWatchAi ai,
        IMatchRepository matches,
        IWatchResultRepository watchResults,
        IFighterRepository fighters,
        IFighterResultRepository fighterResults,
        IFighterWordsRepository fighterWords,
        WatchPersonaBacklog personas,
        IWatchAudioStore audio,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (request.Rounds.Count == 0 || request.Rounds.Count > WatchMatch.MaxLinesPerMatch)
        {
            return Problem("rounds", $"A match carries between one and {WatchMatch.MaxLinesPerMatch} lines.");
        }

        var sides = await ResolveAsync(request.Husband, request.Wife, profiles, ct);
        if (sides.Problem is { } problem)
        {
            return problem;
        }

        var userId = http.User.UserId();
        var matchId = MatchId.New();
        if (request.MatchId is { } supplied)
        {
            // A supplied id only ever means "judge this match again". One the caller does not own is not theirs to
            // overwrite, and minting a new match under their name would quietly duplicate somebody else's.
            // And only a watch: a fight's id here would replace its row with a watch and drop its recording.
            if (await matches.GetAsync(userId, supplied, ct) is not { Mode: MatchMode.Watch })
            {
                return Results.NotFound();
            }

            matchId = supplied;
        }

        var (husband, wife) = sides.Value;
        var history = ToContext(request.Rounds);
        var verdict = await ai.JudgeAsync(husband, wife, history, PoFightJudge.Shared.Models.Sides.Describe(request.Topic, request.HusbandStance, "the husband", "the wife"), ct);

        var totalWords = history.Sum(r => ArgueScoreCalculator.WordCount(r.Text));
        var husbandStats = ArgueScoreCalculator.Compute(LinesOf(history, WatchRound.Husband), totalWords);
        var wifeStats = ArgueScoreCalculator.Compute(LinesOf(history, WatchRound.Wife), totalWords);

        var now = clock.GetUtcNow();
        var rounds = new List<WatchRound>(request.Rounds.Count);
        for (var i = 0; i < request.Rounds.Count; i++)
        {
            var round = request.Rounds[i];
            var blobName = round.Audio is { } clip && !clip.IsEmpty
                ? await audio.SaveRoundAsync(matchId, i, new TtsAudio(clip.Base64, clip.Format), ct)
                : null;
            rounds.Add(new WatchRound(round.Speaker, round.Text, round.Mood, null, blobName, round.Audio?.Format ?? TtsAudioFormats.Pcm));
        }

        var match = WatchMatch.Complete(matchId, request.Husband, request.Wife, userId, request.Topic, rounds, verdict.Winner, verdict.Verdict, husbandStats, wifeStats, now);
        await PersistAsync(match, ai.IsFake, string.IsNullOrWhiteSpace(request.Topic) ? null : request.HusbandStance, matches, watchResults, fighters, fighterResults, fighterWords, now, ct);

        // A person who argued here has said one more thing about how they argue, so their card is rewritten from
        // everything they have said — after this reply, because the room is not waiting on it.
        if (request.Husband.IsHuman || request.Wife.IsHuman)
        {
            await personas.SubmitAsync(userId, matchId, ct);
        }

        return Results.Ok(new VerdictResponse(matchId, verdict.Winner, verdict.Verdict, verdict.HusbandScore, verdict.WifeScore, husbandStats.ToDto(), wifeStats.ToDto(), Persisted: true));
    }

    private static async Task PersistAsync(
        WatchMatch match,
        bool isFake,
        Stance? husbandStance,
        IMatchRepository matches,
        IWatchResultRepository watchResults,
        IFighterRepository fighters,
        IFighterResultRepository fighterResults,
        IFighterWordsRepository fighterWords,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await matches.UpsertAsync(
            new MatchDto(
                match.Id,
                match.UserId,
                MatchMode.Watch,
                now,
                now,
                match.Topic ?? string.Empty,
                match.Husband,
                match.Wife,
                SessionPhase.Done,
                SessionStatus.Ready,
                match.Winner,
                match.Verdict,
                isFake)
            {
                Side1Stance = husbandStance,
            },
            ct);

        await matches.SaveTurnsAsync(
            match.Id,
            match.Rounds.Select((round, index) => new TurnDto(
                match.Id,
                index,
                round.IsHusband ? Speaker.Player1 : Speaker.Player2,
                TurnKind.Round,
                round.Text)
            {
                Mood = round.Mood,
                AudioBlobName = round.AudioBlobName,
                AudioFormat = round.AudioFormat,
            }),
            ct);

        var winner = match.WinnerSide();
        var personaRows = new List<WatchResultDto>();
        foreach (var (side, opponent, stats) in Sides(match))
        {
            var won = winner is not null && string.Equals(winner.Id, side.Id, StringComparison.Ordinal);
            var draw = winner is null;
            var score = won ? 100 : draw ? 50 : 0;

            if (side.IsHuman)
            {
                // Their own record, and what this debate says about how they argue. A watch has no judge report per
                // side, so the snapshot is built from their own lines: what they repeat, how they open, their best.
                var spoken = match.Rounds.Where(r => string.Equals(r.Speaker, SpeakerOf(side, match), StringComparison.Ordinal)).Select(r => r.Text).ToList();
                var style = FightStyleSnapshotExtractor.FromSpokenLines(spoken);
                var seat = string.Equals(side.Id, match.Husband.Id, StringComparison.Ordinal) ? ProfileRole.Husband : ProfileRole.Wife;
                await fighters.EnsureAsync(FighterId.From(side.Id), now, seat, ct);
                await fighterResults.SaveAsync([new FighterResultDto(side.Id, match.UserId, match.Id, MatchMode.Watch, now, match.Topic ?? string.Empty, opponent.Id, won, draw, score, style)], ct);

                // And the words themselves, so a persona written after any later debate has this one to read too.
                await fighterWords.SaveAsync(SpokenDebate.From(side.Id, match.Id, now, MatchMode.Watch, spoken) with { UserId = match.UserId }, ct);
            }
            else
            {
                personaRows.Add(new WatchResultDto(side.Id, match.Id, now, match.Topic ?? string.Empty, opponent.Id, won, draw, score, stats.ToDto()));
            }
        }

        if (personaRows.Count > 0)
        {
            await watchResults.SaveAsync(personaRows, ct);
        }
    }

    /// <summary>Which speaker this side argued as, so their own lines can be picked out of the transcript.</summary>
    private static string SpeakerOf(MatchSide side, WatchMatch match) =>
        string.Equals(match.Husband.Id, side.Id, StringComparison.Ordinal) ? WatchTurns.Husband : WatchTurns.Wife;

    private static IEnumerable<(MatchSide Side, MatchSide Opponent, AdvancedStats Stats)> Sides(WatchMatch match) =>
    [
        (match.Husband, match.Wife, match.HusbandStats),
        (match.Wife, match.Husband, match.WifeStats),
    ];

    /// <summary>
    /// Loads both sides. A persona must exist; a person is minted on the spot from their tag, because their identity
    /// lives in the Fighters table rather than the cast. Two people arguing is a fight, not a watch.
    /// </summary>
    private static async Task<(bool Ok, (Profile Husband, Profile Wife) Value, IResult? Problem)> ResolveAsync(
        MatchSide husband,
        MatchSide wife,
        IProfileRepository profiles,
        CancellationToken ct)
    {
        if (string.Equals(husband.Id, wife.Id, StringComparison.OrdinalIgnoreCase))
        {
            return (false, default, Problem("sides", "The two sides must be different."));
        }

        if (husband.IsHuman && wife.IsHuman)
        {
            return (false, default, Problem("sides", "Two people arguing is a fight, not a watch."));
        }

        var husbandProfile = await LoadAsync(husband, ProfileRole.Husband, profiles, ct);
        var wifeProfile = await LoadAsync(wife, ProfileRole.Wife, profiles, ct);
        return husbandProfile is null || wifeProfile is null
            ? (false, default, Results.NotFound())
            : (true, (husbandProfile, wifeProfile), null);
    }

    private static async Task<Profile?> LoadAsync(MatchSide side, ProfileRole role, IProfileRepository profiles, CancellationToken ct) =>
        side.IsHuman
            ? Profile.CreateHuman(role, side.Id, side.DisplayName)
            : ProfileId.TryFrom(side.Id, out var id) ? await profiles.GetByIdAsync(id, ct) : null;

    private static List<RoundContext> ToContext(IReadOnlyList<WatchRoundDto> rounds) =>
        [.. rounds.Select(r => new RoundContext(r.Speaker, r.Text, r.Mood))];

    private static IEnumerable<string> LinesOf(IReadOnlyList<RoundContext> history, string speaker) =>
        history.Where(r => string.Equals(r.Speaker, speaker, StringComparison.OrdinalIgnoreCase)).Select(r => r.Text);

    private static int RoundNumber(IReadOnlyList<RoundContext> history, string speaker) =>
        history.Count(r => string.Equals(r.Speaker, speaker, StringComparison.OrdinalIgnoreCase)) + 1;

    private static string? LastMoodOf(IReadOnlyList<RoundContext> history, string speaker) =>
        history.LastOrDefault(r => string.Equals(r.Speaker, speaker, StringComparison.OrdinalIgnoreCase))?.Mood;

    private static IResult Problem(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [message] });

    private static async Task WriteLineAsync<T>(HttpContext http, T value, CancellationToken ct)
    {
        await http.Response.WriteAsync(JsonSerializer.Serialize(value, JsonSerializerOptions.Web) + "\n", Encoding.UTF8, ct);
        await http.Response.Body.FlushAsync(ct);
    }
}
