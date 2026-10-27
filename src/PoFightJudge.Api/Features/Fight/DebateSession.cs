using System.Diagnostics.CodeAnalysis;
using PoFightJudge.Api.Common;
using PoFightJudge.Shared.Identifiers;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Api.Features.Fight;

[SuppressMessage(
    "Design",
    "CA1008:Enums should have zero value",
    Justification = "There is no third person in the room, so a zero member would name a player who does not exist. " +
        "TryParse reports a value it could not read through its return value rather than by handing back a default.")]
public enum PlayerId
{
    Player1 = 1,
    Player2 = 2,
}

public enum NudgeKind
{
    /// <summary>Somebody has held the floor past the long-talker limit.</summary>
    LongTalker,

    /// <summary>Nobody has spoken for the silence threshold; the host should say something.</summary>
    Silence,

    /// <summary>A second silence window elapsed; the host should move on to the next step.</summary>
    SilenceAdvance,

    /// <summary>The questioning window is open; the host may cut in.</summary>
    ProbeWindowOpen,

    /// <summary>The argument has run its full time and must stop now.</summary>
    DebateCap,

    /// <summary>The questioning ran long; the host must rule now.</summary>
    ProbeCap,

    /// <summary>The two of them have been named and are talking, but the argument was never formally started.</summary>
    SetupCap,
}

/// <summary>A deterministic instruction for the host, produced by the state machine's clock.</summary>
public sealed record DebateNudge(NudgeKind Kind, string Message);

/// <summary>The ruling as the host called it, before it is turned into a stored result.</summary>
public sealed record VerdictCall(PlayerId WinnerLogic, PlayerId WinnerCorrect, PlayerId Overall, IReadOnlyList<string> Reasons);

public static class PlayerIdExtensions
{
    public static Speaker ToSpeaker(this PlayerId id) => id == PlayerId.Player1 ? Speaker.Player1 : Speaker.Player2;

    public static PlayerId Other(this PlayerId id) => id == PlayerId.Player1 ? PlayerId.Player2 : PlayerId.Player1;

    /// <summary>The other side. The host has no opponent and maps to Player1 for callers that need a value.</summary>
    public static Speaker Other(this Speaker speaker) => speaker == Speaker.Player1 ? Speaker.Player2 : Speaker.Player1;

    /// <summary>Reads the values a model might use for a player: "player1", "Player 2", "p1", "two".</summary>
    public static bool TryParse(string? value, out PlayerId id)
    {
        var cleaned = (value ?? string.Empty).Trim().ToLowerInvariant().Replace(" ", string.Empty, StringComparison.Ordinal);
        switch (cleaned)
        {
            case "player1" or "p1" or "1" or "one":
                id = PlayerId.Player1;
                return true;
            case "player2" or "p2" or "2" or "two":
                id = PlayerId.Player2;
                return true;
            default:
                id = default;
                return false;
        }
    }
}

/// <summary>
/// The rules of one fight: Intro → Setup → Debate → Probe → Verdict → Done. Callers feed it tool calls and speech
/// activity with explicit timestamps and <see cref="Tick"/> hands back nudges. No I/O and no timers of its own, so
/// a whole show is reproducible from a list of moments.
/// </summary>
public sealed class DebateSession(DebateOptions options, MatchId matchId, DateTimeOffset startedAt, ShowSetup? setup = null)
{
    public const int MaxShortText = 200;
    public const int MaxQuestionText = 1_000;
    public const int MaxVerdictText = 8_000;

    /// <summary>A host that flags every sentence has stopped hosting; past this the calls are refused.</summary>
    public const int MaxFlags = 12;

    private const int MaxFallacyName = 40;

    private readonly List<TurnDto> _turns = [];
    private readonly List<LiveFlagDto> _flags = [];
    private readonly HashSet<NudgeKind> _nudged = [];
    private DateTimeOffset _lastSpeech = startedAt;
    private DateTimeOffset _turnStartedAt = startedAt;
    private DateTimeOffset? _debateStartedAt;

    /// <summary>When the arguing stopped. The clock the room sees has to stop with it.</summary>
    private DateTimeOffset? _debateEndedAt;

    /// <summary>When the two of them were named, so a show that never gets going can be nudged out of Setup.</summary>
    private DateTimeOffset? _setupStartedAt;
    private DateTimeOffset? _probeStartedAt;
    private int? _openTurnIndex;
    private int _silenceLevel;

    public MatchId MatchId { get; } = matchId;

    public DateTimeOffset StartedAt { get; } = startedAt;

    public SessionPhase Phase { get; private set; } = SessionPhase.Intro;

    public string Topic { get; private set; } = setup?.Topic ?? string.Empty;

    public string Player1Name { get; private set; } = setup?.HasPlayers == true ? setup.Player1 : "Player 1";

    public string Player2Name { get; private set; } = setup?.HasPlayers == true ? setup.Player2 : "Player 2";

    /// <summary>Who is hosting. Carried on every snapshot so the room can theme itself.</summary>
    public HostPersonaId Persona { get; } = setup?.Persona ?? HostPersonaId.Referee;

    public PlayerId? CurrentSpeaker { get; private set; }

    public VerdictCall? Verdict { get; private set; }

    public DateTimeOffset? VerdictAt { get; private set; }

    /// <summary>A copy: the list itself is still being written to while a fight runs.</summary>
    public IReadOnlyList<TurnDto> Turns => Locked(() => (IReadOnlyList<TurnDto>)[.. _turns]);

    /// <summary>
    /// How long they argued for. It stops when the argument does — the room is shown this as a countdown, and a
    /// timer that keeps running through the questions and the ruling counts down to nothing in front of everybody.
    /// </summary>
    public TimeSpan DebateElapsed(DateTimeOffset now) =>
        _debateStartedAt is null ? TimeSpan.Zero : (_debateEndedAt ?? now) - _debateStartedAt.Value;

    public TimeSpan DebateRemaining(DateTimeOffset now)
    {
        var remaining = TimeSpan.FromSeconds(options.MaxDebateSeconds) - DebateElapsed(now);
        return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }

    public string NameOf(PlayerId id) => id == PlayerId.Player1 ? Player1Name : Player2Name;

    public Outcome SetPlayers(string topic, string player1, string player2, DateTimeOffset now) => Locked(() => SetPlayersCore(topic, player1, player2, now));

    private Outcome SetPlayersCore(string topic, string player1, string player2, DateTimeOffset now)
    {
        if (Phase is not (SessionPhase.Intro or SessionPhase.Setup))
        {
            return Outcome.Fail($"Cannot set players during {Phase}.");
        }

        // The tags were entered before the show and key the fighter tables, so the host never gets to rename anyone,
        // and a topic they agreed before the microphone went on is theirs rather than the host's to rewrite. The host
        // is told to restate an agreed topic, and a restatement that came back slightly different would otherwise
        // replace what they actually typed.
        if (setup?.HasTopic != true)
        {
            Topic = Clean(topic, MaxShortText, Topic.Length > 0 ? Topic : "an open topic");
        }

        if (setup?.HasPlayers != true)
        {
            Player1Name = Clean(player1, MaxShortText, "Player 1");
            Player2Name = Clean(player2, MaxShortText, "Player 2");
        }

        Phase = SessionPhase.Setup;
        _setupStartedAt ??= now;
        NoteSpeechCore(now);
        return Outcome.Ok;
    }

    /// <summary>
    /// Which of the two a tool call means. The declarations ask for "player1" or "player2", but a host that has
    /// been calling somebody AL all night reaches for AL — and a refused tool call is a turn of the show wasted on
    /// an error message, so their own tags count as well.
    /// </summary>
    public bool TryResolvePlayer(string? value, out PlayerId id)
    {
        if (PlayerIdExtensions.TryParse(value, out id))
        {
            return true;
        }

        var cleaned = (value ?? string.Empty).Trim();
        if (cleaned.Length > 0 && !string.Equals(Player1Name, Player2Name, StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(cleaned, Player1Name, StringComparison.OrdinalIgnoreCase))
            {
                id = PlayerId.Player1;
                return true;
            }

            if (string.Equals(cleaned, Player2Name, StringComparison.OrdinalIgnoreCase))
            {
                id = PlayerId.Player2;
                return true;
            }
        }

        id = default;
        return false;
    }

    /// <summary>
    /// Moves a show out of Intro when the host has plainly moved past it. Both fighters were named in the request
    /// that started the fight, so Intro is not waiting to learn who they are — it is waiting for the host to say
    /// so, and a host that has gone on to ask a question has said everything it is going to say. Without this the
    /// session sits in Intro refusing every call, which is what a real fight did.
    /// </summary>
    private void MoveOnFromIntro(DateTimeOffset now)
    {
        if (Phase is SessionPhase.Intro && setup?.HasPlayers == true)
        {
            Phase = SessionPhase.Setup;
            _setupStartedAt ??= now;
            NoteSpeechCore(now);
        }
    }

    public Outcome StartTurn(PlayerId player, DateTimeOffset now) => Locked(() => StartTurnCore(player, now));

    private Outcome StartTurnCore(PlayerId player, DateTimeOffset now)
    {
        MoveOnFromIntro(now);
        if (Phase is SessionPhase.Setup)
        {
            Phase = SessionPhase.Debate;
            _debateStartedAt = now;
        }

        if (Phase is not SessionPhase.Debate)
        {
            return Outcome.Fail($"Cannot start a turn during {Phase}.");
        }

        OpenTurn(player, TurnKind.Talk, string.Empty, now);
        _nudged.Remove(NudgeKind.LongTalker);
        return Outcome.Ok;
    }

    public Outcome EndDebate(DateTimeOffset now) => Locked(() => EndDebateCore(now));

    private Outcome EndDebateCore(DateTimeOffset now)
    {
        MoveOnFromIntro(now);

        // A host that never called start_turn is no reason for a show to be unendable. It happens: the two of them
        // simply start arguing, the host follows the argument rather than the script, and by the time it wants to
        // move on the session is still formally in Setup. Refusing leaves the fight stuck there for as long as the
        // socket lives — which is what a real one did, for eight minutes, while the host asked eleven times.
        if (Phase is SessionPhase.Setup)
        {
            // Straight to Debate rather than through StartTurn: opening a turn here would put an empty one on the
            // record, attributed to somebody who never got the floor, and then close it in the next breath. The
            // They have been arguing since some point this session, so the start is the session rather than this
            // instant: recording it as "now" would report an eight-minute argument as having lasted no time.
            Phase = SessionPhase.Debate;
            _debateStartedAt ??= StartedAt;
        }

        if (Phase is not SessionPhase.Debate)
        {
            return Outcome.Fail($"Cannot end the argument during {Phase}.");
        }

        _debateEndedAt = now;
        CloseOpenTurn(now);
        CurrentSpeaker = null;
        Phase = SessionPhase.Probe;
        _probeStartedAt = now;
        NoteSpeechCore(now);
        return Outcome.Ok;
    }

    public Outcome AskProbe(PlayerId player, string question, DateTimeOffset now) => Locked(() => AskProbeCore(player, question, now));

    private Outcome AskProbeCore(PlayerId player, string question, DateTimeOffset now)
    {
        MoveOnFromIntro(now);

        // A host that skips straight to questions must not be left stuck in the wrong phase — from Setup as well
        // as from the debate, because a question is just as clear a statement that the argument is over.
        if (Phase is SessionPhase.Setup or SessionPhase.Debate)
        {
            EndDebateCore(now);
        }

        if (Phase is not SessionPhase.Probe)
        {
            return Outcome.Fail($"Cannot ask a question during {Phase}.");
        }

        OpenTurn(player, TurnKind.Probe, Clean(question, MaxQuestionText, string.Empty), now);
        return Outcome.Ok;
    }

    /// <summary>The host calls a fallacy as it happens. Only while they argue or answer, and only so many times.</summary>
    public Outcome FlagFallacy(PlayerId player, string fallacy, string quote, DateTimeOffset now) => Locked(() =>
    {
        MoveOnFromIntro(now);
        if (Phase is not (SessionPhase.Debate or SessionPhase.Probe))
        {
            return Outcome.Fail($"Cannot flag a fallacy during {Phase}.");
        }

        if (_flags.Count >= MaxFlags)
        {
            return Outcome.Fail("Enough fallacies called; let them argue.");
        }

        _flags.Add(new LiveFlagDto(player.ToSpeaker(), Clean(fallacy, MaxFallacyName, "Fallacy"), Clean(quote, MaxShortText, string.Empty)));
        return Outcome.Ok;
    });

    public Outcome DeliverVerdict(VerdictCall verdict, DateTimeOffset now) => Locked(() => DeliverVerdictCore(verdict, now));

    private Outcome DeliverVerdictCore(VerdictCall verdict, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        if (Phase is SessionPhase.Verdict or SessionPhase.Done)
        {
            return Outcome.Fail("The ruling has already been given.");
        }

        CloseOpenTurn(now);
        CurrentSpeaker = null;
        Verdict = verdict;
        VerdictAt ??= now;
        Phase = SessionPhase.Verdict;
        _turns.Add(new TurnDto(MatchId, _turns.Count, Speaker.Host, TurnKind.Verdict, Clean(string.Join(" | ", verdict.Reasons), MaxVerdictText, string.Empty))
        {
            StartSeconds = Seconds(now),
        });
        return Outcome.Ok;
    }

    /// <summary>Called once the host has finished speaking the ruling, or the show is cut short.</summary>
    public void Complete(DateTimeOffset now) => Locked(() => CompleteCore(now));

    private void CompleteCore(DateTimeOffset now)
    {
        CloseOpenTurn(now);
        CurrentSpeaker = null;
        Phase = SessionPhase.Done;
    }

    /// <summary>Any audio activity resets the silence clock. The long-talker clock is per turn and is not affected.</summary>
    public void NoteSpeech(DateTimeOffset now) => Locked(() => NoteSpeechCore(now));

    private void NoteSpeechCore(DateTimeOffset now)
    {
        _lastSpeech = now;
        _silenceLevel = 0;
        _nudged.Remove(NudgeKind.Silence);
        _nudged.Remove(NudgeKind.SilenceAdvance);
    }

    /// <summary>A nudge that could not be delivered: it is owed again on the next tick.</summary>
    public void Rearm(NudgeKind kind) => Locked(() =>
    {
        _nudged.Remove(kind);
        _silenceLevel = kind switch
        {
            NudgeKind.Silence => 0,
            NudgeKind.SilenceAdvance => 1,
            _ => _silenceLevel,
        };
    });

    /// <summary>Records a host interruption. The turn is attributed to whoever was cut off.</summary>
    public void NoteInterrupt(DateTimeOffset now) => Locked(() => NoteInterruptCore(now));

    private void NoteInterruptCore(DateTimeOffset now)
    {
        if (CurrentSpeaker is { } speaker)
        {
            _turns.Add(new TurnDto(MatchId, _turns.Count, speaker.ToSpeaker(), TurnKind.Interrupt, $"Host interrupted {NameOf(speaker)}")
            {
                StartSeconds = Seconds(now),
                EndSeconds = Seconds(now),
            });
        }

        _nudged.Add(NudgeKind.LongTalker);
    }

    /// <summary>Reads the clocks. Each nudge fires once per condition, and only fires again once that condition has reset.</summary>
    public IReadOnlyList<DebateNudge> Tick(DateTimeOffset now) => Locked<IReadOnlyList<DebateNudge>>(() => TickCore(now));

    private List<DebateNudge> TickCore(DateTimeOffset now)
    {
        var nudges = new List<DebateNudge>();
        if (Phase is SessionPhase.Done or SessionPhase.Verdict)
        {
            return nudges;
        }

        var silence = now - _lastSpeech;
        if (_silenceLevel == 0 && silence >= TimeSpan.FromSeconds(options.SilenceSeconds) && !_nudged.Contains(NudgeKind.Silence))
        {
            _silenceLevel = 1;
            _nudged.Add(NudgeKind.Silence);
            nudges.Add(new DebateNudge(NudgeKind.Silence, SilenceMessage()));
        }
        else if (_silenceLevel == 1 && silence >= TimeSpan.FromSeconds(options.SilenceSeconds * 2) && !_nudged.Contains(NudgeKind.SilenceAdvance))
        {
            _silenceLevel = 2;
            _nudged.Add(NudgeKind.SilenceAdvance);
            nudges.Add(new DebateNudge(NudgeKind.SilenceAdvance, "SYSTEM: Still silence. Move on to the next step now without waiting."));
        }

        if (Phase is SessionPhase.Debate)
        {
            AddDebateNudges(now, nudges);
        }
        else if (Phase is SessionPhase.Setup && _setupStartedAt is { } setupStart
            && !_nudged.Contains(NudgeKind.SetupCap) && now - setupStart >= TimeSpan.FromSeconds(options.MaxSetupSeconds))
        {
            // The silence nudges above never fire while they are arguing, and none of the debate clocks are running
            // yet — so without this the show can sit here talking until the session's own cap kills it.
            _nudged.Add(NudgeKind.SetupCap);
            nudges.Add(new DebateNudge(
                NudgeKind.SetupCap,
                $"SYSTEM: {Player1Name} and {Player2Name} are already arguing. Start the argument properly now — hand the floor to one of them (call start_turn)."));
        }
        else if (Phase is SessionPhase.Probe && _probeStartedAt is { } probeStart
            && !_nudged.Contains(NudgeKind.ProbeCap) && now - probeStart >= TimeSpan.FromSeconds(options.MaxProbeSeconds))
        {
            _nudged.Add(NudgeKind.ProbeCap);
            nudges.Add(new DebateNudge(NudgeKind.ProbeCap, "SYSTEM: Question time is over. Give the ruling now (call deliver_verdict, then announce it)."));
        }

        return nudges;
    }

    public DebateSnapshotDto Snapshot(DateTimeOffset now) => Locked(() => new DebateSnapshotDto(
        MatchId,
        Phase,
        Topic,
        Player1Name,
        Player2Name,
        CurrentSpeaker?.ToSpeaker(),
        (int)DebateElapsed(now).TotalSeconds,
        (int)DebateRemaining(now).TotalSeconds,
        Verdict is null ? null : new VerdictDto(Verdict.WinnerLogic.ToSpeaker(), Verdict.WinnerCorrect.ToSpeaker(), Verdict.Overall.ToSpeaker(), Verdict.Reasons),
        Persona,
        (int)(now - StartedAt).TotalSeconds,
        setup?.Player1Stance,
        _flags.Count == 0 ? null : [.. _flags]));

    /// <summary>
    /// Every entry point takes this. The hub thread reports speech, the registry's ticker reads the clocks, the
    /// host's event loop applies tool calls and an HTTP request can ask for a snapshot — four callers, one list of
    /// turns. The lock is re-entrant, so one rule may apply another.
    /// </summary>
    private readonly Lock _gate = new();

    private T Locked<T>(Func<T> rule)
    {
        lock (_gate)
        {
            return rule();
        }
    }

    private void Locked(Action rule)
    {
        lock (_gate)
        {
            rule();
        }
    }

    /// <summary>Trims model-supplied text and caps its length, so a stored property stays inside what Table Storage takes.</summary>
    public static string Clean(string? value, int max, string fallback)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return fallback;
        }

        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    private void AddDebateNudges(DateTimeOffset now, List<DebateNudge> nudges)
    {
        var elapsed = DebateElapsed(now);

        if (CurrentSpeaker is { } speaker && !_nudged.Contains(NudgeKind.LongTalker)
            && now - _turnStartedAt >= TimeSpan.FromSeconds(options.LongTalkerSeconds))
        {
            _nudged.Add(NudgeKind.LongTalker);
            nudges.Add(new DebateNudge(
                NudgeKind.LongTalker,
                $"SYSTEM: {NameOf(speaker)} has held the floor for {options.LongTalkerSeconds} seconds. Interrupt politely and hand over to {NameOf(speaker.Other())} (call start_turn)."));
        }

        if (!_nudged.Contains(NudgeKind.ProbeWindowOpen) && elapsed >= TimeSpan.FromSeconds(options.ProbeWindowStartSeconds))
        {
            _nudged.Add(NudgeKind.ProbeWindowOpen);
            nudges.Add(new DebateNudge(
                NudgeKind.ProbeWindowOpen,
                $"SYSTEM: {options.ProbeWindowStartSeconds} seconds of arguing done. When you have heard enough (before {options.ProbeWindowEndSeconds} seconds), call end_debate and start questioning them one at a time with ask_probe."));
        }

        if (!_nudged.Contains(NudgeKind.DebateCap) && elapsed >= TimeSpan.FromSeconds(options.MaxDebateSeconds))
        {
            _nudged.Add(NudgeKind.DebateCap);
            nudges.Add(new DebateNudge(NudgeKind.DebateCap, "SYSTEM: Time is up. Stop the argument immediately (call end_debate) and begin the questions."));
        }
    }

    private string SilenceMessage() => Phase switch
    {
        SessionPhase.Intro => "SYSTEM: Nobody has answered. Ask again for the topic and both names, briefly.",
        SessionPhase.Setup => $"SYSTEM: Silence. Kick things off — invite {Player1Name} to open (call start_turn).",
        SessionPhase.Debate when CurrentSpeaker is { } speaker => $"SYSTEM: {NameOf(speaker)} has gone quiet. Prompt them by name, or hand over to {NameOf(speaker.Other())}.",
        SessionPhase.Probe => "SYSTEM: Silence. Repeat or simplify your question, or move to the other one.",
        _ => "SYSTEM: Silence. Keep the show moving.",
    };

    private void OpenTurn(PlayerId player, TurnKind kind, string text, DateTimeOffset now)
    {
        CloseOpenTurn(now);
        CurrentSpeaker = player;
        _turnStartedAt = now;
        NoteSpeechCore(now);
        _openTurnIndex = _turns.Count;
        _turns.Add(new TurnDto(MatchId, _turns.Count, player.ToSpeaker(), kind, text) { StartSeconds = Seconds(now) });
    }

    /// <summary>Closes whichever turn is open, wherever it sits in the list — interrupt entries may have been added after it.</summary>
    private void CloseOpenTurn(DateTimeOffset now)
    {
        if (_openTurnIndex is { } index)
        {
            _turns[index] = _turns[index] with { EndSeconds = Seconds(now) };
            _openTurnIndex = null;
        }
    }

    private double Seconds(DateTimeOffset now) => Math.Round((now - StartedAt).TotalSeconds, 2);
}
