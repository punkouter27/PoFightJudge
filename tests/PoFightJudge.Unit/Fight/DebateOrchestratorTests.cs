using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PoFightJudge.Api.Features.Fight;
using PoFightJudge.Api.Features.Live;
using PoFightJudge.Api.Features.Storage;
using PoFightJudge.Shared.Identifiers;
using PoFightJudge.Shared.Models;
using PoFightJudge.TestSupport;

namespace PoFightJudge.Unit.Fight;

/// <summary>
/// Runs one live fight: audio both ways, the host's tool calls applied to the rules, deterministic nudges, two
/// recorded tracks, and everything persisted when it ends.
/// </summary>
/// <summary>
/// Runs one live fight: audio both ways, the host's tool calls applied to the rules, deterministic nudges, two
/// recorded tracks, and everything persisted when it ends.
/// </summary>
public sealed class DebateOrchestratorTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 9, 6, 19, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Start);
    private readonly FakeGeminiLiveClientFactory _clients = new();
    private readonly RecordingLiveSink _sink = new();
    private readonly RecordingAnalysisIntake _analysis = new();
    private readonly InMemoryAudioBlobStore _blobs = new();
    private readonly InMemoryWatchResultRepository _watchResults = new();
    private readonly InMemoryFighterResultRepository _fighterResults = new();
    private readonly InMemoryMatchRepository _matches;
    private readonly DebateOptions _options = new()
    {
        MaxDebateSeconds = 180,
        LongTalkerSeconds = 45,
        SilenceSeconds = 10,
        MaxSessionSeconds = 900,
        ClientGraceSeconds = 30,
        VerdictGraceSeconds = 45,
        SilenceGraceMilliseconds = 800,
        SilenceKeepAliveEvery = 4,
    };

    private DebateOrchestrator? _orchestrator;

    public DebateOrchestratorTests() => _matches = new InMemoryMatchRepository(_watchResults, _fighterResults, new InMemoryFighterWordsRepository());

    private static byte[] Loud(int samples = 800)
    {
        var pcm = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)(i % 2 == 0 ? 12000 : -12000));
        }

        return pcm;
    }

    private static byte[] Quiet(int samples = 800) => new byte[samples * 2];

    private DebateOrchestrator New(ShowSetup? setup = null, bool isFake = false)
    {
        _orchestrator = new DebateOrchestrator(
            "user-1",
            MatchId.New(),
            setup ?? new ShowSetup(HostPersonaId.Referee, "AB", "CD", "the thermostat"),
            () => _clients.Create(),
            _sink,
            _matches,
            _blobs,
            _analysis,
            _options,
            _clock,
            isFake,
            NullLogger<DebateOrchestrator>.Instance);
        return _orchestrator;
    }

    /// <summary>Starts a fight with a browser attached, which is the only state in which one runs.</summary>
    private async Task<DebateOrchestrator> StartedAsync(ShowSetup? setup = null, bool isFake = false)
    {
        var orchestrator = New(setup, isFake);
        orchestrator.ClientConnected();
        await orchestrator.StartAsync(CancellationToken.None);
        return orchestrator;
    }

    /// <summary>Starts a fight and takes it as far as the first turn, which is where most of these tests begin.</summary>
    private async Task<DebateOrchestrator> DebatingAsync(string floor = "player1")
    {
        var orchestrator = await StartedAsync();
        await EmitAsync(
            Call(ToolDeclarations.SetPlayers, new { topic = "the thermostat", player1 = "Alex", player2 = "Sam" }),
            () => orchestrator.Session.Phase == SessionPhase.Setup);
        await EmitAsync(
            Call(ToolDeclarations.StartTurn, new { player = floor }),
            () => orchestrator.Session.Phase == SessionPhase.Debate);
        return orchestrator;
    }

    /// <summary>Pushes an event in and waits for the orchestrator's loop to have acted on it.</summary>
    private const int PollAttempts = 400;

    private async Task EmitAsync(LiveServerEvent evt, Func<bool> until)
    {
        _clients.Latest.Emit(evt);
        await WaitForAsync(until);
        until().Should().BeTrue($"the orchestrator should have handled the event (errors: {string.Join("; ", _sink.Errors)})");
    }

    /// <summary>The event loop runs on its own thread, so a test waits for it rather than assuming it has caught up.</summary>
    private static async Task WaitForAsync(Func<bool> until)
    {
        for (var attempt = 0; attempt < PollAttempts && !until(); attempt++)
        {
            await Task.Delay(5, CancellationToken.None);
        }
    }

    private static LiveServerEvent.ToolCall Call(string name, object args) =>
        new("fc-1", name, JsonSerializer.SerializeToElement(args));

    public async ValueTask DisposeAsync()
    {
        if (_orchestrator is not null)
        {
            await _orchestrator.DisposeAsync();
        }
    }

    [Fact]
    public async Task The_hosts_tool_calls_drive_the_rules_and_ending_stores_the_match_the_turns_and_the_recording_and_queues_the_analysis()
    {
        var orchestrator = await DebatingAsync();

        // The host's tool calls drive the rules, and each one is answered.
        orchestrator.Session.Player1Name.Should().Be("AB", "the host does not get to rename anyone");
        orchestrator.Session.CurrentSpeaker.Should().Be(PlayerId.Player1);
        _clients.Latest.ToolResponses.Should().HaveCount(2).And.OnlyContain(r => r.Id == "fc-1");
        _sink.Latest!.Phase.Should().Be(SessionPhase.Debate, "the room is told as it happens");

        await orchestrator.OnAudioInAsync(Loud(), CancellationToken.None);

        await orchestrator.EndAsync("user pressed end", CancellationToken.None);

        var id = orchestrator.Session.MatchId;
        var stored = await _matches.GetAsync("user-1", id, CancellationToken.None);
        stored!.Status.Should().Be(SessionStatus.Analyzing);
        stored.EndedAt.Should().NotBeNull();
        stored.AudioBlobName.Should().Be(IAudioBlobStore.PlayersTrack(id, OpusAudio.Extension),
            "the name says which format it was stored in, which is how everything reading it back knows");
        (await _matches.GetTurnsAsync(id, CancellationToken.None)).Should().NotBeEmpty();
        (await _matches.GetAnalysisAsync(id, CancellationToken.None))!.Status.Should().Be(AnalysisStatus.Queued);
        _analysis.Queued.Should().ContainSingle().Which.Should().Be(("user-1", id));
        _sink.EndedMatch.Should().Be(id);
        orchestrator.Finished.Should().BeTrue();
        _clients.Latest.IsClosed.Should().BeTrue();
    }

    [Fact]
    public async Task A_fallacy_the_host_calls_reaches_the_room_on_the_next_snapshot()
    {
        await DebatingAsync();

        await EmitAsync(
            Call(ToolDeclarations.FlagFallacy, new { player = "player2", fallacy = "Strawman", quote = "so you want us to freeze" }),
            () => _sink.Latest?.Flags?.Count == 1);

        _sink.Latest!.Flags![0].Should().Be(new LiveFlagDto(Speaker.Player2, "Strawman", "so you want us to freeze"));
        ToolDeclarations.FunctionDeclarations().Select(d => d!["name"]!.GetValue<string>()).Should().Contain(ToolDeclarations.FlagFallacy);
    }

    [Fact]
    public async Task A_line_that_keeps_dropping_is_given_up_on_and_the_show_is_wrapped_up()
    {
        var orchestrator = await StartedAsync();

        for (var attempt = 0; attempt < 4; attempt++)
        {
            _clients.Latest.EmitClosed("socket reset");
            await WaitForAsync(() => _clients.Created.Count >= attempt + 2 || orchestrator.Ended);
        }

        await WaitForAsync(() => orchestrator.Ended);

        orchestrator.Ended.Should().BeTrue();
        _clients.Created.Should().HaveCount(4, "three reconnects, then it stops trying");
        _sink.Errors.Should().Contain(e => e.Contains("Lost the connection", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_show_that_runs_past_its_hard_cap_is_stopped()
    {
        var orchestrator = await StartedAsync();

        _clock.Advance(TimeSpan.FromSeconds(_options.MaxSessionSeconds + 1));
        await orchestrator.TickAsync(CancellationToken.None);

        orchestrator.Ended.Should().BeTrue();
    }
}
