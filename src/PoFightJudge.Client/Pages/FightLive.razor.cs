using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using PoFightJudge.Client.Components;
using PoFightJudge.Client.Services;
using PoFightJudge.Shared.Identifiers;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Client.Pages;

/// <summary>
/// One fight, as it happens. The page joins the fight, opens the microphone, plays the host back and shows what is
/// being said; when the ruling is in it hands over to the verdict.
/// </summary>
/// <remarks>
/// Everything the fight sends arrives on a background connection, so every handler hops back onto the renderer
/// before touching state. Without that the page would update from the wrong thread and render half-written state.
/// </remarks>
public sealed partial class FightLive : ComponentBase, ILiveFightListener, IAsyncDisposable
{
    /// <summary>The element the backdrop is drawn on, and the one the glass above it blurs.</summary>
    public const string StageSelector = ".stage";

    /// <summary>The PoFx class the stage breaks up with when the host is cut off (see FightLive.razor.css).</summary>
    private const string GlitchEffect = "glitch";

    /// <summary>Where the debate clock starts running hot. The backdrop turns over the last half-minute.</summary>
    private const int HeatFromSeconds = 30;

    private readonly CaptionLog _captions = new();

    /// <summary>Who has done the talking so far, for the tug-of-war under the scoreboard.</summary>
    private readonly FloorClock _floor = new();

    /// <summary>The same clock fed loud seconds instead of voiced ones, for the calm meter.</summary>
    private readonly FloorClock _loud = new();

    private readonly CancellationTokenSource _leaving = new();

    /// <summary>Their records and their series, for the tale of the tape before the bell.</summary>
    private FighterStatsDto? _oneStats;
    private FighterStatsDto? _twoStats;
    private HeadToHeadDto? _series;

    /// <summary>Where the choice of table mode is kept, so a phone that lives on the table opens in it next time.</summary>
    private const string TableKey = "po-table";

    /// <summary>Table mode: the stage takes the whole screen, in words big enough to read from a chair.</summary>
    private bool _table;

    /// <summary>Whether the tale of the tape is over the stage, and the timer that takes it down.</summary>
    private bool _tape;
    private Task? _tapeDown;
    private LiveConnection? _connection;
    private FighterDto? _oneRecord;
    private FighterDto? _twoRecord;
    private DebateSnapshotDto? _snapshot;
    private bool _listening;
    private bool _hostSpeaking;
    private bool _reconnecting;
    private bool _ending;
    private string? _problem;
    private string? _fatal;

    /// <summary>Whether the browser took the backdrop. No is a perfectly good answer; the glass has its own floor.</summary>
    private bool _drawn;

    /// <summary>The phase the last stinger was played for, so one phase is announced once.</summary>
    private SessionPhase? _announced;

    /// <summary>Who was last handed the floor, so the cue is played on the change rather than on every snapshot.</summary>
    private Speaker? _lastFloor;

    /// <summary>The clock on the last snapshot, so a riser is played on the crossing rather than on every one after it.</summary>
    private int _lastRemaining = int.MaxValue;

    [Parameter] public string MatchIdText { get; set; } = string.Empty;

    [Inject] private LiveAudio Audio { get; set; } = default!;

    [Inject] private SetupMemory Memory { get; set; } = default!;

    [Inject] private LiveConnectionFactory Connections { get; set; } = default!;

    [Inject] private IApiClient Api { get; set; } = default!;

    [Inject] private NavigationManager Nav { get; set; } = default!;

    private LeaveGuard? _guard;

    [Inject] private SfxInterop Sound { get; set; } = default!;

    [Inject] private GfxInterop Gfx { get; set; } = default!;

    [Inject] private ParticleInterop Particles { get; set; } = default!;

    [Inject] private FxInterop Fx { get; set; } = default!;

    [Inject] private IJSRuntime Js { get; set; } = default!;

    [Inject] private TimeProvider Clock { get; set; } = default!;

    [Inject] private ILocalStorageService Storage { get; set; } = default!;

    private SessionPhase Phase => _snapshot?.Phase ?? SessionPhase.Intro;

    private string Player1 => _snapshot?.Player1Name is { Length: > 0 } name ? name : "Fighter one";

    private string Player2 => _snapshot?.Player2Name is { Length: > 0 } name ? name : "Fighter two";

    private string HostName => HostPersonaCatalogue.For(_snapshot?.Persona ?? HostPersonaId.Referee).Name;

    private string Title => _snapshot?.Topic is { Length: > 0 } topic ? topic : "The fight";

    private string Lead => _listening
        ? "The host moderates the turns. Speak when it hands you the floor."
        : "Connecting you to the host.";

    protected override async Task OnInitializedAsync()
    {
        if (!MatchId.TryParse(MatchIdText, null, out var id))
        {
            _fatal = "That is not a fight.";
            return;
        }

        // The click that started the fight is the gesture the effects context needs.
        await Sound.ArmAsync();

        try
        {
            _table = string.Equals((await Storage.GetItemAsStringAsync(TableKey))?.Trim('"'), "on", StringComparison.Ordinal);
            if (_table)
            {
                await Js.InvokeVoidAsync("PoFx.table", true, false);
            }
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException)
        {
            // Storage blocked: the ordinary stage, as it always was.
        }

        _connection = Connections.Create();
        try
        {
            await _connection.ConnectAsync(id, this);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _fatal = $"This fight could not be joined ({ex.Message}). It may have already ended.";
            return;
        }

        // The microphone opens only once the fight is joined, so nothing is recorded into nowhere.
        // The microphone chosen and tested at setup, so the fight opens the one that was proved to work.
        var started = await Audio.StartCaptureAsync(_connection, await Memory.MicAsync());
        if (!started.Started)
        {
            _fatal = MicError.Describe(started.Error);
            return;
        }

        _listening = true;
    }

    /// <summary>
    /// Mounts the stage's ink once the stage is really in the page: a fluid that whoever has the floor pours their
    /// colour into from their side, as hard as they are talking, and the host pours into from below. A browser that
    /// cannot simulate it (no float render targets) gets the backdrop that breathes with the host instead.
    /// </summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && _fatal is null)
        {
            _drawn = await Gfx.MountAsync(StageSelector, Shaders.Fluid, GfxInterop.LiveLevel)
                || await Gfx.MountAsync(StageSelector, Shaders.Backdrop, GfxInterop.LiveLevel);
        }
    }

    public void OnHostAudio(byte[] pcm24k) => Dispatch(async () =>
    {
        _hostSpeaking = true;
        await Audio.PlayAsync(pcm24k);
    });

    public void OnHostInterrupted() => Dispatch(async () =>
    {
        var cutIn = _hostSpeaking;
        _hostSpeaking = false;
        await Audio.ClearPlaybackAsync();

        // Somebody talked over the host, so the picture breaks up and the line crackles: a cut-in is a foul and it
        // should feel like one. Only when the host really was mid-sentence — an interruption of silence is not one.
        if (cutIn)
        {
            await Fx.BurstAsync(GlitchEffect);
            await Sound.PlayAsync(Sfx.Glitch);
        }
    });

    /// <summary>
    /// A caption fragment. These arrive several times a second while a line is being rewritten, and they say
    /// nothing about the phase, the clock, the host or the scoreboard — CaptionFeed subscribes to the log and
    /// redraws itself, so the page is only redrawn for the one fragment that does change something up here.
    /// </summary>
    public void OnCaption(CaptionDto caption)
    {
        _captions.Add(caption);

        // The host has finished a sentence; anything still queued is the tail of it.
        if (caption is { Speaker: Speaker.Host, Final: true } && _hostSpeaking)
        {
            Dispatch(() =>
            {
                _hostSpeaking = false;
                return Task.CompletedTask;
            });
        }
    }

    public void OnSnapshot(DebateSnapshotDto snapshot) => Dispatch(async () =>
    {
        var first = _snapshot is null;
        _snapshot = snapshot;

        // The tags are only known once the first snapshot lands, so their records are read then and not before.
        if (first)
        {
            await LoadRecordsAsync(snapshot);
        }

        // The bell takes the card down if the host gets there before the timer does.
        if (snapshot.Phase is not (SessionPhase.Intro or SessionPhase.Setup))
        {
            _tape = false;
        }

        await AnnounceAsync(snapshot);
        await PaintAsync(snapshot);
        if (await VoicedAsync() is { } heard)
        {
            _floor.Tick(snapshot.Speaking, _hostSpeaking, heard.Voiced);
            _loud.Tick(snapshot.Speaking, _hostSpeaking, heard.Loud);
        }
    });

    /// <summary>What <c>PoLive.voiced</c> answers.</summary>
    private sealed record Heard(double Voiced, double Loud);

    /// <summary>Seconds of voice the microphone has heard, and how many were loud, counted by the browser. Null when it will not say.</summary>
    private async Task<Heard?> VoicedAsync()
    {
        try
        {
            return await Js.InvokeAsync<Heard>("PoLive.voiced");
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The show's punctuation: a sting on each new phase and a cue when the floor changes hands. Both land over
    /// the host rather than beside them — the host is usually mid-sentence when the phase turns over, and a sound
    /// competing with the words is worse than no sound.
    /// </summary>
    private async Task AnnounceAsync(DebateSnapshotDto snapshot)
    {
        if (_announced != snapshot.Phase)
        {
            _announced = snapshot.Phase;
            if (StingFor(snapshot.Phase) is { } sting)
            {
                await Sound.PlayOverAsync(sting, 0.7);
            }
        }

        // Half a minute to go, and ten seconds: a climb that never arrives, under the host rather than over them.
        if (RiserDue(snapshot.Phase, _lastRemaining, snapshot.DebateRemainingSeconds))
        {
            await Sound.PlayAsync(Sfx.Riser);
        }

        _lastRemaining = snapshot.DebateRemainingSeconds;

        // Only a real hand-over, and only while somebody actually has the floor: the host taking it back is their
        // own voice saying so.
        if (_lastFloor != snapshot.Speaking)
        {
            var had = _lastFloor;
            _lastFloor = snapshot.Speaking;
            if (snapshot.Speaking is not null && had is not null)
            {
                await Sound.PlayAsync(Sfx.Cue);
                if (_table)
                {
                    await BuzzAsync();
                }
            }
        }
    }

    /// <summary>Which sound belongs to a phase. Intro has none: the host is already talking over it.</summary>
    public static string? StingFor(SessionPhase phase) => phase switch
    {
        SessionPhase.Setup => Sfx.Intro,
        SessionPhase.Debate => Sfx.Bell,
        SessionPhase.Probe => Sfx.Probe,
        SessionPhase.Verdict => Sfx.Ruling,
        SessionPhase.Done => Sfx.BellThree,
        _ => null,
    };

    /// <summary>
    /// Points the backdrop at whoever has the floor and heats it as the clock runs down. Both are one number each,
    /// and the shader does the rest — nothing here is drawn from .NET.
    /// </summary>
    private async Task PaintAsync(DebateSnapshotDto snapshot)
    {
        if (!_drawn)
        {
            return;
        }

        var mix = snapshot.Speaking switch
        {
            Speaker.Player1 => 0.12,
            Speaker.Player2 => 0.88,
            _ => 0.5,
        };

        var heat = HeatFor(snapshot.Phase, snapshot.DebateRemainingSeconds);
        await Gfx.SetAsync(StageSelector, "mix", mix);
        await Gfx.SetAsync(StageSelector, "heat", heat);

        // Embers only while the clock is actually running out. Thirty a second at the death is enough to see and
        // few enough that a phone does not notice them.
        if (snapshot.Phase == SessionPhase.Debate)
        {
            await Particles.EmitAsync(StageSelector, heat * 30);
        }
        else
        {
            await Particles.EmitAsync(StageSelector, 0);
        }
    }

    /// <summary>
    /// How hot the backdrop runs, 0 to 1. It climbs through the last half-minute of arguing and sits halfway up
    /// once the host has taken over — the questions and the ruling are not calm, but they are not a countdown
    /// either. Public because it is the whole decision, and the only part of the backdrop worth asserting.
    /// </summary>
    public static double HeatFor(SessionPhase phase, int remainingSeconds) => phase switch
    {
        SessionPhase.Debate when remainingSeconds is > 0 and <= HeatFromSeconds =>
            (HeatFromSeconds - remainingSeconds) / (double)HeatFromSeconds,
        SessionPhase.Probe or SessionPhase.Verdict => 0.5,
        _ => 0,
    };

    /// <summary>The clock readings a riser announces: half a minute to go, and ten seconds.</summary>
    private static readonly int[] RiserMarks = [HeatFromSeconds, 10];

    /// <summary>
    /// Whether the clock has just crossed one of <see cref="RiserMarks"/>, going from <paramref name="before"/> to
    /// <paramref name="now"/> seconds left. Once per mark, only while they are arguing, and never on the zero — the
    /// bell has that. Public because it is the whole decision.
    /// </summary>
    public static bool RiserDue(SessionPhase phase, int before, int now) =>
        phase == SessionPhase.Debate && now > 0 && RiserMarks.Any(mark => before > mark && now <= mark);

    /// <summary>
    /// What each of them has done before, and how it went between them, for the scoreboard and the tale of the tape.
    /// Missing records are simply absent; the fight does not need them, and a card that could not be read is a card
    /// that is not shown.
    /// </summary>
    private async Task LoadRecordsAsync(DebateSnapshotDto snapshot)
    {
        if (!FighterId.TryParse(snapshot.Player1Name, null, out var one) || !FighterId.TryParse(snapshot.Player2Name, null, out var two))
        {
            return;
        }

        try
        {
            var first = await Api.GetFighterProfileAsync(one);
            var second = await Api.GetFighterProfileAsync(two);
            (_oneRecord, _oneStats) = (first?.Fighter, first?.Stats);
            (_twoRecord, _twoStats) = (second?.Fighter, second?.Stats);
            _series = await Api.GetHeadToHeadAsync(one, two);
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException)
        {
            // The records are decoration on a fight, never a reason to stop one.
        }

        // Only before the arguing starts: a card over the stage once they are at it would be in the way.
        if (snapshot.Phase is SessionPhase.Intro or SessionPhase.Setup)
        {
            _tape = true;
            await Sound.PlayAsync(Sfx.Whoosh);
            _tapeDown = TakeTapeDownAsync();
        }
    }

    /// <summary>The card stays up while the host introduces them, and no longer than this.</summary>
    private static readonly TimeSpan TapeFor = TimeSpan.FromSeconds(7);

    private async Task TakeTapeDownAsync()
    {
        try
        {
            await Task.Delay(TapeFor, Clock, _leaving.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await InvokeAsync(() =>
        {
            _tape = false;
            StateHasChanged();
        });
    }

    private void SkipTape() => _tape = false;

    /// <summary>
    /// Into or out of table mode. Fullscreen is asked for on this click, the only moment a browser allows it; the
    /// takeover itself is CSS, so a phone that will not go fullscreen still gets the whole page. Remembered, so it
    /// opens that way next time — without fullscreen, which only a click can ask for.
    /// </summary>
    private async Task TableAsync(bool on)
    {
        _table = on;
        try
        {
            await Js.InvokeVoidAsync("PoFx.table", on, true);
            await Storage.SetItemAsStringAsync(TableKey, on ? "on" : "off");
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException)
        {
            // Not remembered, or not fullscreen. The stage is still the whole page.
        }
    }

    /// <summary>Two short pulses: the floor has changed hands.</summary>
    private static readonly int[] Buzz = [40, 60, 40];

    /// <summary>A phone lying flat buzzes on the table when the floor changes hands. Nowhere else has a motor.</summary>
    private async Task BuzzAsync()
    {
        try
        {
            await Js.InvokeVoidAsync("navigator.vibrate", Buzz);
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException)
        {
        }
    }

    public void OnEnded(string matchId) => Dispatch(async () =>
    {
        _listening = false;
        await Audio.StopAsync();
        _guard?.Allow();
        Nav.NavigateTo($"verdict/{matchId}");
    });

    public void OnProblem(string message) => Dispatch(() =>
    {
        _problem = message;
        return Task.CompletedTask;
    });

    public void OnConnectionLost(string? reason) => Dispatch(() =>
    {
        _reconnecting = true;
        return Task.CompletedTask;
    });

    public void OnConnectionAbandoned(string reason) => Dispatch(async () =>
    {
        _reconnecting = false;
        _listening = false;
        _fatal = reason;
        await Audio.StopAsync();
    });

    private async Task StopAsync()
    {
        if (_ending || _connection is null)
        {
            return;
        }

        _ending = true;
        try
        {
            await _connection.EndFightAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _problem = $"The fight could not be stopped cleanly ({ex.Message}).";
            _ending = false;
        }
    }

    /// <summary>Runs a handler on the renderer and redraws. Everything the fight sends arrives off it.</summary>
    private void Dispatch(Func<Task> work) => _ = InvokeAsync(async () =>
    {
        // A reconnected fight is a connected fight, whatever else the update was about.
        _reconnecting = false;
        try
        {
            await work();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _problem = ex.Message;
        }

        StateHasChanged();
    });

    public async ValueTask DisposeAsync()
    {
        await _leaving.CancelAsync();
        _tapeDown = null;

        // Table mode belongs to this page: the chrome comes back, and so does the browser's.
        if (_table)
        {
            try
            {
                await Js.InvokeVoidAsync("PoFx.table", false, true);
            }
            catch (Exception ex) when (ex is JSException or InvalidOperationException or TaskCanceledException)
            {
            }
        }

        _leaving.Dispose();

        // The canvas first: it holds a GL context and a frame loop, and both outlive a page that only half
        // finished tidying itself up.
        await Gfx.UnmountAsync(StageSelector, CancellationToken.None);
        await Particles.ClearAsync(StageSelector, CancellationToken.None);
        await Audio.StopAsync(CancellationToken.None);
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
    }
}
