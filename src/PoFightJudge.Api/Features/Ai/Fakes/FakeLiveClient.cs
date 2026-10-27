using System.Text.Json;
using System.Threading.Channels;
using PoFightJudge.Api.Features.Live;

namespace PoFightJudge.Api.Features.Ai.Fakes;

/// <summary>
/// A scripted host for when there is no Gemini key. It is driven by the microphone rather than by a timer: every so
/// many frames it takes the next step of the show, so a fight only advances while somebody is actually talking into
/// it, and the whole thing — greeting, turns, questions, ruling — plays out offline and in order.
/// </summary>
/// <remarks>
/// Every line it speaks says it is a rehearsal. Silently sounding like the real host would make a missing key
/// indistinguishable from a working one, which is the confusion the fake-AI banner exists to prevent.
/// </remarks>
public sealed class FakeLiveClient(TimeProvider clock) : IGeminiLiveClient
{
    /// <summary>Microphone frames between one beat of the script and the next. Frames are 100 ms, so this is about two seconds.</summary>
    public const int FramesPerBeat = 20;

    /// <summary>Length of the "voice" the fake emits per beat: silence, but silence of a believable length.</summary>
    public const int SpokenMilliseconds = 400;

    private readonly Channel<LiveServerEvent> _events = Channel.CreateUnbounded<LiveServerEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock _lock = new();
    private int _frames;
    private int _beat;
    private bool _closed;

    public ChannelReader<LiveServerEvent> Events => _events.Reader;

    /// <summary>The fake never offers one: there is no session to resume.</summary>
    public string? ResumptionHandle => null;

    /// <summary>The show, in order. Each step is a line the host says and, where the rules need one, a tool call.</summary>
    private static IReadOnlyList<(string Says, string? Tool, object? Args)> Script { get; } =
    [
        ("(rehearsal host) Welcome, both of you. Let us hear it.", ToolDeclarations.SetPlayers, new { topic = "", player1 = "", player2 = "" }),
        ("(rehearsal host) You first. Thirty seconds.", ToolDeclarations.StartTurn, new { player = "player1" }),
        ("(rehearsal host) Right, your turn.", ToolDeclarations.StartTurn, new { player = "player2" }),
        ("(rehearsal host) Whataboutism. Carry on.", ToolDeclarations.FlagFallacy, new { player = "player2", fallacy = "Whataboutism", quote = "what about you, though" }),
        ("(rehearsal host) That is enough arguing. Questions.", ToolDeclarations.EndDebate, null),
        ("(rehearsal host) Where is your evidence for that?", ToolDeclarations.AskProbe, new { player = "player1", question = "Where is your evidence for that?" }),
        ("(rehearsal host) And you said the opposite a minute ago.", ToolDeclarations.AskProbe, new { player = "player2", question = "Did you not say the opposite a minute ago?" }),
        (
            "(rehearsal host) I have heard enough.",
            ToolDeclarations.DeliverVerdict,
            new
            {
                winner_logic = "player1",
                winner_correct = "player2",
                overall = "player1",
                reasons = new[] { "answered the actual question", "did not change the subject", "conceded the one point worth conceding" },
            }),
        ("(rehearsal host) That is my ruling. Stop keeping score and go to bed.", null, null),
    ];

    public Task ConnectAsync(LiveSessionConfig config, CancellationToken ct)
    {
        _events.Writer.TryWrite(new LiveServerEvent.SetupComplete());
        return Task.CompletedTask;
    }

    /// <summary>
    /// The microphone drives the script. Frames arrive whether or not anyone is speaking, which is what makes the
    /// pacing predictable: a fake fight is always the same length in frames.
    /// </summary>
    public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16k, CancellationToken ct)
    {
        var due = false;
        lock (_lock)
        {
            if (!_closed && ++_frames % FramesPerBeat == 0 && _beat < Script.Count)
            {
                due = true;
            }
        }

        return due ? PlayNextBeatAsync() : Task.CompletedTask;
    }

    /// <summary>A nudge from the producer. The fake obeys the two that matter by taking the step they ask for.</summary>
    public Task SendTextAsync(string text, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Contains("Time is up", StringComparison.Ordinal) || text.Contains("Deliver the ruling", StringComparison.Ordinal)
            ? PlayNextBeatAsync()
            : Task.CompletedTask;
    }

    public Task SendToolResponseAsync(string id, string name, object result, CancellationToken ct) => Task.CompletedTask;

    public Task CloseAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            _closed = true;
        }

        _events.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private Task PlayNextBeatAsync()
    {
        (string Says, string? Tool, object? Args) step;
        lock (_lock)
        {
            if (_closed || _beat >= Script.Count)
            {
                return Task.CompletedTask;
            }

            step = Script[_beat++];
        }

        // The tool call comes before the line, exactly as the host is instructed to do it.
        if (step.Tool is { } tool)
        {
            _events.Writer.TryWrite(new LiveServerEvent.ToolCall(
                $"fake-{clock.GetUtcNow().ToUnixTimeMilliseconds()}",
                tool,
                JsonSerializer.SerializeToElement(step.Args ?? new object())));
        }

        _events.Writer.TryWrite(new LiveServerEvent.OutputTranscript(step.Says));
        _events.Writer.TryWrite(new LiveServerEvent.AudioOut(Silence()));
        _events.Writer.TryWrite(new LiveServerEvent.TurnComplete());
        return Task.CompletedTask;
    }

    /// <summary>Audio of the right shape and length for the room to play, carrying nothing.</summary>
    private static byte[] Silence() =>
        new byte[Fight.DebateOrchestrator.HostSampleRate / 1000 * SpokenMilliseconds * 2];
}
