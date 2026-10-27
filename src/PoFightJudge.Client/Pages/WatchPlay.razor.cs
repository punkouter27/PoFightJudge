using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using PoFightJudge.Client.Components;
using PoFightJudge.Client.Services;
using PoFightJudge.Shared.Identifiers;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Client.Pages;

/// <summary>
/// The round loop. Lines are asked for one at a time, spoken, and answered until both sides have had their rounds;
/// then the judge rules and the match is persisted server-side. The loop lives on the client because the pace of an
/// argument is a viewing decision — a spectator can interrupt it, and a person arguing a side stops it dead until
/// they have spoken.
/// </summary>
public sealed partial class WatchPlay : IAsyncDisposable
{
    /// <summary>
    /// The pause between lines when there is no audio to wait for. Without it the whole argument would appear at
    /// once, and nobody could get a slap in edgeways.
    /// </summary>
    internal static readonly TimeSpan MinimumBeat = TimeSpan.FromSeconds(1.2);

    /// <summary>The element the aura and the slap are drawn on. One selector, used to mount it and to take it down.</summary>
    public const string StageSelector = ".stage";

    private readonly List<WatchRoundDto> _rounds = [];
    private int _revealedRounds;

    /// <summary>
    /// Each round's spoken audio, by index, kept for the verdict. It is deliberately not held on the rounds
    /// themselves: every generate-round request carries the whole history, and clips on those would upload the
    /// entire match again per line. Only the verdict — sent once, at the end — needs them, and archiving them there
    /// is what a replay plays back.
    /// </summary>
    private readonly Dictionary<int, TtsAudioDto> _clips = [];
    private readonly CancellationTokenSource _leaving = new();

    private IReadOnlyList<ProfileDto> _cast = [];
    private CancellationTokenSource? _beat;

    /// <summary>
    /// When the line now being spoken finishes. The browser is asked the same question, but this is the answer the
    /// app already has — and if the two disagree the longer one wins, because cutting a speaker off mid-sentence
    /// is far worse than a moment of quiet after they finish.
    /// </summary>
    private DateTimeOffset? _lineEndsAt;
    private Stage _stage = Stage.Arguing;
    private string? _speaking;
    private string? _pendingInterjection;
    private bool _slapUsed;
    private bool _isFake;

    /// <summary>How many rounds have been rung in. The bell is the husband opening one, and it rings once each.</summary>
    private int _bellsRung;

    /// <summary>Whether the browser took the stage effects. It answers no on a phone that cannot, and under reduced motion.</summary>
    private bool _drawn;

    /// <summary>Who the aura is currently pointed at, so it is only re-aimed when the speaker actually changes.</summary>
    private string? _aimedAt;
    private string _line = string.Empty;
    private string? _error;

    /// <summary>
    /// When a throttled call may be tried again. The AI routes are limited per user, and a Try again that fires
    /// straight back into the same 429 reads as a broken button — so the button waits, visibly, and says how long.
    /// </summary>
    private DateTimeOffset? _retryAt;

    /// <summary>Held so the loop that counts it down is observed rather than dropped on the floor.</summary>
    private Task? _countdown;

    /// <summary>The ruling, once there is one.</summary>
    private VerdictResponse? _verdict;

    private enum Stage
    {
        Arguing,
        WaitingForPerson,
        Judging,
        Done,
    }

    [Inject] private IApiClient Api { get; set; } = default!;

    [Inject] private FeatureGate Features { get; set; } = default!;

    [Inject] private SimulationState Simulation { get; set; } = default!;

    [Inject] private AudioInterop Audio { get; set; } = default!;

    [Inject] private FxInterop Fx { get; set; } = default!;

    [Inject] private SfxInterop Sound { get; set; } = default!;

    [Inject] private GfxInterop Gfx { get; set; } = default!;

    [Inject] private ParticleInterop Particles { get; set; } = default!;

    [Inject] private MicInterop Mic { get; set; } = default!;

    [Inject] private TimeProvider Clock { get; set; } = default!;

    [Inject] private NavigationManager Nav { get; set; } = default!;

    [Inject] private IJSRuntime JS { get; set; } = default!;

    private LeaveGuard? _guard;

    /// <summary>The side a real person is arguing, when there is one.</summary>
    private MatchSide? Person => Simulation.Human;

    /// <summary>What to wait when a 429 arrives without a header to say. One window of the server's limit.</summary>
    private static readonly TimeSpan DefaultThrottleWait = TimeSpan.FromSeconds(30);

    private string Headline => Simulation.Topic.Length == 0 ? "The argument" : Simulation.Topic;

    /// <summary>Whole seconds left of a throttle, or zero when there is nothing to wait for.</summary>
    private int RetrySeconds =>
        _retryAt is { } at && at > Clock.GetUtcNow() ? (int)Math.Ceiling((at - Clock.GetUtcNow()).TotalSeconds) : 0;

    private string Lead =>
        _stage == Stage.Done ? "The judge has ruled. The match is on the record."
        : Person is not null ? "You are in this one. Answer when it is your turn."
        : "Three rounds each, then the judge rules.";

    protected override async Task OnInitializedAsync()
    {
        if (!Simulation.IsReady && !await RecallMatchupAsync())
        {
            // Nothing to play, and nothing this tab remembers: a shared link, or a tab opened fresh.
            Nav.NavigateTo("watch");
            return;
        }

        await RememberMatchupAsync();

        // The click that started this match is the gesture the audio context needs; taking it now means the first
        // bell rings on time rather than being swallowed by the autoplay policy.
        await Sound.ArmAsync(_leaving.Token);

        await LoadCastAsync();
        await RunAsync();
    }

    /// <summary>
    /// Mounts the stage effects once, then keeps the aura pointed at whoever is speaking. The aim is measured in
    /// the browser from the card itself, so it follows the layout rather than assuming one: the two corners are
    /// side by side on a laptop and stacked on a phone.
    /// </summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _drawn = await Gfx.MountAsync(StageSelector, Shaders.Stage, GfxInterop.WatchLevel, ct: _leaving.Token);
        }

        if (!_drawn || string.Equals(_aimedAt, _speaking, StringComparison.Ordinal))
        {
            return;
        }

        _aimedAt = _speaking;
        var side = _speaking is null ? 0 : WatchTurns.IsHusband(_speaking) ? -1 : 1;
        await Gfx.SetAsync(StageSelector, "side", side, _leaving.Token);
        await Gfx.FocusAsync(StageSelector, _speaking is null ? null : ".corner.speaking", _leaving.Token);
    }

    /// <summary>
    /// Portraits for the personas on the stage, and which ways of speaking a turn are open. A failure here costs a
    /// face and the microphone button, not the match: the line can always be typed.
    /// </summary>
    private async Task LoadCastAsync()
    {
        try
        {
            if (!Features.Loaded)
            {
                // The gate is loaded in Program.cs; this is the browser that booted while the API was down.
                await Features.LoadAsync(_leaving.Token);
            }

            _cast = await Api.GetProfilesAsync(_leaving.Token) ?? [];
        }
        catch (ApiException)
        {
            _cast = [];
        }
        catch (HttpRequestException)
        {
            _cast = [];
        }
    }

    /// <summary>
    /// Plays the argument out from wherever it stands. It returns at three points: the person's turn, the verdict,
    /// and a failure — each of which needs something to happen before there can be another line.
    /// </summary>
    private async Task RunAsync()
    {
        var ct = _leaving.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (WatchTurns.IsComplete(_rounds.Select(r => r.Speaker)) || _rounds.Count >= WatchTurns.MaxLines)
                {
                    await JudgeAsync(ct);
                    return;
                }

                var speaker = WatchTurns.NextSpeaker(_rounds.Count, _slapUsed);
                await RingRoundAsync(speaker, ct);
                if (SideOf(speaker).IsHuman)
                {
                    _stage = Stage.WaitingForPerson;
                    _speaking = null;
                    StateHasChanged();

                    // Their turn, announced: somebody watching the transcript rather than the stage has no other
                    // way of knowing the argument has stopped and is waiting for them.
                    await Sound.PlayAsync(Sfx.Cue, ct: ct);

                    // Their turn opens the microphone: pressing a button before you may speak is a step nobody
                    // asked for, and the meter is what says it is working.
                    await BeginListeningAsync();
                    return;
                }

                if (!await SpeakAsync(speaker, ct))
                {
                    return;
                }

                await BeatAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // The page is being left; the argument goes no further.
        }
    }

    /// <summary>
    /// Rings a round in. A round is the husband opening one, so it is his turn that carries the bell — and it is
    /// counted rather than derived, because a slap inserts a line without advancing anything.
    /// </summary>
    private async Task RingRoundAsync(string speaker, CancellationToken ct)
    {
        if (!WatchTurns.IsHusband(speaker))
        {
            return;
        }

        var round = _rounds.Count(r => WatchTurns.IsHusband(r.Speaker)) + 1;
        if (round <= _bellsRung || round > WatchTurns.RoundsPerSide)
        {
            return;
        }

        _bellsRung = round;
        await Fx.BurstAsync(FxInterop.Round, ct);
        await Sound.PlayAsync(Sfx.RoundBell, ct: ct);
    }

    /// <summary>Asks for one line, shows it, and speaks it. False when it could not be generated.</summary>
    private async Task<bool> SpeakAsync(string speaker, CancellationToken ct)
    {
        _stage = Stage.Arguing;
        _error = null;
        _speaking = speaker;
        StateHasChanged();

        var thrown = _pendingInterjection;
        _pendingInterjection = null;

        GenerateRoundResponse response;
        try
        {
            response = await NextLineAsync(speaker, thrown, ct);
        }
        catch (ApiException ex)
        {
            return Failed(ex);
        }
        catch (HttpRequestException ex)
        {
            return Failed($"The line could not be fetched ({ex.Message}).");
        }
        finally
        {
            // Whatever happened to it, the line is no longer arriving: the round below replaces it, and a failure
            // must not leave half a sentence on the page under a Try again.
            ClearArriving();
        }

        var round = new WatchRoundDto(speaker, response.Text, WatchTurns.NormalizeMood(response.Mood));
        _rounds.Add(round);
        _isFake |= response.IsFake;
        StateHasChanged();

        // The next line is asked for while this one is still being spoken, so the pause between them is the pause
        // a viewer expects rather than the round trip.
        StartPrefetch(ct);
        await PlayAsync(round, SideOf(speaker), ct);
        return true;
    }

    /// <summary>Fetches and plays a line's audio. A voiceless line is still a line, so a failure here is swallowed.</summary>
    private async Task PlayAsync(WatchRoundDto round, MatchSide side, CancellationToken ct)
    {
        if (side.IsHuman)
        {
            _revealedRounds = _rounds.Count;
            await InvokeAsync(StateHasChanged);
            return;
        }

        var revealed = false;
        void Reveal()
        {
            if (!revealed)
            {
                revealed = true;
                _revealedRounds = _rounds.Count;
            }
        }

        try
        {
            // Clause by clause: each one is scheduled the moment it arrives, so the line starts on its first phrase
            // rather than after the last one is synthesized. PoAudio butts them up against each other, so what the
            // listener hears is still one continuous utterance.
            var clauses = new List<TtsAudioDto>();

            // Mirrors PoAudio's own schedule clock: a clause starts where the previous one ends, or now if
            // synthesis fell behind playback. Accumulating durations onto "now" instead would push the end of the
            // line further out with every clause that arrived late, and the argument would sit through the drift.
            var endsAt = Clock.GetUtcNow();

            // The voice fetched ahead of this line, if there is one for these exact words. It is already whole, so
            // its clauses go straight onto the schedule; otherwise they are scheduled as they arrive.
#pragma warning disable VSTHRD003 // StartPrefetch started it, one turn earlier in this same loop.
            var ahead = TryTakePrefetchedAudio(out var waiting) ? await waiting : null;
#pragma warning restore VSTHRD003

            // Only for these exact words: a voice fetched for a line a slap threw away must never be played over
            // the line that replaced it.
            if (ahead is not null
                && string.Equals(ahead.SpeakerId, side.Id, StringComparison.Ordinal)
                && string.Equals(ahead.Text, round.Text, StringComparison.Ordinal))
            {
                foreach (var clause in ahead.Clauses)
                {
                    await ScheduleAsync(clause);
                }
            }
            else
            {
                await foreach (var chunk in Api.StreamRoundAudioAsync(new RoundAudioRequest(ProfileId.From(side.Id), round.Text), ct))
                {
                    await ScheduleAsync(chunk.Audio);
                }
            }

            async Task ScheduleAsync(TtsAudioDto clause)
            {
                if (clause.IsEmpty)
                {
                    return;
                }

                clauses.Add(clause);
                var spoken = await Audio.EnqueueAsync(clause, AudioInterop.PanFor(WatchTurns.IsHusband(round.Speaker)), ct);
                var now = Clock.GetUtcNow();
                endsAt = (endsAt > now ? endsAt : now) + spoken;

                // Re-read on every clause: the first is already playing while the rest are still being made, and
                // the beat has to know how long the whole line runs, not how long its first phrase did.
                _lineEndsAt = endsAt;

                // Reveal text in lockstep with the voice starting.
                Reveal();

                // The line is being spoken now rather than merely written, and the stage says so — the speaking
                // corner, the meter and the caret all read state this loop is the only thing moving. Through the
                // dispatcher, because a clause arrives on whatever thread its continuation landed on.
                await InvokeAsync(StateHasChanged);
            }

            // Kept as well as played: the same bytes are what the verdict archives and a replay plays back.
            var index = _rounds.IndexOf(round);
            if (index >= 0 && TtsAudioDto.Join(clauses) is { } whole)
            {
                _clips[index] = whole;
            }
        }
        catch (ApiException)
        {
            // The voice chain is down; the line stays on screen and the argument carries on.
        }
        catch (HttpRequestException)
        {
        }
        finally
        {
            // If the audio was voiceless, empty, or failed, ensure the line is still shown.
            Reveal();
            await InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>Holds the argument for as long as the line takes to speak, or a readable beat when it is silent.</summary>
    private async Task BeatAsync(CancellationToken ct)
    {
        using var beat = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _beat = beat;
        try
        {
            var pending = await Audio.PendingAsync(beat.Token);
            var remaining = _lineEndsAt is { } ends ? ends - Clock.GetUtcNow() : TimeSpan.Zero;
            var wait = Max(Max(pending, remaining), MinimumBeat);
            await Task.Delay(wait, Clock, beat.Token);

            static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // A slap landed mid-sentence: the reaction starts now rather than after the line finishes.
        }
        finally
        {
            _beat = null;
            _lineEndsAt = null;
        }
    }

    /// <summary>Takes the interjection, cuts the speaker off, and lets the loop pick the reaction up.</summary>
    private async Task ThrowAsync(Interjection heckle)
    {
        if (_slapUsed || _stage != Stage.Arguing)
        {
            return;
        }

        _slapUsed = true;
        _pendingInterjection = heckle.Key;
        DropPrefetch();
        await Fx.BurstAsync(heckle.Key, _leaving.Token);

        // A phone in the hand feels it land. Nowhere else has a motor, and saying so is not worth an exception.
        try
        {
            await JS.InvokeVoidAsync("navigator.vibrate", _leaving.Token, 70);
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException)
        {
        }

        // The crack lands over the top of the line it interrupts, on the side of whoever was saying it, and the ring
        // goes out from the middle of the stage — one gesture in three places, which is the only reason it reads as
        // a hit rather than a glitch.
        var struck = _speaking is null ? AudioInterop.Centre : AudioInterop.PanFor(WatchTurns.IsHusband(_speaking));
        await Sound.PlayOverAtAsync(Sfx.Slap, 0.6, struck, _leaving.Token);
        await Gfx.ShockAsync(StageSelector, _leaving.Token);
        await Particles.BurstAsync(StageSelector, ParticleInterop.Sparks, ".corner.speaking", _leaving.Token);
        await Audio.StopAsync(_leaving.Token);
        if (_beat is { } interrupted)
        {
            await interrupted.CancelAsync();
        }
    }

    private async Task OnLineTypedAsync(ChangeEventArgs e)
    {
        _line = e.Value?.ToString() ?? string.Empty;
        await StopListeningAsync();
    }

    /// <summary>The person's line, typed. Their spoken turn arrives with the microphone.</summary>
    private async Task SayAsync()
    {
        // Sending is the end of the turn however the words got there, so an open microphone is let go rather than
        // standing in the way: whoever typed this is finished, and the pause that ends a spoken turn is not coming.
        await StopListeningAsync();

        var said = _line.Trim();
        if (said.Length == 0 || _stage != Stage.WaitingForPerson)
        {
            return;
        }

        // What register they said it in is read from their words when the match is analysed, not guessed here; the
        // default is the middle of the scale, exactly as the server treats a line whose mood it does not know.
        var speaker = WatchTurns.NextSpeaker(_rounds.Count, _slapUsed);
        _rounds.Add(new WatchRoundDto(speaker, said, WatchTurns.NormalizeMood(null)));
        _revealedRounds = _rounds.Count;
        _line = string.Empty;
        _stage = Stage.Arguing;
        await RunAsync();
    }

    private async Task JudgeAsync(CancellationToken ct)
    {
        _stage = Stage.Judging;
        _speaking = null;
        StateHasChanged();

        // Three rings: that is the end of the argument, whatever the judge goes on to make of it.
        await Sound.PlayAsync(Sfx.BellThree, ct: ct);

        try
        {
            var topic = Simulation.Topic.Length == 0 ? null : Simulation.Topic;
            _verdict = await Api.VerdictAsync(new VerdictRequest(Simulation.Husband!, Simulation.Wife!, WithClips(), topic, HusbandStance: Simulation.HusbandStance), ct);
            _stage = Stage.Done;
            _error = null;
            await Sound.PlayOverAsync(Sfx.GavelThree, 0.9, ct);

            // After the render that puts the ruling on the page: the confetti falls on the card of whoever took it,
            // and there is nothing to measure that against until it is there.
            StateHasChanged();
            await Task.Yield();
            await Particles.BurstAsync(StageSelector, ParticleInterop.Confetti, WinnerCorner, ct);
            await LightAsync(ct);
        }
        catch (ApiException ex)
        {
            Failed(ex);
        }
        catch (HttpRequestException ex)
        {
            Failed($"The judge could not be reached ({ex.Message}).");
        }

        StateHasChanged();
    }

    /// <summary>
    /// The whole play area, under everything on it: the stage is nearly all opaque cards, so light under it alone
    /// would only show in the gaps. The streamers come down across all of it too.
    /// </summary>
    private const string RaysSelector = "div.play";

    /// <summary>
    /// The winner's light breaking out from behind their corner, the crowd on its feet and streamers coming down. A
    /// draw gets none of it. Nobody is recording by now: the microphone closed with the last turn (T65).
    /// </summary>
    private async Task LightAsync(CancellationToken ct)
    {
        if (WinnerCorner is not { } corner)
        {
            return;
        }

        if (await Gfx.MountAsync(RaysSelector, Shaders.Rays, ct: ct))
        {
            await Gfx.FocusAsync(RaysSelector, corner, ct);
            await Gfx.SetAsync(RaysSelector, "side", corner.Contains("first", StringComparison.Ordinal) ? -1 : 1, ct);
        }

        await Sound.PlayAsync(Sfx.Crowd, ct: ct);
        await Particles.BurstAsync(RaysSelector, ParticleInterop.Streamers, ct: ct);
    }

    private async Task RetryAsync()
    {
        _error = null;
        _stage = _rounds.Count == 0 ? Stage.Arguing : _stage;
        StateHasChanged();
        await RunAsync();
    }

    private async Task RematchAsync()
    {
        await Audio.StopAsync(_leaving.Token);
        await Gfx.UnmountAsync(RaysSelector, _leaving.Token);
        await Particles.ClearAsync(RaysSelector, _leaving.Token);
        _rounds.Clear();
        _revealedRounds = 0;
        _clips.Clear();
        _verdict = null;
        _error = null;
        _slapUsed = false;
        _pendingInterjection = null;
        _isFake = false;
        _bellsRung = 0;
        _stage = Stage.Arguing;
        DropPrefetch();
        await RunAsync();
    }

    private void Leave()
    {
        _guard?.Allow();
        Nav.NavigateTo("watch");
    }

    /// <summary>
    /// The matchup, kept for the life of the tab. The page holds it in memory, so a reload used to land on the setup
    /// screen with the argument gone; now a reload starts the same two over on the same topic.
    /// </summary>
    private const string MatchupKey = "po-watch-matchup";

    private sealed record Matchup(MatchSide Husband, MatchSide Wife, string Topic, Stance? HusbandStance = null);

    private async Task RememberMatchupAsync()
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new Matchup(Simulation.Husband!, Simulation.Wife!, Simulation.Topic, Simulation.HusbandStance), System.Text.Json.JsonSerializerOptions.Web);
            await JS.InvokeVoidAsync("sessionStorage.setItem", _leaving.Token, MatchupKey, json);
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException)
        {
            // Storage is blocked, or there is no browser here. A reload goes back to setup, as it always did.
        }
    }

    private async Task<bool> RecallMatchupAsync()
    {
        try
        {
            var json = await JS.InvokeAsync<string?>("sessionStorage.getItem", _leaving.Token, MatchupKey);
            if (string.IsNullOrEmpty(json) || System.Text.Json.JsonSerializer.Deserialize<Matchup>(json, System.Text.Json.JsonSerializerOptions.Web) is not { Husband: not null, Wife: not null } kept)
            {
                return false;
            }

            Simulation.Set(kept.Husband, kept.Wife, kept.Topic);
            return true;
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The argument with each line's audio attached, for the one request that archives it. A line nobody spoke —
    /// a person's turn, or one the voice chain could not manage — simply travels without a clip.
    /// </summary>
    private IReadOnlyList<WatchRoundDto> WithClips() =>
        [.. _rounds.Select((round, index) => _clips.TryGetValue(index, out var clip) ? round with { Audio = clip } : round)];

    /// <summary>Records a failure and stops the loop. Always false, so a caller can return it directly.</summary>
    private bool Failed(string message)
    {
        _error = message;
        _speaking = null;
        StateHasChanged();
        return false;
    }

    /// <summary>
    /// The same, for an answer from the API. A throttle is held apart from a fault: it carries a wait, and until that
    /// wait is up there is nothing to try again.
    /// </summary>
    private bool Failed(ApiException ex)
    {
        if (ex.IsThrottled)
        {
            StartCountdown(ex.RetryAfter ?? DefaultThrottleWait);
        }

        return Failed(ex.Summary);
    }

    /// <summary>Ticks once a second so the wait counts down on screen, and clears itself when the window is up.</summary>
    private void StartCountdown(TimeSpan wait)
    {
        _retryAt = Clock.GetUtcNow() + wait;
        _countdown = CountDownAsync();
    }

    private async Task CountDownAsync()
    {
        while (!_leaving.IsCancellationRequested && RetrySeconds > 0)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), Clock, _leaving.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            StateHasChanged();
        }

        _retryAt = null;
        StateHasChanged();
    }

    /// <summary>
    /// Which corner the confetti falls on and the light breaks out behind. A draw gets the middle of the stage rather
    /// than an arbitrary side. Of-type rather than last-child: PoGfx appends its canvases to the stage, so its last
    /// child is a canvas and "the last corner" by position matched nothing.
    /// </summary>
    private string? WinnerCorner =>
        _verdict is null || _verdict.Winner.Length == 0 ? null
        : string.Equals(Simulation.Husband?.Id, _verdict.Winner, StringComparison.OrdinalIgnoreCase)
            ? ".corner:first-of-type"
            : ".corner:last-of-type";

    private MatchSide SideOf(string speaker) =>
        WatchTurns.IsHusband(speaker) ? Simulation.Husband! : Simulation.Wife!;

    private bool IsSpeaking(string speaker) => string.Equals(_speaking, speaker, StringComparison.Ordinal);

    private string NameOf(string speaker) => SideOf(speaker).DisplayName;

    /// <summary>The display name behind a side id, for the ruling.</summary>
    private string NameOfSide(string id) =>
        string.Equals(Simulation.Husband?.Id, id, StringComparison.OrdinalIgnoreCase) ? Simulation.Husband!.DisplayName
        : string.Equals(Simulation.Wife?.Id, id, StringComparison.OrdinalIgnoreCase) ? Simulation.Wife!.DisplayName
        : id;

    private ProfileDto? ProfileFor(MatchSide? side) =>
        side is null || side.IsHuman
            ? null
            : _cast.FirstOrDefault(p => string.Equals(p.Persona.Initials, side.Id, StringComparison.OrdinalIgnoreCase));

    public async ValueTask DisposeAsync()
    {
        await _leaving.CancelAsync();
        DropPrefetch();

        // The microphone goes first: a recording light left on after the page is gone is not something to explain.
        await Mic.CancelAsync(CancellationToken.None);
        await Particles.ClearAsync(StageSelector, CancellationToken.None);

        // Then the canvas, before anything that might take its time: it holds a GL context and a frame loop, and
        // both of those outlive a page that only half finished tidying itself up.
        await Gfx.UnmountAsync(StageSelector, CancellationToken.None);
        await Gfx.UnmountAsync(RaysSelector, CancellationToken.None);
        await Particles.ClearAsync(RaysSelector, CancellationToken.None);

        // The beat owns its own lifetime through a using in BeatAsync; disposing it again here is free and is what
        // proves the field cannot outlive the page.
        _beat?.Dispose();
        _beat = null;
        _countdown = null;

        StopLevelWatch();
        _leaving.Dispose();
        await Audio.StopAsync(CancellationToken.None);
    }
}
