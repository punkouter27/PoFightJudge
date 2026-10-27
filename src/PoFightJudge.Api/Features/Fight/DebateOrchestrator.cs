using System.Text;
using System.Text.Json;
using PoFightJudge.Api.Common;
using PoFightJudge.Api.Features.Live;
using PoFightJudge.Api.Features.Records;
using PoFightJudge.Api.Features.Storage;
using PoFightJudge.Shared.Identifiers;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Api.Features.Fight;

/// <summary>
/// Runs one live fight: pumps audio both ways, applies the host's tool calls to the rules, sends the deterministic
/// nudges, records both tracks, and persists everything when the show ends.
/// </summary>
public sealed partial class DebateOrchestrator : IAsyncDisposable
{
    /// <summary>What the Live API takes in and gives back, respectively. Neither is ours to choose.</summary>
    public const int PlayerSampleRate = 16_000;

    public const int HostSampleRate = 24_000;

    public const int MaxFrameBytes = 32_000;

    /// <summary>How far the recording may run ahead of the clock before frames are dropped: jitter, not a file upload.</summary>
    public const double AheadOfClockSeconds = 2.0;

    /// <summary>Length of one microphone frame from the browser. Mirrors the frame size in the capture worklet.</summary>
    public const int FrameMilliseconds = 100;

    private const double SpeechRmsThreshold = 0.02;
    private const int MaxReconnects = 3;
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    private readonly LiveClientFactory _liveFactory;
    private readonly ILiveClientSink _sink;
    private readonly IMatchRepository _matches;
    private readonly IAudioBlobStore _blobs;
    private readonly IAnalysisIntake _analysis;
    private readonly TimeProvider _clock;
    private readonly ILogger<DebateOrchestrator> _logger;
    private readonly DebateOptions _options;
    private readonly WavWriter _playersTrack = new(PlayerSampleRate);
    private readonly WavWriter _hostTrack = new(HostSampleRate);
    private readonly StringBuilder _playerCaption = new();

    /// <summary>
    /// Who was speaking when the sentence in <see cref="_playerCaption"/> began. A sentence arrives in pieces and
    /// the floor can move while it is still arriving — the long-talker nudge exists to make that happen — so the
    /// caption is labelled with whoever started it rather than with whoever holds the floor when it is flushed.
    /// </summary>
    private Speaker? _playerCaptionSpeaker;
    private readonly StringBuilder _hostCaption = new();
    private readonly CaptionTranscript _captions = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _endLock = new(1, 1);
    private readonly long _maxTrackBytes;
    private readonly bool _isFake;
    private IGeminiLiveClient? _live;
    private Task? _eventLoop;
    private int _reconnects;
    private int _silentFrames;
    private int _showBegun;
    private int _clients;
    private DateTimeOffset _lastClientSeen;
    private volatile bool _ended;
    private volatile bool _finished;
    private bool _verdictSpoken;
    private string? _hostVerdictText;

    public DebateOrchestrator(
        string userId,
        MatchId matchId,
        ShowSetup setup,
        LiveClientFactory liveFactory,
        ILiveClientSink sink,
        IMatchRepository matches,
        IAudioBlobStore blobs,
        IAnalysisIntake analysis,
        DebateOptions options,
        TimeProvider clock,
        bool isFake,
        ILogger<DebateOrchestrator> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        UserId = userId;
        Setup = setup;
        _liveFactory = liveFactory;
        _sink = sink;
        _matches = matches;
        _blobs = blobs;
        _analysis = analysis;
        _clock = clock;
        _isFake = isFake;
        _logger = logger;
        _options = options;
        _maxTrackBytes = (long)options.MaxSessionSeconds * PlayerSampleRate * 2;
        _lastClientSeen = clock.GetUtcNow();
        Session = new DebateSession(options, matchId, clock.GetUtcNow(), setup);
    }

    public string UserId { get; }

    /// <summary>The host and the two tags chosen before the microphone went on. The tags are what the record goes on.</summary>
    public ShowSetup Setup { get; }

    public DebateSession Session { get; }

    /// <summary>True once <see cref="EndAsync"/> has started: no more audio and no more ticks are accepted.</summary>
    public bool Ended => _ended;

    /// <summary>True once everything has been persisted and the orchestrator can be disposed.</summary>
    public bool Finished => _finished;

    private DateTimeOffset Now => _clock.GetUtcNow();

    /// <summary>
    /// Whether this frame is worth paying to send. Speech always goes, immediately — the gate only thins a silence
    /// that has already run past its grace, and even then it keeps a trickle so the socket never falls quiet.
    /// </summary>
    internal bool ShouldForward(bool speaking)
    {
        if (speaking || !_options.SilenceGating)
        {
            return true;
        }

        if (_silentFrames <= Math.Max(0, _options.SilenceGraceMilliseconds) / FrameMilliseconds)
        {
            return true;
        }

        return _silentFrames % Math.Max(1, _options.SilenceKeepAliveEvery) == 0;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await _matches.UpsertAsync(ToMatchDto(SessionStatus.Live), ct);
        await ConnectAsync(ct);
        await PushSnapshotAsync(ct);
    }

    /// <summary>Starts the host talking. Called once the browser has joined, so the opening line is not said to an empty room.</summary>
    public async Task BeginShowAsync(CancellationToken ct)
    {
        if (_ended || _live is null || Interlocked.Exchange(ref _showBegun, 1) == 1)
        {
            return;
        }

        await _live.SendTextAsync("SYSTEM: The show is starting now. Greet the room, then ask what they are arguing about and what each of them is called.", ct);
    }

    /// <summary>A browser joined, or re-joined after its own reconnect.</summary>
    public void ClientConnected() => Interlocked.Increment(ref _clients);

    public void ClientDisconnected()
    {
        if (Interlocked.Decrement(ref _clients) == 0)
        {
            _lastClientSeen = Now;
        }
    }

    /// <summary>One frame of 16 kHz PCM from the browser. A malformed or over-budget frame is dropped, not forwarded.</summary>
    public async Task OnAudioInAsync(byte[] pcm16k, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pcm16k);

        var recorded = _playersTrack.Bytes;
        if (_ended || _live is null || pcm16k.Length == 0 || pcm16k.Length % 2 != 0 || pcm16k.Length > MaxFrameBytes || recorded >= _maxTrackBytes)
        {
            return;
        }

        // Keep the players' track on the wall clock, so word offsets line up with the turn timeline.
        var elapsed = Now - Session.StartedAt;

        // A microphone cannot get ahead of the clock. Something that does is pushing a file, and every second of it
        // is a second the model is paid to listen to.
        if (recorded / 2.0 / PlayerSampleRate > elapsed.TotalSeconds + AheadOfClockSeconds)
        {
            return;
        }
        if (recorded / 2.0 / PlayerSampleRate < elapsed.TotalSeconds - 0.3)
        {
            _playersTrack.PadTo(elapsed - TimeSpan.FromMilliseconds(FrameMilliseconds));
        }

        _playersTrack.Append(pcm16k);
        var speaking = WavWriter.Rms(pcm16k) >= SpeechRmsThreshold;
        if (speaking)
        {
            Session.NoteSpeech(Now);
            _silentFrames = 0;
        }
        else
        {
            _silentFrames++;
        }

        if (!ShouldForward(speaking))
        {
            return;
        }

        try
        {
            await _live.SendAudioAsync(pcm16k, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSendFailed(_logger, ex);
        }
    }

    /// <summary>Called by the registry's timer, about once a second.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        if (_ended || _live is null)
        {
            return;
        }

        var now = Now;
        if (EndReason(now) is { } reason)
        {
            await EndAsync(reason, ct);
            return;
        }

        foreach (var nudge in Session.Tick(now))
        {
            LogNudge(_logger, Session.MatchId.Value, nudge.Kind);
            try
            {
                await _live.SendTextAsync(nudge.Message, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The session has already ticked this one off. Put it back, or a nudge lost to a dropped line —
                // "time is up" among them — is never sent again.
                Session.Rearm(nudge.Kind);
                LogSendFailed(_logger, ex);
                continue;
            }

            if (nudge.Kind == NudgeKind.LongTalker)
            {
                Session.NoteInterrupt(now);
            }
        }

        await PushSnapshotAsync(ct);
    }

    /// <summary>Ends the show — somebody pressed stop, the host finished, or the line is gone for good — and persists everything.</summary>
    public async Task EndAsync(string reason, CancellationToken ct)
    {
        await _endLock.WaitAsync(ct);
        try
        {
            if (_ended)
            {
                return;
            }

            _ended = true;
            LogEnding(_logger, Session.MatchId.Value, reason);
            Session.Complete(Now);

            // Deliberately not the caller's token: this may be running inside the event loop whose token is cancelled
            // below, and the recording must be saved regardless. Closing is bounded and never throws.
            if (_live is not null)
            {
                await _live.CloseAsync(CancellationToken.None);
            }

            try
            {
                await PersistAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogPersistFailed(_logger, ex);
                await _sink.ErrorAsync("The recording could not be saved.", CancellationToken.None);
            }

            await _sink.EndedAsync(Session.MatchId, CancellationToken.None);
            await _cts.CancelAsync();
        }
        finally
        {
            _finished = true;
            _endLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_ended)
        {
            await _cts.CancelAsync();
        }

        if (_eventLoop is not null)
        {
            try
            {
                await _eventLoop.WaitAsync(CloseTimeout);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                // The loop is cancelled or stuck on a dead socket; disposing the client below unblocks it.
            }
        }

        if (_live is not null)
        {
            await _live.DisposeAsync();
        }

        _cts.Dispose();
        _endLock.Dispose();
        _playersTrack.Dispose();
        _hostTrack.Dispose();
    }

    /// <summary>The deadlines that end a show on their own, in priority order.</summary>
    private string? EndReason(DateTimeOffset now)
    {
        if (now - Session.StartedAt >= TimeSpan.FromSeconds(_options.MaxSessionSeconds))
        {
            return "session hard cap";
        }

        if (_clients == 0 && now - _lastClientSeen >= TimeSpan.FromSeconds(_options.ClientGraceSeconds))
        {
            return "no browser connected";
        }

        if (Session.Phase == SessionPhase.Verdict
            && Session.VerdictAt is { } verdictAt
            && now - verdictAt >= TimeSpan.FromSeconds(_options.VerdictGraceSeconds))
        {
            return "verdict grace elapsed";
        }

        return null;
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        if (_ended)
        {
            return;
        }

        var previous = _live;
        var next = _liveFactory();
        var config = new LiveSessionConfig(
            HostPersona.SystemInstruction(_options, Setup),
            previous?.ResumptionHandle,
            HostPersona.VoiceFor(Setup.Persona),
            ToolDeclarations.FunctionDeclarations());

        // Swapped in only once it is connected: frames and nudges that arrive while it is still dialling go to the
        // old client and fail quietly, rather than to a new one that throws "not connected" for every frame.
        try
        {
            await next.ConnectAsync(config, ct);
        }
        catch
        {
            await next.DisposeAsync();
            throw;
        }

        _live = next;
        if (previous is not null)
        {
            await previous.DisposeAsync();
        }

        _eventLoop = Task.Run(() => EventLoopAsync(next, _cts.Token), CancellationToken.None);
    }

    private async Task EventLoopAsync(IGeminiLiveClient live, CancellationToken ct)
    {
        try
        {
            await foreach (var received in live.Events.ReadAllAsync(ct))
            {
                await HandleAsync(received, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // The fight is over.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogEventLoopFailed(_logger, ex);
            if (!_ended)
            {
                await _sink.ErrorAsync("The host lost its voice.", CancellationToken.None);
            }
        }
    }

    private async Task HandleAsync(LiveServerEvent received, CancellationToken ct)
    {
        switch (received)
        {
            case LiveServerEvent.AudioOut audio:
                _hostTrack.PadTo(Now - Session.StartedAt - TimeSpan.FromMilliseconds(50));
                _hostTrack.Append(audio.Pcm24k.Span);
                Session.NoteSpeech(Now);
                if (Session.Phase == SessionPhase.Verdict)
                {
                    _verdictSpoken = true;
                }

                await _sink.AudioOutAsync(audio.Pcm24k, ct);
                break;

            case LiveServerEvent.Interrupted:
                await _sink.AudioClearAsync(ct);
                break;

            case LiveServerEvent.InputTranscript heard:
                var speaking = Session.CurrentSpeaker?.ToSpeaker() ?? Speaker.Player1;
                if (_playerCaption.Length > 0 && _playerCaptionSpeaker is { } started && started != speaking)
                {
                    // The floor moved mid-sentence. Finish what the last one was saying under their own name
                    // before the new one starts, or their words end up printed under somebody else's.
                    await _sink.CaptionAsync(new CaptionDto(started, _playerCaption.ToString(), true), ct);
                    _playerCaption.Clear();
                }

                _playerCaptionSpeaker = speaking;
                _playerCaption.Append(heard.Text);

                // The same text the room reads is the transcript the analysis reads: the show is transcribed once.
                // The room needs a name to print it under; the transcript is told the truth, which may be "nobody".
                _captions.Add(heard.Text, Session.CurrentSpeaker?.ToSpeaker(), CaptionTranscript.Elapsed(Now, Session.StartedAt));
                await _sink.CaptionAsync(new CaptionDto(speaking, _playerCaption.ToString(), false), ct);
                break;

            case LiveServerEvent.OutputTranscript said:
                _hostCaption.Append(said.Text);
                await _sink.CaptionAsync(new CaptionDto(Speaker.Host, _hostCaption.ToString(), false), ct);
                break;

            case LiveServerEvent.TurnComplete:
                await FlushCaptionsAsync(ct);

                // Calling the ruling and speaking it are separate turns; the show ends only once it has been said.
                if (Session.Phase == SessionPhase.Verdict && _verdictSpoken)
                {
                    await EndAsync("verdict delivered", ct);
                }

                break;

            case LiveServerEvent.ToolCall call:
                await HandleToolCallAsync(call, ct);
                break;

            case LiveServerEvent.GoAway goAway:
                LogGoAway(_logger, goAway.TimeLeft);
                break;

            case LiveServerEvent.Closed closed:
                await HandleClosedAsync(closed.Reason, ct);
                break;

            default:
                break;
        }
    }

    private async Task FlushCaptionsAsync(CancellationToken ct)
    {
        if (_playerCaption.Length > 0)
        {
            await _sink.CaptionAsync(new CaptionDto(_playerCaptionSpeaker ?? Speaker.Player1, _playerCaption.ToString(), true), ct);
            _playerCaption.Clear();
            _playerCaptionSpeaker = null;
        }

        if (_hostCaption.Length > 0)
        {
            var text = _hostCaption.ToString();
            if (Session.Phase == SessionPhase.Verdict)
            {
                _hostVerdictText = DebateSession.Clean(text, DebateSession.MaxVerdictText, string.Empty);
            }

            await _sink.CaptionAsync(new CaptionDto(Speaker.Host, text, true), ct);
            _hostCaption.Clear();
        }
    }

    private async Task HandleToolCallAsync(LiveServerEvent.ToolCall call, CancellationToken ct)
    {
        var now = Now;
        var args = call.Args;

        string Arg(string name) =>
            args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) ? value.ToString() : string.Empty;

        // reasons is declared as an array; a model that ignores the schema sends one pipe-joined string instead.
        string[] ArgList(string name)
        {
            if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value))
            {
                return [];
            }

            return value.ValueKind == JsonValueKind.Array
                ? [.. value.EnumerateArray().Select(e => e.ToString().Trim()).Where(s => s.Length > 0)]
                : [.. value.ToString().Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        }

        var result = call.Name switch
        {
            ToolDeclarations.SetPlayers => Session.SetPlayers(Arg("topic"), Arg("player1"), Arg("player2"), now),
            ToolDeclarations.StartTurn => Session.TryResolvePlayer(Arg("player"), out var speaking)
                ? Session.StartTurn(speaking, now)
                : Outcome.Fail("Unknown player; use 'player1' or 'player2'."),
            ToolDeclarations.EndDebate => Session.EndDebate(now),
            ToolDeclarations.AskProbe => Session.TryResolvePlayer(Arg("player"), out var questioned)
                ? Session.AskProbe(questioned, Arg("question"), now)
                : Outcome.Fail("Unknown player; use 'player1' or 'player2'."),
            ToolDeclarations.FlagFallacy => Session.TryResolvePlayer(Arg("player"), out var flagged)
                ? Session.FlagFallacy(flagged, Arg("fallacy"), Arg("quote"), now)
                : Outcome.Fail("Unknown player; use 'player1' or 'player2'."),
            ToolDeclarations.DeliverVerdict => DeliverVerdict(Arg("winner_logic"), Arg("winner_correct"), Arg("overall"), ArgList("reasons"), now),
            _ => Outcome.Fail($"Unknown tool {call.Name}."),
        };

        LogToolCall(_logger, Session.MatchId.Value, call.Name, result.IsSuccess ? "ok" : result.Error!);
        await _live!.SendToolResponseAsync(
            call.Id,
            call.Name,
            result.IsSuccess ? new { ok = true, phase = Session.Phase.ToString() } : new { ok = false, error = result.Error },
            ct);
        await PushSnapshotAsync(ct);
    }

    private Outcome DeliverVerdict(string logic, string correct, string overall, IReadOnlyList<string> reasons, DateTimeOffset now)
    {
        if (!PlayerIdExtensions.TryParse(overall, out var overallId))
        {
            return Outcome.Fail("overall must be 'player1' or 'player2'.");
        }

        // The overall winner is the one call that must be readable; the other two fall back to it rather than failing
        // a ruling the host has already decided.
        var logicId = PlayerIdExtensions.TryParse(logic, out var byLogic) ? byLogic : overallId;
        var correctId = PlayerIdExtensions.TryParse(correct, out var byFacts) ? byFacts : overallId;
        return Session.DeliverVerdict(new VerdictCall(logicId, correctId, overallId, reasons), now);
    }

    private async Task HandleClosedAsync(string reason, CancellationToken ct)
    {
        if (_ended || Session.Phase is SessionPhase.Done)
        {
            return;
        }

        if (_reconnects >= MaxReconnects)
        {
            await _sink.ErrorAsync("Lost the connection to the host; wrapping up.", ct);
            await EndAsync($"connection lost: {reason}", ct);
            return;
        }

        _reconnects++;
        LogReconnecting(_logger, Session.MatchId.Value, _reconnects, reason);
        try
        {
            await ConnectAsync(ct);
            if (_live is not null && !_ended)
            {
                await _live.SendTextAsync("SYSTEM: The line dropped for a moment. Pick up exactly where you were, briefly.", ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogEventLoopFailed(_logger, ex);
            await EndAsync("reconnect failed", ct);
        }
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        var id = Session.MatchId;
        string? blobName = null;
        try
        {
            // Both tracks stream straight from their buffers, and the caption transcript goes up with them, so the
            // pipeline finds it already there rather than paying to transcribe the show a second time.
            var players = _playersTrack.Bytes > 0 ? UploadTrackAsync(id, IAudioBlobStore.PlayersTrack, _playersTrack, ct) : Task.FromResult<string?>(null);
            var host = _hostTrack.Bytes > 0 ? UploadTrackAsync(id, IAudioBlobStore.HostTrack, _hostTrack, ct) : Task.FromResult<string?>(null);
            var extras = Task.WhenAll(host, UploadCaptionTranscriptAsync(id, ct));
            try
            {
                blobName = await players;
            }
            finally
            {
                // The host's track and the captions are extras: losing one of them costs a replay or a cheaper
                // transcript, never the fight.
                try
                {
                    await extras;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogPersistFailed(_logger, ex);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPersistFailed(_logger, ex);
        }

        var match = ToMatchDto(blobName is null ? SessionStatus.Failed : SessionStatus.Analyzing) with { EndedAt = Now };
        await _matches.UpsertAsync(match with { AudioBlobName = blobName }, ct);
        if (Session.Turns.Count > 0)
        {
            await _matches.SaveTurnsAsync(id, Session.Turns, ct);
        }

        if (blobName is not null)
        {
            // Recorded first, so a poll that lands before the pipeline picks the job up sees "queued" rather than nothing.
            await _matches.SaveAnalysisAsync(new AnalysisRecordDto(id, AnalysisStatus.Queued, null, null, Now), ct);
            await _analysis.SubmitAsync(UserId, id, ct);
        }
    }

    /// <summary>
    /// Stores one track. Opus by default — a few minutes of speech is a tenth the size — with the WAV kept when
    /// the option says so. The name carries the format, which is how everything reading it back knows.
    /// </summary>
    private async Task<string?> UploadTrackAsync(MatchId id, Func<MatchId, string, string> name, WavWriter track, CancellationToken ct)
    {
        if (_options.StoreOpus && OpusAudio.IsSupportedRate(track.SampleRate))
        {
            var opus = OpusAudio.Encode(track.Pcm.Span, track.SampleRate);
            await using var encoded = new MemoryStream(opus, writable: false);
            return await _blobs.UploadAsync(name(id, OpusAudio.Extension), encoded, OpusAudio.ContentType, ct);
        }

        await using var stream = track.OpenWavStream();
        return await _blobs.UploadAsync(name(id, "wav"), stream, "audio/wav", ct);
    }

    /// <summary>
    /// Stores the transcript assembled from the host's captions. Best-effort on purpose: if it is missing or
    /// unusable the pipeline falls back to the transcribe model, which is only ever a cost regression.
    /// </summary>
    private async Task<string?> UploadCaptionTranscriptAsync(MatchId matchId, CancellationToken ct)
    {
        if (_captions.Build() is not { } transcript)
        {
            return null;
        }

        using var json = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(transcript, JsonSerializerOptions.Web));
        LogCaptionTranscript(_logger, matchId.Value, transcript.Words.Count);
        return await _blobs.UploadAsync(IAudioBlobStore.LiveTranscript(matchId), json, "application/json", ct);
    }

    /// <summary>
    /// A fight is always two people on one microphone, so both sides are human and both are keyed by the tag that
    /// was entered before the show — never by whatever the host ended up calling them.
    /// </summary>
    private MatchDto ToMatchDto(SessionStatus status) => new(
        Session.MatchId,
        UserId,
        MatchMode.Fight,
        Session.StartedAt,
        null,
        Session.Topic,
        MatchSide.Human(Setup.HasPlayers ? Setup.Player1 : Session.Player1Name, Session.Player1Name),
        MatchSide.Human(Setup.HasPlayers ? Setup.Player2 : Session.Player2Name, Session.Player2Name),
        Session.Phase,
        status,
        WinnerTag(),
        _hostVerdictText,
        _isFake)
    {
        Persona = Setup.Persona.ToString(),
        Side1Stance = Setup.Player1Stance,
    };

    /// <summary>The winning tag, or empty while there is no ruling yet.</summary>
    private string WinnerTag() => Session.Verdict is not { } verdict
        ? string.Empty
        : verdict.Overall == PlayerId.Player1
            ? (Setup.HasPlayers ? Setup.Player1 : Session.Player1Name)
            : (Setup.HasPlayers ? Setup.Player2 : Session.Player2Name);

    private Task PushSnapshotAsync(CancellationToken ct) => _sink.SnapshotAsync(Session.Snapshot(Now), ct);

    [LoggerMessage(EventId = 4001, Level = LogLevel.Information, Message = "Fight {MatchId}: nudge {Kind}")]
    private static partial void LogNudge(ILogger logger, string matchId, NudgeKind kind);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Information, Message = "Fight {MatchId}: tool {Tool} -> {Result}")]
    private static partial void LogToolCall(ILogger logger, string matchId, string tool, string result);

    [LoggerMessage(EventId = 4003, Level = LogLevel.Information, Message = "Fight {MatchId}: ending ({Reason})")]
    private static partial void LogEnding(ILogger logger, string matchId, string reason);

    [LoggerMessage(EventId = 4004, Level = LogLevel.Warning, Message = "Fight {MatchId}: reconnecting ({Attempt}) after: {Reason}")]
    private static partial void LogReconnecting(ILogger logger, string matchId, int attempt, string reason);

    [LoggerMessage(EventId = 4005, Level = LogLevel.Warning, Message = "Live event loop failed")]
    private static partial void LogEventLoopFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4006, Level = LogLevel.Warning, Message = "Failed to send audio to Gemini")]
    private static partial void LogSendFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4007, Level = LogLevel.Error, Message = "Failed to persist the fight")]
    private static partial void LogPersistFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4008, Level = LogLevel.Information, Message = "GoAway received, {TimeLeft} left")]
    private static partial void LogGoAway(ILogger logger, TimeSpan timeLeft);

    [LoggerMessage(EventId = 4009, Level = LogLevel.Information, Message = "Fight {MatchId}: caption transcript stored ({Words} words)")]
    private static partial void LogCaptionTranscript(ILogger logger, string matchId, int words);
}
