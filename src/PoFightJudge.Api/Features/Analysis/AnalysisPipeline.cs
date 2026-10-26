using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using PoFightJudge.Api.Features.Ai;
using PoFightJudge.Api.Features.Diagnostics;
using PoFightJudge.Api.Features.Fight;
using PoFightJudge.Api.Features.Fighters;
using PoFightJudge.Api.Features.Live;
using PoFightJudge.Api.Features.Records;
using PoFightJudge.Api.Features.Storage;
using PoFightJudge.Shared.Identifiers;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Api.Features.Analysis;

/// <summary>
/// What happens to a fight once it is over: the recording goes up, a transcript is found or paid for, the speakers
/// are resolved, everything countable is counted, the judge rules, and both fighters get a row on their record.
/// Status is written at every step, so the room can poll and see where it has got to.
/// </summary>
public sealed partial class AnalysisPipeline(
    IServiceScopeFactory scopes,
    IGeminiFilesClient files,
    IGeminiTranscribeClient transcriber,
    IGeminiJudgeClient judge,
    TimeProvider clock,
    IOptions<AnalysisOptions> options,
    ILogger<AnalysisPipeline> logger,
    IHubContext<LiveHub>? hub = null,
    IClaimChecker? claims = null) : BackgroundService, IAnalysisIntake
{
    public const string UserFacingFailure = "The analysis could not be completed. Try again in a moment.";

    /// <summary>What the log line calls a transcript the model read back, as against one the room already had.</summary>
    public const string GeminiTranscribeSource = "gemini-transcribe";

    /// <summary>Below this the judge is not called: an empty recording invites it to invent a fight that never happened.</summary>
    public const int MinWordsForJudging = 30;

    /// <summary>The same gate in seconds, so thirty muttered words from one side still fails it.</summary>
    public const double MinSpeechSecondsForJudging = 12.0;

    private readonly Channel<(string UserId, MatchId MatchId)> _queue = Channel.CreateUnbounded<(string, MatchId)>();

    public ValueTask SubmitAsync(string userId, MatchId matchId, CancellationToken ct) => _queue.Writer.WriteAsync((userId, matchId), ct);

    /// <summary>Drains the queue a few fights at a time. The cap keeps both memory and spend bounded.</summary>
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Parallel.ForEachAsync(
            _queue.Reader.ReadAllAsync(stoppingToken),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, options.Value.MaxParallelAnalyses),
                CancellationToken = stoppingToken,
            },
            async (job, ct) => await RunAsync(job.UserId, job.MatchId, ct));

    /// <summary>One job, guarded: only cancellation may escape, or the whole drain loop stops with it.</summary>
    private async ValueTask RunAsync(string userId, MatchId matchId, CancellationToken ct)
    {
        // The judge is the biggest prompt in the app; it counts against the allowance of whoever fought.
        AiLatencyTracker.Caller = userId;
        try
        {
            await ProcessAsync(userId, matchId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogFailed(logger, matchId.Value, ex);

            // The detail stays in the log. A fight that was deleted while this ran must not get a resurrected row.
            using var scope = scopes.CreateScope();
            var matches = scope.ServiceProvider.GetRequiredService<IMatchRepository>();
            if (await matches.GetAsync(userId, matchId, CancellationToken.None) is { } match)
            {
                await SaveStatusAsync(matches, matchId, AnalysisStatus.Failed, null, UserFacingFailure, CancellationToken.None);
                await MarkAsync(matches, match, SessionStatus.Failed, CancellationToken.None);
            }
        }
    }

    /// <summary>The whole pipeline for one fight. Public so a test can drive it without the background loop.</summary>
    public async Task ProcessAsync(string userId, MatchId matchId, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        using var scope = scopes.CreateScope();
        var matches = scope.ServiceProvider.GetRequiredService<IMatchRepository>();
        var results = scope.ServiceProvider.GetRequiredService<IFighterResultRepository>();
        var words = scope.ServiceProvider.GetRequiredService<IFighterWordsRepository>();
        var blobs = scope.ServiceProvider.GetRequiredService<IAudioBlobStore>();

        var match = await matches.GetAsync(userId, matchId, ct)
            ?? throw new InvalidOperationException($"Fight {matchId.Value} was not found for this user.");
        var blobName = match.AudioBlobName ?? throw new InvalidOperationException("That fight has no recording.");

        await SaveStatusAsync(matches, matchId, AnalysisStatus.Diarizing, null, null, ct, starting: true);
        match = await MarkAsync(matches, match, SessionStatus.Analyzing, ct);


        // The turns are read while the recording uploads, and the upload is also what buys a browser-side
        // transcript the time it needs to arrive.
        var turnsTask = matches.GetTurnsAsync(matchId, ct);

        // Stored compressed, sent as PCM. The Files API takes an Ogg/Opus upload happily and reports it ACTIVE,
        // and then generateContent answers 400 INVALID_ARGUMENT for it — measured against the live endpoint on
        // 2026-09-07, under audio/ogg and audio/opus alike, where the same clip as a WAV is accepted. So the disk
        // keeps the small version and the model gets the one it will read.
        //
        // Scoped to the upload and nothing more: a recording is tens of megabytes, and holding it for the length of
        // the analysis meant two concurrent fights were carrying both of them through the judge as well.
        const string MimeType = "audio/wav";
        GeminiFile file;
        await using (var recording = await RecordingSource.OpenAsync(blobs, blobName, DebateOrchestrator.PlayerSampleRate, ct))
        {
            file = await files.UploadAndWaitAsync(recording.Content, recording.Length, MimeType, $"{matchId.Value}-players.wav", clock, ct);
        }

        // Taken back when the reading is over, however it ends.
        await using var uploaded = new UploadedRecording(files, file.Name, logger);

        // The cheapest usable transcript wins. Only a fight that produced neither is worth paying to read back.
        var (existing, source) = await ReadExistingTranscriptAsync(blobs, matchId, ct);
        var transcript = existing ?? await transcriber.TranscribeAsync(file.Uri, MimeType, ct);
        if (existing is null)
        {
            source = GeminiTranscribeSource;

            // Kept, so that if the judge fails after this the next attempt does not pay to transcribe it again.
            using var kept = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(transcript, GeminiJudgeClient.JsonOptions));
            await blobs.UploadAsync(IAudioBlobStore.DiarizedTranscript(matchId), kept, "application/json", ct);
        }

        var turns = await turnsTask;
        var mapped = SpeakerMapper.Map(transcript, turns);
        LogTranscribed(logger, matchId.Value, transcript.Words.Count, source, mapped.Note);

        var first = SpeechMetrics.Compute(Speaker.Player1, mapped, turns);
        var second = SpeechMetrics.Compute(Speaker.Player2, mapped, turns);

        // Without this gate the model invents facts and quotes from twelve seconds of breathing, and the headline
        // then contradicts the recording it came from.
        JudgeOutputDto output;
        var ruled = mapped.Words.Count >= MinWordsForJudging && first.TalkSeconds + second.TalkSeconds >= MinSpeechSecondsForJudging;
        if (!ruled)
        {
            LogTooShort(logger, matchId.Value, mapped.Words.Count, first.TalkSeconds + second.TalkSeconds);
            output = NothingToJudge();
        }
        else
        {
            await SaveStatusAsync(matches, matchId, AnalysisStatus.Judging, null, null, ct);
            output = await judge.JudgeAsync(
                new JudgeRequest(
                    Sides.Describe(match.Topic, match.Side1Stance, "player1", "player2") ?? match.Topic,
                    match.Side1.DisplayName,
                    match.Side2.DisplayName,
                    file.Uri,
                    MimeType,
                    mapped,
                    turns,
                    first,
                    second,
                    match.Verdict),
                ct);
        }

        if (ruled && claims is not null)
        {
            output = await CheckClaimsAsync(claims, match.Topic, output, matchId, ct);
        }

        var overall = new OverallVerdictDto(
            ToSpeaker(output.Overall.WinnerLogic),
            ToSpeaker(output.Overall.WinnerCorrect),
            ToSpeaker(output.Overall.Overall),
            output.Overall.Reasons,
            output.Overall.Summary,
            output.Overall.Recap)
        { Ruled = ruled };

        var report = new AnalysisReportDto(
            matchId,
            match.Topic,
            new PlayerReportDto(match.Side1.DisplayName, first, output.Player1),
            new PlayerReportDto(match.Side2.DisplayName, second, output.Player2),
            overall,
            match.Verdict,
            mapped.Flagged,
            mapped.Note,
            Math.Round(stopwatch.Elapsed.TotalSeconds, 1),
            clock.GetUtcNow(),
            HighlightFinder.Find(output.Player1, output.Player2, mapped));

        // Read again before anything is written back. The row in hand is minutes old: the fight may have been deleted
        // since, in which case writing it would bring it back, or shared, in which case writing the old copy would
        // drop the link.
        if (await matches.GetAsync(userId, matchId, ct) is not { } current)
        {
            LogDeletedMeanwhile(logger, matchId.Value);
            return;
        }

        await SaveStatusAsync(matches, matchId, AnalysisStatus.Ready, JsonSerializer.Serialize(report, GeminiJudgeClient.JsonOptions), null, ct);
        await SaveWordsAsync(words, match, mapped, ct);

        // A fight nobody said enough in has no winner. Recording one would hand somebody a win, and the other a loss,
        // for sitting in silence.
        if (ruled)
        {
            await SaveRecordsAsync(results, match, report, mapped, ct);
        }

        await MarkAsync(matches, current with { Winner = ruled ? WinnerTag(match, overall) : string.Empty }, SessionStatus.Ready, ct);
        LogReady(logger, matchId.Value, stopwatch.Elapsed.TotalSeconds);

        // Last, and after the room can already read the ruling: the persona is a bonus read from a report that is
        // on the record, and it is the one step here that costs a model call per fighter.
        if (ruled)
        {
            var personas = scope.ServiceProvider.GetRequiredService<FighterPersonaWriter>();
            await personas.WriteAsync(match, report, ct);
        }
    }

    /// <summary>
    /// Both fighters' claims, re-read against a web search in one call. A bonus on top of the ruling, never a reason
    /// to lose it: if the search fails, the judge's own verdicts stand.
    /// </summary>
    private async Task<JudgeOutputDto> CheckClaimsAsync(IClaimChecker checker, string topic, JudgeOutputDto output, MatchId matchId, CancellationToken ct)
    {
        var one = output.Player1.Claims;
        var two = output.Player2.Claims;
        if (one.Count + two.Count == 0)
        {
            return output;
        }

        try
        {
            var checkedClaims = await checker.CheckAsync(topic, [.. one, .. two], ct);
            return output with
            {
                Player1 = output.Player1 with { Claims = [.. checkedClaims.Take(one.Count)] },
                Player2 = output.Player2 with { Claims = [.. checkedClaims.Skip(one.Count)] },
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            LogFactCheckFailed(logger, matchId.Value, ex);
            return output;
        }
    }

    [LoggerMessage(EventId = 5009, Level = LogLevel.Warning, Message = "Analysis {MatchId}: the fact check failed; the judge's verdicts stand")]
    private static partial void LogFactCheckFailed(ILogger logger, string matchId, Exception ex);

    /// <summary>
    /// One row per fighter, keyed on the tag the fight was started under, each carrying the snapshot their style
    /// profile is built from. Re-analysing a fight overwrites those rows rather than counting it twice.
    /// </summary>
    private static async Task SaveRecordsAsync(
        IFighterResultRepository results,
        MatchDto match,
        AnalysisReportDto report,
        MappedTranscript transcript,
        CancellationToken ct)
    {
        if (!Initials.ArePair(match.Side1.Id, match.Side2.Id))
        {
            return;
        }

        var at = match.EndedAt ?? match.StartedAt;
        await results.SaveAsync(
            [
                Row(Speaker.Player1, match.Side1.Id, match.Side2.Id, report.Player1),
                Row(Speaker.Player2, match.Side2.Id, match.Side1.Id, report.Player2),
            ],
            ct);

        FighterResultDto Row(Speaker side, string tag, string opponent, PlayerReportDto theirs) => new(
            tag,
            match.UserId,
            match.Id,
            MatchMode.Fight,
            at,
            match.Topic,
            opponent,
            report.Overall.Overall == side,
            Draw: false,
            Score(theirs.Assessment),
            FightStyleSnapshotExtractor.FromFight(theirs.Assessment, theirs.Metrics, FirstWords(transcript, side)));
    }

    /// <summary>
    /// What each person actually said, kept as words so their persona can be written from every debate they have
    /// spoken in rather than from this one. Idempotent on (tag, match), like the result rows beside it.
    /// </summary>
    private static async Task SaveWordsAsync(IFighterWordsRepository words, MatchDto match, MappedTranscript transcript, CancellationToken ct)
    {
        var at = match.EndedAt ?? match.StartedAt;
        foreach (var (side, speaker) in new[] { (match.Side1, Speaker.Player1), (match.Side2, Speaker.Player2) }.Where(s => s.Item1.IsHuman))
        {
            await words.SaveAsync(
                SpokenDebate.From(side.Id, match.Id, at, MatchMode.Fight, transcript.For(speaker).OrderBy(w => w.Start).Select(w => w.Text)) with { UserId = match.UserId },
                ct);
        }
    }

    /// <summary>
    /// One number for how well they argued, out of a hundred: the five things the judge scores about the argument
    /// itself. Emotion and politeness are deliberately not in it — this is a record of arguing, not of behaving.
    /// </summary>
    private static int Score(PlayerAssessmentDto assessment) =>
        Math.Clamp(
            (assessment.Logic + assessment.Clarity + assessment.Persuasiveness + assessment.EvidenceUse + assessment.RebuttalQuality) * 2,
            0,
            100);

    /// <summary>The opening of the first thing this side said, which is where a habit shows first.</summary>
    private static string FirstWords(MappedTranscript transcript, Speaker side) =>
        string.Join(' ', transcript.For(side).OrderBy(w => w.Start).Take(FightStyleSnapshotExtractor.OpenerWords).Select(w => w.Text));

    /// <summary>
    /// The transcript this fight already produced, if there is one: the host's own captions. The
    /// source is logged, so a run that quietly fell back to the paid model is visible afterwards.
    /// </summary>
    private async Task<(TranscriptDto? Transcript, string Source)> ReadExistingTranscriptAsync(IAudioBlobStore blobs, MatchId matchId, CancellationToken ct)
    {
        // An earlier attempt at this same fight already paid for one.
        if (await ReadTranscriptAsync(blobs, matchId, IAudioBlobStore.DiarizedTranscript(matchId), ct) is { } diarized)
        {
            return (diarized, "diarized, kept from an earlier attempt");
        }

        if (options.Value.UseLiveCaptionTranscript
            && await ReadTranscriptAsync(blobs, matchId, IAudioBlobStore.LiveTranscript(matchId), ct) is { } captions
            && captions.Words.Count >= Math.Max(0, options.Value.MinCaptionTranscriptWords))
        {
            return (captions, "live-caption");
        }

        return (null, "diarized");
    }

    /// <summary>Reads one stored transcript. Anything missing or malformed is simply not there.</summary>
    private async Task<TranscriptDto?> ReadTranscriptAsync(IAudioBlobStore blobs, MatchId matchId, string blobName, CancellationToken ct)
    {
        try
        {
            await using var stream = await blobs.OpenReadAsync(blobName, ct);
            if (stream is null)
            {
                return null;
            }

            var transcript = await JsonSerializer.DeserializeAsync<TranscriptDto>(stream, GeminiJudgeClient.JsonOptions, ct);
            return transcript is { Words.Count: > 0 } ? transcript : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            LogTranscriptUnusable(logger, matchId.Value, ex);
            return null;
        }
    }

    private static Speaker ToSpeaker(string value) =>
        PlayerIdExtensions.TryParse(value, out var id) ? id.ToSpeaker() : Speaker.Player1;

    private static string WinnerTag(MatchDto match, OverallVerdictDto overall) =>
        overall.Overall == Speaker.Player1 ? match.Side1.Id : match.Side2.Id;

    /// <summary>A ruling that admits nothing was said, so the headline cannot contradict the recording.</summary>
    private static JudgeOutputDto NothingToJudge() => new(
        NothingToAssess(),
        NothingToAssess(),
        new JudgeOverallDto(
            "player1",
            "player1",
            "player1",
            ["Neither of you said enough to judge.", "The recording was cut short or stayed quiet.", "Have the argument again, for longer."],
            "There was not enough here to rule on. A few sentences each is the minimum."));

    private static PlayerAssessmentDto NothingToAssess() => new(
        "A1", "Not enough speech to assess.", 0, [],
        0, 0, 0, [], 0, 0, 0, 0, [],
        new EmotionProfileDto(0, 0, 0, 0, 0, 0), [],
        "N/A", [], "Unknown", "Unknown", "Unknown",
        0, 0, 0, 0, "N/A", "N/A",
        ["Speak for longer, so there is something to assess.", "Take a position; hedging gives the judge nothing to grade.", "Answer the question you were actually asked."]);

    /// <summary>
    /// Writes where the reading has got to. The count of attempts rides along on every write and goes up by one each
    /// time a reading starts, which is how a fight that takes the process down with it is recognised on the way
    /// back up instead of being queued again on every restart.
    /// </summary>
    private async Task SaveStatusAsync(IMatchRepository matches, MatchId matchId, AnalysisStatus status, string? json, string? error, CancellationToken ct, bool starting = false)
    {
        var attempts = (await matches.GetAnalysisAsync(matchId, ct))?.Attempts ?? 0;
        await matches.SaveAnalysisAsync(
            new AnalysisRecordDto(matchId, status, json, error, clock.GetUtcNow()) { Attempts = starting ? attempts + 1 : attempts },
            ct);

        // Said out loud as well as written down, so the page that is waiting hears it the moment it happens rather
        // than on its next ask. Only the stage's name goes out: the report is fetched, by its owner, the usual way.
        if (hub is not null)
        {
            try
            {
                await hub.Clients.Group(LiveHub.AnalysisGroup(matchId)).SendAsync(LiveHubContract.AnalysisStatus, status.ToString(), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Nobody listening, or the line is down. The page still asks, just less promptly.
                LogNotAnnounced(logger, matchId.Value, ex);
            }
        }
    }

    [LoggerMessage(EventId = 5008, Level = LogLevel.Debug, Message = "Analysis {MatchId}: the stage change could not be announced")]
    private static partial void LogNotAnnounced(ILogger logger, string matchId, Exception ex);

    /// <summary>The recording as the provider holds it, for exactly as long as the reading takes.</summary>
    private sealed class UploadedRecording(IGeminiFilesClient files, string name, ILogger logger) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await files.DeleteAsync(name, CancellationToken.None);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // It expires by itself in two days; a failed tidy-up is not worth losing a report over.
                LogUploadNotDeleted(logger, name, ex);
            }
        }
    }

    [LoggerMessage(EventId = 5007, Level = LogLevel.Warning, Message = "The uploaded recording {Name} could not be deleted; it expires by itself.")]
    private static partial void LogUploadNotDeleted(ILogger logger, string name, Exception ex);

    private static async Task<MatchDto> MarkAsync(IMatchRepository matches, MatchDto match, SessionStatus status, CancellationToken ct)
    {
        var updated = match with { Status = status };
        await matches.UpsertAsync(updated, ct);
        return updated;
    }

    [LoggerMessage(EventId = 5001, Level = LogLevel.Information, Message = "Analysis {MatchId}: {Words} words from the {Source} transcript. {Note}")]
    private static partial void LogTranscribed(ILogger logger, string matchId, int words, string source, string note);

    [LoggerMessage(EventId = 5002, Level = LogLevel.Information, Message = "Analysis {MatchId}: ready in {Seconds:F1}s")]
    private static partial void LogReady(ILogger logger, string matchId, double seconds);

    [LoggerMessage(EventId = 5003, Level = LogLevel.Error, Message = "Analysis {MatchId} failed")]
    private static partial void LogFailed(ILogger logger, string matchId, Exception ex);

    [LoggerMessage(EventId = 5004, Level = LogLevel.Information, Message = "Analysis {MatchId}: the judge was skipped ({Words} words, {Seconds:F1}s of speech) — too little was said")]
    private static partial void LogTooShort(ILogger logger, string matchId, int words, double seconds);

    [LoggerMessage(EventId = 5006, Level = LogLevel.Information, Message = "Analysis {MatchId}: the fight was deleted while it was being read; nothing was written")]
    private static partial void LogDeletedMeanwhile(ILogger logger, string matchId);

    [LoggerMessage(EventId = 5005, Level = LogLevel.Warning, Message = "Analysis {MatchId}: a stored transcript was unusable, falling back to the transcribe model")]
    private static partial void LogTranscriptUnusable(ILogger logger, string matchId, Exception ex);
}
