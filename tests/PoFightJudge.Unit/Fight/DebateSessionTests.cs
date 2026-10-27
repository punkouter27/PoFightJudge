using PoFightJudge.Api.Features.Fight;
using PoFightJudge.Shared.Identifiers;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Unit.Fight;

/// <summary>
/// The debate's rules, with no clock and no I/O of its own: every timing decision is a function of the timestamps
/// it is handed, so the whole show is reproducible from a list of moments.
/// </summary>
public class DebateSessionTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 6, 19, 0, 0, TimeSpan.Zero);
    private static readonly MatchId Match = MatchId.New();

    private static readonly DebateOptions Options = new()
    {
        MaxDebateSeconds = 180,
        LongTalkerSeconds = 45,
        SilenceSeconds = 10,
        ProbeWindowStartSeconds = 60,
        MaxProbeSeconds = 120,
    };

    private static readonly ShowSetup Pair = new(HostPersonaId.Referee, "AB", "CD", "the thermostat");

    private static DateTimeOffset At(double seconds) => Start.AddSeconds(seconds);

    private static DebateSession NewSession(ShowSetup? setup = null) => new(Options, Match, Start, setup);

    /// <summary>Drives a session to the point where both players are debating.</summary>
    private static DebateSession Debating()
    {
        var session = NewSession(Pair);
        session.SetPlayers("the thermostat", "AB", "CD", At(5));
        session.StartTurn(PlayerId.Player1, At(10));
        return session;
    }

    [Fact]
    public void A_fallacy_is_called_only_while_they_are_arguing_or_being_questioned_and_the_calls_are_capped()
    {
        var session = NewSession(Pair);
        session.FlagFallacy(PlayerId.Player1, "Strawman", "so you want us to freeze", At(1)).IsSuccess.Should().BeFalse("nobody has argued yet");

        session = Debating();
        session.FlagFallacy(PlayerId.Player2, "  Ad hominem ", "you would say that", At(20)).IsSuccess.Should().BeTrue();
        session.Snapshot(At(20)).Flags.Should().ContainSingle()
            .Which.Should().Be(new LiveFlagDto(Speaker.Player2, "Ad hominem", "you would say that"));

        for (var i = 0; i < DebateSession.MaxFlags; i++)
        {
            session.FlagFallacy(PlayerId.Player1, "Whataboutism", "what about you", At(30 + i));
        }

        session.Snapshot(At(60)).Flags.Should().HaveCount(DebateSession.MaxFlags, "a host that flags every sentence has stopped hosting");

        session.EndDebate(At(70));
        session.DeliverVerdict(new VerdictCall(PlayerId.Player1, PlayerId.Player1, PlayerId.Player1, ["a", "b", "c"]), At(80));
        session.FlagFallacy(PlayerId.Player1, "Strawman", "x", At(81)).IsSuccess.Should().BeFalse("the ruling is in");
    }

    [Fact]
    public void A_show_walks_from_the_introduction_to_the_ruling_refusing_steps_out_of_order_and_recording_every_turn()
    {
        var session = NewSession(Pair);
        session.Phase.Should().Be(SessionPhase.Intro);

        session.SetPlayers("the thermostat", "AB", "CD", At(5)).IsSuccess.Should().BeTrue();
        session.Phase.Should().Be(SessionPhase.Setup);

        session.StartTurn(PlayerId.Player1, At(10)).IsSuccess.Should().BeTrue();
        session.Phase.Should().Be(SessionPhase.Debate);
        session.CurrentSpeaker.Should().Be(PlayerId.Player1);

        session.EndDebate(At(70)).IsSuccess.Should().BeTrue();
        session.Phase.Should().Be(SessionPhase.Probe);
        session.CurrentSpeaker.Should().BeNull("nobody holds the floor between the debate and the questions");

        session.AskProbe(PlayerId.Player2, "Where did that number come from?", At(75)).IsSuccess.Should().BeTrue();
        session.DeliverVerdict(new VerdictCall(PlayerId.Player1, PlayerId.Player2, PlayerId.Player1, ["clearer", "evidence", "stayed on topic"]), At(120))
            .IsSuccess.Should().BeTrue();
        session.Phase.Should().Be(SessionPhase.Verdict);

        session.Complete(At(140));
        session.Phase.Should().Be(SessionPhase.Done);

        // Steps taken out of order are refused by name rather than half applied.
        var refused = NewSession(Pair);

        // Skipping forward is allowed — a host that has moved on has moved on — but going back is not: the debate
        // is over once the questions start, and a refused step must leave the show exactly where it was.
        refused.SetPlayers("t", "AB", "CD", At(5));
        refused.StartTurn(PlayerId.Player1, At(10));
        refused.EndDebate(At(60));

        refused.StartTurn(PlayerId.Player2, At(65)).Error.Should().Contain("Probe");
        refused.Phase.Should().Be(SessionPhase.Probe, "a refused step changes nothing");

        refused.SetPlayers("something else", "ZZ", "YY", At(70)).Error.Should().Contain("Probe");
        refused.Player1Name.Should().Be("AB", "and nothing it refused was half applied");

        // Turns are recorded with their own start and end, and an interruption names who was cut off.
        var debating = Debating();
        debating.NoteInterrupt(At(55));
        debating.StartTurn(PlayerId.Player2, At(56));
        debating.EndDebate(At(90));

        var talk = debating.Turns.Where(t => t.Kind == TurnKind.Talk).ToList();
        talk.Should().HaveCount(2);
        talk[0].Speaker.Should().Be(Speaker.Player1);
        talk[0].StartSeconds.Should().Be(10);
        talk[0].EndSeconds.Should().Be(56, "a turn ends when the next one starts");
        talk[1].EndSeconds.Should().Be(90, "the last turn ends when the debate does");

        var interrupt = debating.Turns.Should().ContainSingle(t => t.Kind == TurnKind.Interrupt).Subject;
        interrupt.Speaker.Should().Be(Speaker.Player1, "the interruption belongs to whoever was cut off");
        interrupt.Text.Should().Contain("AB");
        debating.Turns.Should().OnlyContain(t => t.MatchId == Match);
    }

    /// <summary>
    /// The hub thread, the ticker, the host's event loop and an HTTP read all call into one session. And a nudge the
    /// session has ticked off but the line then dropped is still owed.
    /// </summary>
    [Fact]
    public async Task Four_callers_at_once_do_not_corrupt_it_and_a_nudge_that_was_not_delivered_is_owed_again()
    {
        var session = Debating();
        var callers = Enumerable.Range(0, 4).Select(caller => Task.Run(() =>
        {
            for (var i = 0; i < 2_000; i++)
            {
                var now = At(10 + (i / 100.0));
                switch (caller)
                {
                    case 0: session.NoteSpeech(now); break;
                    case 1: session.Tick(now); break;
                    case 2: session.StartTurn(i % 2 == 0 ? PlayerId.Player1 : PlayerId.Player2, now); break;
                    default: session.Snapshot(now).Phase.Should().Be(SessionPhase.Debate); _ = session.Turns.Count; break;
                }
            }
        }));
        await Task.WhenAll(callers);
        session.Turns.Select(t => t.Index).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems("every turn got its own slot");

        session.Tick(At(200)).Select(n => n.Kind).Should().Contain(NudgeKind.DebateCap);
        session.Tick(At(201)).Select(n => n.Kind).Should().NotContain(NudgeKind.DebateCap, "it fires once");
        session.Rearm(NudgeKind.DebateCap);
        session.Tick(At(202)).Select(n => n.Kind).Should().Contain(NudgeKind.DebateCap, "the line dropped before the host heard it");
    }
}
