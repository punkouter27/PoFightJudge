using Blazored.LocalStorage;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using PoFightJudge.Client.Components;
using PoFightJudge.Client.Pages;
using PoFightJudge.Client.Services;
using PoFightJudge.Shared.Identifiers;
using PoFightJudge.Shared.Models;
using Radzen;

namespace PoFightJudge.Unit.Client;

/// <summary>
/// The play screen holds the audio bridge, which is asynchronously disposable — so the context has to be torn down
/// asynchronously too, or the container refuses to dispose it at the end of every test.
/// </summary>
/// <summary>
/// The play screen holds the audio bridge, which is asynchronously disposable — so the context has to be torn down
/// asynchronously too, or the container refuses to dispose it at the end of every test.
/// </summary>
public sealed class WatchPlayTests : BunitContext, IAsyncLifetime
{
    private static readonly MatchSide Matthew = MatchSide.Persona("MAH", "Matthew");
    private static readonly MatchSide Kimberly = MatchSide.Persona("KSH", "Kimberly");
    private static readonly MatchSide Person = MatchSide.Human("KD", "Kim");

    private readonly IApiClient _api = Substitute.For<IApiClient>();
    private readonly SimulationState _simulation = new();
    private readonly FakeTimeProvider _clock = new();
    private readonly List<GenerateRoundRequest> _asked = [];

    /// <summary>
    /// What the voice chain says, if anything. A field rather than a second <c>Returns</c> per test: both would
    /// match <c>Arg.Any</c>, and which one wins is not something a test should be resting on.
    /// </summary>

    public WatchPlayTests()
    {
        Services.AddRadzenComponents();
        // The play screen binds S to the slap, so its hot-key context needs a home.
        Services.AddSingleton(_api);
        // Program.cs loads the gate before the first render; under bunit the component's own fallback load does it.
        Services.AddSingleton<FeatureGate>();
        Services.AddSingleton(_simulation);
        Services.AddSingleton<TimeProvider>(_clock);
        Services.AddScoped<AudioInterop>();
        Services.AddScoped<FxInterop>();
        // The stage makes a noise and draws on itself; both are best-effort, and under bunit both are no-ops.
        Services.AddSingleton(Substitute.For<ILocalStorageService>());
        Services.AddScoped<SfxInterop>();
        Services.AddScoped<GfxInterop>();
        Services.AddScoped<ParticleInterop>();
        Services.AddScoped<MicInterop>();
        Services.AddScoped<AsrInterop>();
        Services.AddScoped<Preferences>();
        JSInterop.Mode = JSRuntimeMode.Loose;

        _api.GenerateRoundAsync(Arg.Any<GenerateRoundRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<GenerateRoundRequest>();
                _asked.Add(request);
                return Task.FromResult(new GenerateRoundResponse($"line {_asked.Count} (seething)", "angry", "escalating", true));
            });

        // A line with nothing prefetched behind it is streamed, and one the prefetch holds is not. Both are the
        // same question asked of the same model, so both land in _asked: these tests are about the argument the
        // page has, not about which of the two transports carried a given line.
        _api.StreamRoundAsync(Arg.Any<GenerateRoundRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<GenerateRoundRequest>();
                _asked.Add(request);
                return OneLineAsync($"line {_asked.Count} (seething)");
            });
        _api.StreamRoundAudioAsync(Arg.Any<RoundAudioRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => ClausesAsync(null));
        _api.VerdictAsync(Arg.Any<VerdictRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new VerdictResponse(
                MatchId.New(), "MAH", "He answered the point; she changed the subject.",
                62, 41, AdvancedStatsDto.Empty, AdvancedStatsDto.Empty, Persisted: true)));
    }

    /// <summary>A stream that fails on the first read, which is how a refused round reaches the page.</summary>
    private static async IAsyncEnumerable<RoundStreamPart> Failing(ApiException failure)
    {
        await Task.Yield();
        throw failure;
#pragma warning disable CS0162 // Required to make the method an iterator; the throw above is the whole body.
        yield break;
#pragma warning restore CS0162
    }

    /// <summary>A stream that answered in one chunk, which is a complete stream and the shape a fake produces.</summary>
    private static async IAsyncEnumerable<RoundStreamPart> OneLineAsync(string line)
    {
        await Task.Yield();
        yield return new RoundStreamPart(null, new GenerateRoundResponse(line, "angry", "escalating", true));
    }

    /// <summary>The line as the chain said it — one clause, or nothing at all, which is still a line.</summary>
    private static async IAsyncEnumerable<RoundAudioChunkDto> ClausesAsync(TtsAudioDto? audio)
    {
        await Task.Yield();
        if (audio is not null)
        {
            yield return new RoundAudioChunkDto(0, audio.Base64, audio.Format, IsLast: true);
        }
    }

    /// <summary>Plays the argument out by letting each beat elapse until the page stops asking for lines.</summary>
    public Task InitializeAsync() => Task.CompletedTask;

    public new async Task DisposeAsync() => await base.DisposeAsync().ConfigureAwait(false);

    private static async Task RunToTheEndAsync(IRenderedComponent<WatchPlay> cut, FakeTimeProvider clock)
    {
        for (var beat = 0; beat < 24 && cut.FindAll("section.verdict").Count == 0 && cut.FindAll("section.your-turn").Count == 0; beat++)
        {
            await cut.InvokeAsync(() => clock.Advance(TimeSpan.FromSeconds(2)));
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task Two_personas_argue_their_rounds_out_voicing_but_not_printing_the_cue_and_the_judge_rules()
    {
        _simulation.Set(Matthew, Kimberly, "the thermostat", Stance.For);

        var cut = Render<WatchPlay>();
        await RunToTheEndAsync(cut, _clock);

        await cut.WaitForAssertionAsync(() => cut.FindAll("section.verdict").Should().HaveCount(1), TimeSpan.FromSeconds(10));
        cut.Find(".transcript").GetAttribute("data-lines").Should().Be("6", "each side gets its three rounds");
        cut.FindAll("article.line").Should().ContainSingle("only the line just said is on the page, so the page stays one screen");
        _asked.Select(a => a.Speaker).Should().Equal(
            [WatchTurns.Husband, WatchTurns.Wife, WatchTurns.Husband, WatchTurns.Wife, WatchTurns.Husband, WatchTurns.Wife],
            "the husband opens and the two alternate");
        _asked.Should().OnlyContain(a => a.Topic == "the thermostat");
        cut.Markup.Should().Contain("He answered the point");
        cut.Find("section.verdict .winning-side").TextContent.Trim().Should().Be("For wins", "Matthew took it, and he was for the thermostat");
        // History and the cast's standings are not on the nav bar: the end of a match is where they are reached from.
        cut.FindAll("section.verdict a[href='history'], section.verdict a[href='fighters']").Should().HaveCount(2);
        cut.Markup.Should().Contain("Matthew", "the ruling names the winner rather than printing initials alone");

        // The stage cue is voiced but not printed.
        cut.FindAll("article.line p").Select(p => p.TextContent).Should().Equal(
            ["line 6"], "the cue is for the voice, not the page, and the page shows the last line only");
        _api.ReceivedCalls()
            .Where(c => string.Equals(c.GetMethodInfo().Name, nameof(IApiClient.StreamRoundAudioAsync), StringComparison.Ordinal))
            .Select(c => ((RoundAudioRequest)c.GetArguments()[0]!).Text)
            .Should().NotBeEmpty().And.OnlyContain(t => t.EndsWith("(seething)", StringComparison.Ordinal), "the voice still gets the cue");
    }

    [Fact(Timeout = 60_000)]
    public async Task Each_round_rings_in_with_a_bell_across_the_stage_and_a_wipe_over_it()
    {
        _simulation.Set(Matthew, Kimberly, "the thermostat", Stance.For);

        var cut = Render<WatchPlay>();
        await RunToTheEndAsync(cut, _clock);
        await cut.WaitForAssertionAsync(() => cut.FindAll("section.verdict").Should().HaveCount(1), TimeSpan.FromSeconds(10));

        string[] Called(string identifier) =>
            [.. JSInterop.Invocations.Where(i => string.Equals(i.Identifier, identifier, StringComparison.Ordinal)).Select(i => (string)i.Arguments[0]!)];

        Called(SfxInterop.Play).Count(n => string.Equals(n, Sfx.RoundBell, StringComparison.Ordinal)).Should().Be(WatchTurns.RoundsPerSide);
        Called(FxInterop.Burst).Count(n => string.Equals(n, FxInterop.Round, StringComparison.Ordinal)).Should().Be(WatchTurns.RoundsPerSide);
    }

    [Fact(Timeout = 60_000)]
    public async Task The_persons_turn_stops_the_loop_until_they_have_said_something()
    {
        _simulation.Set(Matthew, Person, null);
        // Spoken turns on, so the turn opens listening — which is when the box is out of the way.
        _api.GetFeaturesAsync(Arg.Any<CancellationToken>()).Returns(new FeatureFlagsDto(UseFakeAi: true, DevGuestEnabled: true, HumanInWatch: true, FishVoices: false));

        var cut = Render<WatchPlay>();
        await cut.WaitForAssertionAsync(() => cut.FindAll("article.line").Should().HaveCount(1), TimeSpan.FromSeconds(10));
        await cut.AdvanceUntilAsync(_clock, "section.your-turn");

        await cut.WaitForAssertionAsync(() => cut.FindAll("section.your-turn").Should().HaveCount(1), TimeSpan.FromSeconds(10));
        _asked.Should().HaveCount(1, "the page does not write the person's line for them");
        cut.Markup.Should().Contain("Kim", "the panel addresses the person by the name they argue under");

        // While it listens the turn is spoken, so there is no box taking up the panel; typing is one press away.
        cut.FindAll("textarea[name=line]").Should().BeEmpty("a turn that is being spoken has nothing to type into");
        await cut.FindAll("button").Single(b => b.TextContent.Contains("Type it instead", StringComparison.Ordinal)).ClickAsync(new());

        // Typed, not changed: the line is published on every keystroke so the button answers straight away.
        await cut.Find("textarea[name=line]").InputAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "that is not what I said and you know it" });
        var spoken = cut.FindAll("button").Single(b => b.TextContent.Contains("Say it", StringComparison.Ordinal)).ClickAsync(new());

        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("that is not what I said"), TimeSpan.FromSeconds(10));
        int.Parse(cut.Find(".transcript").GetAttribute("data-lines")!, System.Globalization.CultureInfo.InvariantCulture)
            .Should().BeGreaterThanOrEqualTo(2, "the person's line joins the argument");
        await cut.WaitForAssertionAsync(() => _asked.Should().HaveCount(2, "the loop resumes on the persona's side once the person has spoken"), TimeSpan.FromSeconds(10));
        _asked[1].History.Should().HaveCount(2, "the persona answers the person's line, not the one before it");

        await cut.AdvanceUntilAsync(_clock, "section.your-turn");
        await spoken;
        await cut.WaitForAssertionAsync(() => cut.FindAll("section.your-turn").Should().HaveCount(1, "it is the person's turn again"), TimeSpan.FromSeconds(10));
    }

    [Fact(Timeout = 60_000)]
    public async Task A_line_that_cannot_be_generated_is_reported_and_the_argument_can_be_picked_up_again()
    {
        _simulation.Set(Matthew, Kimberly, null);
        _api.StreamRoundAsync(Arg.Any<GenerateRoundRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Failing(new ApiException(500, "Upstream", null)));

        var cut = Render<WatchPlay>();

        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("Upstream"), TimeSpan.FromSeconds(10));
        cut.FindAll("button").Should().Contain(b => b.TextContent.Contains("Try again", StringComparison.Ordinal));
        cut.FindAll("section.verdict").Should().BeEmpty("a failed line is not a result");
    }
}
