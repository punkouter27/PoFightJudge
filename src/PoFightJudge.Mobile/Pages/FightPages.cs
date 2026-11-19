using PoFightJudge.Mobile.Services;
using PoFightJudge.Shared.Identifiers;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Mobile.Pages;

/// <summary>What the setup screens hand to the fight screens.</summary>
public sealed class FightDraft
{
    public string P1Name { get; set; } = string.Empty;
    public string P1Tag { get; set; } = string.Empty;
    public string P2Name { get; set; } = string.Empty;
    public string P2Tag { get; set; } = string.Empty;
    public string? Topic { get; set; }
    public Stance? P1Stance { get; set; }
    public HostPersonaId Persona { get; set; } = HostPersonaId.Referee;
    public Opponents.Picked? Opponent { get; set; }
    public DateTimeOffset StartedAt { get; set; }
}

/// <summary>Home: start a live fight, record one offline, watch the cast argue, and anything still waiting to upload.</summary>
public sealed class PlayPage : ContentPage
{
    private readonly Session _session;
    private readonly RecordedFights _recorded;
    private readonly VerticalStackLayout _body = new() { Spacing = 12, Padding = 16 };

    public PlayPage(Session session, RecordedFights recorded)
    {
        _session = session;
        _recorded = recorded;
        Title = "Play";
        Content = new ScrollView { Content = _body };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (!await _session.RefreshAsync())
        {
            await Shell.Current.GoToAsync("login");
            return;
        }

        List<MatchId> uploaded = await _recorded.UploadWaitingAsync();
        Render(uploaded);
    }

    private void Render(List<MatchId> justUploaded)
    {
        _body.Clear();
        _body.Add(Ui.H1("PoFightJudge"));
        _body.Add(Ui.Card(Ui.Stack(8,
            Ui.H2("FIGHT — two of you, one phone"),
            Ui.Muted("An AI host introduces you, cuts in and rules; afterwards the recording is judged."),
            Ui.Primary("Live fight with a host", () => Shell.Current.GoToAsync("setup?mode=live")),
            Ui.Secondary("Record offline (judged when you're back online)", () => Shell.Current.GoToAsync("setup?mode=record")))));
        _body.Add(Ui.Card(Ui.Stack(8,
            Ui.H2("WATCH — the cast argues"),
            Ui.Muted("Pick a husband and a wife from the cast, or take a seat yourself and argue out loud."),
            Ui.Primary("Watch a fight", () => Shell.Current.GoToAsync("watchsetup")))));
        _body.Add(Ui.Secondary("Leaderboard", () => Shell.Current.GoToAsync("leaderboard")));

        foreach (MatchId id in justUploaded)
        {
            _body.Add(Ui.Card(Ui.Stack(4, Ui.Colored("An offline fight was uploaded and is being judged.", Ui.Good), Ui.Secondary("See the ruling", () => Shell.Current.GoToAsync($"verdict?id={id.Value}")))));
        }

        IReadOnlyList<PendingFight> waiting = RecordedFights.Waiting();
        if (waiting.Count > 0)
        {
            VerticalStackLayout box = Ui.Stack(6, Ui.H2("Waiting to upload"));
            foreach (PendingFight fight in waiting)
            {
                box.Add(Ui.Text($"{fight.Meta.Player1Tag} vs {fight.Meta.Player2Tag} · {fight.Meta.RecordedAt.LocalDateTime:g}"));
                if (fight.Problem is { } problem)
                {
                    box.Add(Ui.Colored(problem, Ui.Bad));
                }

                box.Add(Ui.Danger("Discard", async () => { RecordedFights.Discard(fight); Render([]); await Task.CompletedTask; }));
            }

            box.Add(Ui.Secondary("Upload now", async () => Render(await _recorded.UploadWaitingAsync())));
            _body.Add(Ui.Card(box));
        }
    }
}

/// <summary>Who is fighting about what. Either fighter can be picked from contacts (item 8).</summary>
public sealed class FightSetupPage(FightDraft draft) : ContentPage, IQueryAttributable
{
    private bool _record;

    public void ApplyQueryAttributes(IDictionary<string, object> query) => _record = query.TryGetValue("mode", out object? m) && m as string == "record";

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Title = _record ? "Record a fight" : "Live fight";
        Entry p1Name = new() { Placeholder = "Fighter one's name", Text = draft.P1Name };
        Entry p1Tag = new() { Placeholder = "Tag (3 letters)", MaxLength = 3, Text = draft.P1Tag };
        Entry p2Name = new() { Placeholder = "Fighter two's name", Text = draft.P2Name };
        Entry p2Tag = new() { Placeholder = "Tag (3 letters)", MaxLength = 3, Text = draft.P2Tag };
        Entry topic = new() { Placeholder = "What's it about? (optional)", Text = draft.Topic };
        Picker stance = new() { Title = "Sides", ItemsSource = new[] { "No sides", "Fighter one is FOR", "Fighter one is AGAINST" }, SelectedIndex = draft.P1Stance switch { Stance.For => 1, Stance.Against => 2, _ => 0 } };
        Picker host = new() { Title = "Host", ItemsSource = HostPersonaCatalogue.All.Select(h => $"{h.Emoji} {h.Name} — {h.Tagline}").ToList(), SelectedIndex = Math.Max(0, HostPersonaCatalogue.All.ToList().FindIndex(h => h.Id == draft.Persona)) };
        Label error = Ui.Colored(null, Ui.Bad, 14);

        async Task PickAsync(Entry name, Entry tag, bool opponent)
        {
            if (await Opponents.PickAsync() is { } picked)
            {
                name.Text = picked.Name;
                tag.Text = picked.Tag;
                if (opponent)
                {
                    draft.Opponent = picked;
                }
            }
        }

        VerticalStackLayout body = Ui.Stack(10,
            Ui.H1(Title),
            Ui.H2("Fighter one"), p1Name, Ui.Row(p1Tag, Ui.Secondary("📇 From contacts", () => PickAsync(p1Name, p1Tag, false))),
            Ui.H2("Fighter two"), p2Name, Ui.Row(p2Tag, Ui.Secondary("📇 From contacts", () => PickAsync(p2Name, p2Tag, true))),
            Ui.Muted("Picked from contacts, fighter two can be sent the ruling when it's ready."),
            Ui.H2("Topic"), topic, stance);
        if (!_record)
        {
            body.Add(Ui.H2("Host"));
            body.Add(host);
        }
        else
        {
            body.Add(Ui.Muted("No host offline: you tap whose turn it is while you argue, and the judge reads it later."));
        }

        body.Add(error);
        body.Add(Ui.Primary(_record ? "Start recording" : "Start the fight", async () =>
        {
            string t1 = (p1Tag.Text ?? string.Empty).Trim().ToUpperInvariant(), t2 = (p2Tag.Text ?? string.Empty).Trim().ToUpperInvariant();
            if (!Initials.IsTag(t1) || !Initials.IsTag(t2) || t1 == t2)
            {
                error.Text = "Both fighters need their own tag: exactly three letters, and not the same as each other.";
                return;
            }

            draft.P1Name = p1Name.Text?.Trim() ?? t1;
            draft.P1Tag = t1;
            draft.P2Name = p2Name.Text?.Trim() ?? t2;
            draft.P2Tag = t2;
            draft.Topic = string.IsNullOrWhiteSpace(topic.Text) ? null : topic.Text.Trim();
            draft.P1Stance = draft.Topic is null ? null : stance.SelectedIndex switch { 1 => Stance.For, 2 => Stance.Against, _ => null };
            draft.Persona = HostPersonaCatalogue.All[Math.Max(0, host.SelectedIndex)].Id;
            draft.StartedAt = DateTimeOffset.Now;
            Haptics.Tap();
            await Shell.Current.GoToAsync(_record ? "../record" : "../live");
        }));
        Content = new ScrollView { Content = body, Padding = 16 };
    }
}

/// <summary>The live show: the host's voice, captions, whose turn it is, the clock; keeps going with the screen locked.</summary>
public sealed class LiveFightPage(ApiClient api, LiveFight fight, FightDraft draft) : ContentPage
{
    private readonly Label _phase = Ui.H2("Connecting…");
    private readonly Label _clock = Ui.Muted(null, 15);
    private readonly Label _speaking = Ui.Text(null, 18);
    private readonly VerticalStackLayout _captions = new() { Spacing = 4 };
    private SessionPhase? _lastPhase;
    private bool _started;

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_started)
        {
            return;
        }

        _started = true;
        Title = "Live";
        Content = new ScrollView
        {
            Padding = 16,
            Content = Ui.Stack(10,
                Ui.H1($"{draft.P1Name} vs {draft.P2Name}"),
                Ui.Muted(draft.Topic ?? "No topic — the host will ask."),
                _phase, _clock, _speaking,
                Ui.Danger("End the fight", EndAsync),
                Ui.Card(_captions)),
        };

        (MatchId? id, string? problem) = await api.StartFightAsync(new CreateFightRequest(draft.Persona, draft.P1Tag, draft.P2Tag, draft.Topic, ProfileRole.Husband, ProfileRole.Wife, draft.P1Stance));
        if (id is null)
        {
            _phase.Text = problem;
            return;
        }

        fight.Caption += OnCaption;
        fight.Snapshot += OnSnapshot;
        fight.Interrupted += OnInterrupted;
        fight.Ended += OnEnded;
        fight.Problem += OnProblem;
        if (await fight.StartAsync(id.Value) is { } failed)
        {
            _phase.Text = failed;
        }
    }

    private void OnCaption(CaptionDto c) => MainThread.BeginInvokeOnMainThread(() =>
    {
        if (!c.Final)
        {
            return;
        }

        string who = c.Speaker switch { Speaker.Host => "🎙 Host", Speaker.Player1 => draft.P1Name, _ => draft.P2Name };
        _captions.Insert(0, Ui.Text($"{who}: {c.Text}", 14));
        while (_captions.Count > 40)
        {
            _captions.RemoveAt(_captions.Count - 1);
        }
    });

    private void OnSnapshot(DebateSnapshotDto s) => MainThread.BeginInvokeOnMainThread(async () =>
    {
        _phase.Text = s.Phase.ToString();
        _clock.Text = s.DebateRemainingSeconds > 0 ? $"{s.DebateRemainingSeconds / 60}:{s.DebateRemainingSeconds % 60:00} left · {s.DebateElapsedSeconds / 60}:{s.DebateElapsedSeconds % 60:00} in" : null;
        _speaking.Text = s.Speaking switch { Speaker.Player1 => $"🎤 {draft.P1Name}", Speaker.Player2 => $"🎤 {draft.P2Name}", Speaker.Host => "🎙 The host", _ => null };
        if (s.Phase != _lastPhase)
        {
            _lastPhase = s.Phase;
            if (s.Phase == SessionPhase.Verdict)
            {
                await Haptics.VerdictAsync();
            }
            else
            {
                Haptics.Tap();
            }
        }
    });

    private void OnInterrupted() => MainThread.BeginInvokeOnMainThread(() => _speaking.Text = "✋ The host cuts in");

    private void OnProblem(string message) => MainThread.BeginInvokeOnMainThread(() => _phase.Text = message);

    private void OnEnded() => MainThread.BeginInvokeOnMainThread(async () =>
    {
        await fight.StopLocalAsync();
        Detach();
        if (fight.Id is { } id)
        {
            await Shell.Current.GoToAsync($"../verdict?id={id.Value}");
        }
    });

    private async Task EndAsync()
    {
        if (await DisplayAlertAsync("End the fight?", "The host stops and the recording is judged.", "End it", "Keep going"))
        {
            await fight.EndAsync();
            OnEnded();
        }
    }

    private void Detach()
    {
        fight.Caption -= OnCaption;
        fight.Snapshot -= OnSnapshot;
        fight.Interrupted -= OnInterrupted;
        fight.Ended -= OnEnded;
        fight.Problem -= OnProblem;
    }
}

/// <summary>Item 2: record now, upload later. Tap whose turn it is; Stop saves it on the phone.</summary>
public sealed class RecordFightPage(RecordedFights recorder, FightDraft draft) : ContentPage
{
    private readonly Label _time = new() { FontSize = 40, FontAttributes = FontAttributes.Bold, HorizontalOptions = LayoutOptions.Center };
    private readonly Label _status = Ui.Muted(null);
    private Button? _p1, _p2;
    private IDispatcherTimer? _timer;

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (recorder.Recording)
        {
            return;
        }

        Title = "Recording";
        _p1 = new Button { Text = $"🎤 {draft.P1Name} has the floor", HeightRequest = 110, FontSize = 18 };
        _p2 = new Button { Text = $"🎤 {draft.P2Name} has the floor", HeightRequest = 110, FontSize = 18 };
        _p1.Clicked += (_, _) => { recorder.Switch(Speaker.Player1); Paint(); };
        _p2.Clicked += (_, _) => { recorder.Switch(Speaker.Player2); Paint(); };
        Content = new ScrollView
        {
            Padding = 16,
            Content = Ui.Stack(14,
                Ui.H1($"{draft.P1Name} vs {draft.P2Name}"),
                Ui.Muted("Tap whoever is talking. It keeps recording with the screen locked; nothing is sent until you're online."),
                _time, _p1, _p2, _status,
                Ui.Danger("Stop and save", StopAsync)),
        };

        if (await recorder.StartAsync() is { } problem)
        {
            _status.Text = problem;
            return;
        }

        Paint();
        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => _time.Text = TimeSpan.FromSeconds(recorder.Seconds).ToString(@"m\:ss", System.Globalization.CultureInfo.InvariantCulture);
        _timer.Start();
    }

    private void Paint()
    {
        bool first = recorder.Floor == Speaker.Player1;
        _p1!.BackgroundColor = first ? Ui.Accent : Colors.Gray.WithAlpha(0.3f);
        _p2!.BackgroundColor = first ? Colors.Gray.WithAlpha(0.3f) : Ui.Accent;
        _p1.TextColor = _p2.TextColor = Colors.White;
    }

    private async Task StopAsync()
    {
        _timer?.Stop();
        if (await recorder.StopAsync(draft.P1Tag, draft.P2Tag, draft.P1Name, draft.P2Name, draft.Topic, draft.P1Stance, draft.StartedAt) is not { } saved)
        {
            return;
        }

        if (saved.Meta.Turns.Sum(t => t.EndSeconds - t.StartSeconds) < 10)
        {
            _status.Text = "Saved — but it's very short, so the judge may have nothing to rule on.";
        }

        List<MatchId> uploaded = await recorder.UploadWaitingAsync();
        if (uploaded.Contains(saved.Meta.MatchId))
        {
            await Shell.Current.GoToAsync($"../verdict?id={saved.Meta.MatchId.Value}");
            return;
        }

        await DisplayAlertAsync("Saved on the phone", "It uploads for judging as soon as you're online (open the app, or tap Upload now on Play).", "OK");
        await Shell.Current.GoToAsync("..");
    }
}
