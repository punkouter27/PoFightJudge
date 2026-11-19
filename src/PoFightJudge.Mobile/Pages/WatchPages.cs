using PoFightJudge.Mobile.Services;
using PoFightJudge.Shared.Identifiers;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Mobile.Pages;

/// <summary>What the Watch setup hands to the play screen.</summary>
public sealed class WatchDraft
{
    public ProfileDto? Husband { get; set; }
    public ProfileDto? Wife { get; set; }

    /// <summary>The seat a person takes (1P): "husband", "wife" or null for CPU vs CPU.</summary>
    public string? HumanSeat { get; set; }
    public string HumanTag { get; set; } = string.Empty;
    public string HumanName { get; set; } = string.Empty;
    public string? Topic { get; set; }
    public Stance? HusbandStance { get; set; }
}

public sealed class WatchSetupPage(ApiClient api, WatchDraft draft) : ContentPage
{
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        Title = "Watch";
        VerticalStackLayout body = Ui.Stack(10, Ui.H1("Watch a fight"), new ActivityIndicator { IsRunning = true });
        Content = new ScrollView { Content = body, Padding = 16 };
        List<ProfileDto> cast = await api.GetProfilesAsync() ?? [];
        body.Clear();
        body.Add(Ui.H1("Watch a fight"));
        if (cast.Count == 0)
        {
            body.Add(Ui.Muted("No cast to pick from — check the connection."));
            return;
        }

        static string Label(ProfileDto p) => $"{p.Persona.Name ?? p.Persona.Initials} ({p.Persona.Initials})";
        List<ProfileDto> husbands = [.. cast.Where(p => p.Persona.Role == ProfileRole.Husband).DefaultIfEmpty(cast[0])];
        List<ProfileDto> wives = [.. cast.Where(p => p.Persona.Role == ProfileRole.Wife).DefaultIfEmpty(cast[^1])];
        Picker husband = new() { Title = "Husband", ItemsSource = husbands.Select(Label).ToList(), SelectedIndex = 0 };
        Picker wife = new() { Title = "Wife", ItemsSource = wives.Select(Label).ToList(), SelectedIndex = 0 };
        Picker seat = new() { Title = "Take a seat yourself?", ItemsSource = new[] { "No — CPU vs CPU", "I'm the husband", "I'm the wife" }, SelectedIndex = 0 };
        Entry myName = new() { Placeholder = "Your name" };
        Entry myTag = new() { Placeholder = "Your tag (3 letters)", MaxLength = 3 };
        Entry topic = new() { Placeholder = "What's it about? (optional)" };
        Picker stance = new() { Title = "Sides", ItemsSource = new[] { "No sides", "Husband is FOR", "Husband is AGAINST" }, SelectedIndex = 0 };
        Label error = Ui.Colored(null, Ui.Bad, 14);

        body.Add(Ui.Field("Husband", husband));
        body.Add(Ui.Field("Wife", wife));
        body.Add(seat);
        body.Add(myName);
        body.Add(Ui.Row(myTag, Ui.Secondary("📇 Me from contacts", async () => { if (await Opponents.PickAsync() is { } me) { myName.Text = me.Name; myTag.Text = me.Tag; } })));
        body.Add(topic);
        body.Add(stance);
        body.Add(error);
        body.Add(Ui.Primary("Start", async () =>
        {
            draft.Husband = husbands[Math.Max(0, husband.SelectedIndex)];
            draft.Wife = wives[Math.Max(0, wife.SelectedIndex)];
            draft.HumanSeat = seat.SelectedIndex switch { 1 => WatchTurns.Husband, 2 => WatchTurns.Wife, _ => null };
            draft.HumanTag = (myTag.Text ?? string.Empty).Trim().ToUpperInvariant();
            draft.HumanName = myName.Text?.Trim() ?? draft.HumanTag;
            if (draft.HumanSeat is not null && !Initials.IsTag(draft.HumanTag))
            {
                error.Text = "Your tag must be exactly three letters.";
                return;
            }

            draft.Topic = string.IsNullOrWhiteSpace(topic.Text) ? null : topic.Text.Trim();
            draft.HusbandStance = draft.Topic is null ? null : stance.SelectedIndex switch { 1 => Stance.For, 2 => Stance.Against, _ => null };
            await Shell.Current.GoToAsync("../watchplay");
        }));
    }
}

/// <summary>The rounds: the cast's lines with their voices, a spoken human turn (on-device speech, item 3), one slap, then the verdict.</summary>
public sealed class WatchPlayPage(ApiClient api, WatchDraft draft, ISpeaker speaker, ISpeechToText speech, IMicrophone mic) : ContentPage, IDisposable
{
    private readonly VerticalStackLayout _lines = new() { Spacing = 8 };
    private readonly Label _status = Ui.Muted(null);
    private readonly List<WatchRoundDto> _rounds = [];
    private Button? _slap, _done;
    private CancellationTokenSource? _voice;
    private TaskCompletionSource? _turnDone;
    private bool _slapUsed, _slapNow, _afterSlap, _started;

    private MatchSide Side(string seat) =>
        draft.HumanSeat == seat ? MatchSide.Human(draft.HumanTag, draft.HumanName)
        : MatchSide.Persona((seat == WatchTurns.Husband ? draft.Husband : draft.Wife)!.Persona.Initials, (seat == WatchTurns.Husband ? draft.Husband : draft.Wife)!.Persona.Name);

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_started)
        {
            return;
        }

        _started = true;
        Title = "Watch";
        _slap = Ui.Danger("👋 SLAP (once)", () => { _slapNow = true; _voice?.Cancel(); Haptics.Heat(); _slap!.IsEnabled = false; _slapUsed = true; return Task.CompletedTask; });
        _done = Ui.Primary("I'm done talking", () => { _turnDone?.TrySetResult(); return Task.CompletedTask; });
        _done.IsVisible = false;
        Content = new ScrollView
        {
            Padding = 16,
            Content = Ui.Stack(10, Ui.H1($"{Side(WatchTurns.Husband).DisplayName} vs {Side(WatchTurns.Wife).DisplayName}"), Ui.Muted(draft.Topic ?? "Anything goes"), _status, _done, _slap, _lines),
        };

        try
        {
            await PlayAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _status.Text = "Lost the connection — the fight stopped.";
        }
    }

    private async Task PlayAsync()
    {
        while (!WatchTurns.IsComplete(_rounds.Select(r => r.Speaker)))
        {
            string seat = WatchTurns.NextSpeaker(_rounds.Count, _afterSlap);
            bool slapped = _slapNow;
            _slapNow = false;
            if (slapped)
            {
                _afterSlap = !_afterSlap;
                seat = _rounds.Count > 0 ? _rounds[^1].Speaker : seat; // the slapped speaker answers, the other's turn is skipped
            }

            Haptics.Tap();
            WatchRoundDto line = draft.HumanSeat == seat ? await HumanTurnAsync(seat) : await CastTurnAsync(seat, slapped);
            _rounds.Add(line);
        }

        _status.Text = "The judge is deciding…";
        _slap!.IsVisible = false;
        if (await api.VerdictAsync(new VerdictRequest(Side(WatchTurns.Husband), Side(WatchTurns.Wife), _rounds, draft.Topic, null, draft.HusbandStance)) is { } verdict)
        {
            string winner = WatchTurns.IsHusband(verdict.Winner) ? Side(WatchTurns.Husband).DisplayName : Side(WatchTurns.Wife).DisplayName;
            _status.Text = null;
            _lines.Insert(0, Ui.Card(Ui.Stack(4,
                new Label { Text = $"🏆 {winner}", FontSize = 26, FontAttributes = FontAttributes.Bold },
                Ui.Muted($"{verdict.HusbandScore} – {verdict.WifeScore}"),
                Ui.Text(verdict.Verdict))));
            await Haptics.VerdictAsync();
        }
        else
        {
            _status.Text = "No verdict — try again.";
        }
    }

    private async Task<WatchRoundDto> CastTurnAsync(string seat, bool slapped)
    {
        _status.Text = $"{Side(seat).DisplayName} is thinking…";
        GenerateRoundResponse said = await api.GenerateRoundAsync(new GenerateRoundRequest(
            Side(WatchTurns.Husband), Side(WatchTurns.Wife), _rounds, seat, draft.Topic, slapped ? Interjections.SlapKey : null, slapped, draft.HusbandStance))
            ?? throw new HttpRequestException("No line came back.");
        WatchRoundDto line = new(seat, said.Text, WatchTurns.NormalizeMood(said.Mood));
        AddLine(line);
        _status.Text = $"{Side(seat).DisplayName} ({line.Mood})";
        if (said.Mood is "furious" or "hateful" or "hostile")
        {
            Haptics.Heat();
        }

        ProfileDto profile = (seat == WatchTurns.Husband ? draft.Husband : draft.Wife)!;
        _voice = new CancellationTokenSource();
        try
        {
            await foreach (RoundAudioChunkDto chunk in api.RoundAudioAsync(new RoundAudioRequest(profile.Id, said.Text), _voice.Token))
            {
                await speaker.PlayClipAsync(Convert.FromBase64String(chunk.Base64), chunk.Format, string.Equals(chunk.Format, "mp3", StringComparison.OrdinalIgnoreCase) ? 44_100 : 24_000, _voice.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }

        return line;
    }

    /// <summary>The person's spoken turn: on-device speech recognition when the phone has it, otherwise recorded and read by the server.</summary>
    private async Task<WatchRoundDto> HumanTurnAsync(string seat)
    {
        _turnDone = new TaskCompletionSource();
        _done!.IsVisible = true;
        _slap!.IsEnabled = false;
        Label live = Ui.Text("…", 15);
        _lines.Insert(0, Ui.Card(Ui.Stack(2, Ui.H2($"🎤 {draft.HumanName}, your turn"), live)));
        string text;
        if (speech.IsAvailable && await speech.StartAsync(t => MainThread.BeginInvokeOnMainThread(() => live.Text = t)))
        {
            _status.Text = speech.OnDevice ? "Listening — on this phone, nothing is sent until you're done." : "Listening…";
            await Task.WhenAny(_turnDone.Task, Task.Delay(TimeSpan.FromSeconds(90)));
            text = await speech.StopAsync();
        }
        else
        {
            // No recognizer: record the turn and let the server's transcriber read it.
            _status.Text = "Recording your turn…";
            using MemoryStream pcm = new();
            await mic.StartAsync(frame => { lock (pcm) { pcm.Write(frame); } }, voiceProcessing: false);
            await Task.WhenAny(_turnDone.Task, Task.Delay(TimeSpan.FromSeconds(90)));
            mic.Stop();
            _status.Text = "Reading it…";
            text = (await api.TranscribeAsync(Wav.Build(pcm.ToArray())))?.Text ?? string.Empty;
        }

        _done.IsVisible = false;
        _slap.IsEnabled = !_slapUsed;
        _lines.RemoveAt(0);
        WatchRoundDto line = new(seat, string.IsNullOrWhiteSpace(text) ? "(says nothing)" : text, "angry");
        AddLine(line);
        return line;
    }

    public void Dispose() => _voice?.Dispose();

    private void AddLine(WatchRoundDto line) =>
        _lines.Insert(0, Ui.Card(Ui.Stack(2, Ui.H2($"{Side(line.Speaker).DisplayName} · {line.Mood}"), Ui.Text(WatchTurns.Shown(line.Text), 15))));
}
