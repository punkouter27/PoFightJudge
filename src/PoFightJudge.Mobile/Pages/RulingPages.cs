using PoFightJudge.Mobile.Services;
using PoFightJudge.Shared.Identifiers;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Mobile.Pages;

/// <summary>A fight's ruling: waits for the analysis, reveals the winner (with a buzz), and sends it to the opponent (item 8).</summary>
public sealed class VerdictPage(ApiClient api, FightDraft draft) : ContentPage, IQueryAttributable
{
    private readonly VerticalStackLayout _body = new() { Spacing = 10, Padding = 16 };
    private MatchId _id;
    private bool _revealed;

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("id", out object? raw) && raw is string s && s.Length > 0)
        {
            _id = MatchId.From(s);
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        Title = "Ruling";
        Content = new ScrollView { Content = _body };
        _body.Add(Ui.H1("The judge is reading it…"));
        _body.Add(new ActivityIndicator { IsRunning = true });
        for (int attempt = 0; attempt < 120 && IsVisibleNow; attempt++)
        {
            AnalysisResponse? analysis = await api.GetAnalysisAsync(_id);
            if (analysis is { Status: AnalysisStatus.Ready, Report: { } report })
            {
                await ShowAsync(report);
                return;
            }

            if (analysis?.Status == AnalysisStatus.Failed)
            {
                _body.Clear();
                _body.Add(Ui.H1("The reading failed"));
                _body.Add(Ui.Colored(analysis.Error, Ui.Bad));
                _body.Add(Ui.Secondary("Try again", async () => { await api.RetryAnalysisAsync(_id); OnAppearing(); }));
                return;
            }

            ((Label)_body[0]).Text = $"The judge is reading it… ({analysis?.Status.ToString() ?? "waiting"})";
            await Task.Delay(attempt < 10 ? 2000 : 5000);
        }
    }

    private bool IsVisibleNow => Shell.Current?.CurrentPage == this;

    private async Task ShowAsync(AnalysisReportDto report)
    {
        _body.Clear();
        OverallVerdictDto o = report.Overall;
        string winner = !o.Ruled ? "No ruling — not enough was said" : o.Overall == Speaker.Player1 ? report.Player1.Name : report.Player2.Name;
        _body.Add(new Label { Text = o.Ruled ? $"🏆 {winner}" : winner, FontSize = 30, FontAttributes = FontAttributes.Bold, LineBreakMode = LineBreakMode.WordWrap });
        _body.Add(Ui.Muted(report.Topic));
        _body.Add(Ui.Text(o.Summary));
        foreach (string reason in o.Reasons)
        {
            _body.Add(Ui.Text($"• {reason}", 14));
        }

        if (o.Ruled)
        {
            _body.Add(Ui.Muted($"Better logic: {Name(report, o.WinnerLogic)} · more often right: {Name(report, o.WinnerCorrect)}"));
        }

        foreach (PlayerReportDto p in new[] { report.Player1, report.Player2 })
        {
            PlayerMetricsDto m = p.Metrics;
            _body.Add(Ui.Card(Ui.Stack(4,
                Ui.H2(p.Name),
                Ui.Text($"{m.TalkShare:P0} of the talking · {m.Words} words · {m.WordsPerMinute:F0} wpm · {m.Turns} turns"),
                Ui.Muted($"Logic {p.Assessment.Logic}/10 · persuasion {p.Assessment.Persuasiveness}/10 · right {p.Assessment.CorrectnessPercent}% · {p.Assessment.Fallacies.Count} fallacies"))));
        }

        foreach (HighlightDto h in report.Highlights ?? [])
        {
            _body.Add(Ui.Muted($"{h.Label}: \"{h.Quote}\""));
        }

        if (report.SpeakerMappingFlagged)
        {
            _body.Add(Ui.Colored($"Who-said-what is a best guess: {report.SpeakerMappingNote}", Ui.Warn, 12));
        }

        _body.Add(Ui.Primary(draft.Opponent is { } o2 ? $"Send the ruling to {o2.Name}" : "Share the ruling", async () =>
        {
            if (await api.ShareAsync(_id) is not { } share)
            {
                await Ui.Toast("Couldn't make a link.");
                return;
            }

            await Opponents.SendRulingAsync(draft.Opponent, api.U(share.Path).ToString(), o.Ruled ? $"The judge ruled for {winner}." : "The judge couldn't rule on our fight.");
        }));
        if (!_revealed)
        {
            _revealed = true;
            await Haptics.VerdictAsync();
        }
    }

    private static string Name(AnalysisReportDto r, Speaker s) => s == Speaker.Player1 ? r.Player1.Name : r.Player2.Name;
}

/// <summary>A ruling someone shared: opened from its link (https://…/v/{token}) without needing their account.</summary>
public sealed class SharedRulingPage(ApiClient api) : ContentPage, IQueryAttributable
{
    private string _token = string.Empty;

    public void ApplyQueryAttributes(IDictionary<string, object> query) => _token = query.TryGetValue("token", out object? t) ? t as string ?? string.Empty : string.Empty;

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        Title = "Shared ruling";
        VerticalStackLayout body = Ui.Stack(10);
        Content = new ScrollView { Content = body, Padding = 16 };
        if (await api.GetSharedAsync(_token) is not { } shared)
        {
            body.Add(Ui.H1("This link has expired or was taken back."));
            return;
        }

        body.Add(Ui.H1($"{shared.Side1Name} vs {shared.Side2Name}"));
        body.Add(Ui.Muted($"{shared.Topic} · {shared.StartedAt.LocalDateTime:g}"));
        body.Add(new Label { Text = shared.IsDraw ? "No winner" : $"🏆 {(shared.Winner.Equals(shared.Side1Name, StringComparison.OrdinalIgnoreCase) ? shared.Side1Name : shared.Winner)}", FontSize = 26, FontAttributes = FontAttributes.Bold });
        body.Add(Ui.Text(shared.Verdict));
        foreach (string reason in shared.Reasons)
        {
            body.Add(Ui.Text($"• {reason}", 14));
        }

        await Haptics.VerdictAsync();
    }
}

/// <summary>Every fight and watch, newest first; tap to read, swipe to delete.</summary>
public sealed class HistoryPage(ApiClient api) : ContentPage
{
    private const int Take = 25;
    private readonly VerticalStackLayout _list = new() { Spacing = 8 };
    private int _skip;

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        Title = "History";
        _skip = 0;
        _list.Clear();
        Content = new ScrollView { Content = Ui.Stack(10, Ui.H1("History"), _list), Padding = 16 };
        await MoreAsync();
    }

    private async Task MoreAsync()
    {
        if (_list.Count > 0 && _list[^1] is Button)
        {
            _list.RemoveAt(_list.Count - 1);
        }

        if (await api.ListMatchesAsync(_skip, Take) is not { } page)
        {
            _list.Add(Ui.Muted(ApiClient.Online ? "Couldn't load." : "You're offline."));
            return;
        }

        foreach (MatchDto m in page.Matches)
        {
            string winner = string.IsNullOrEmpty(m.Winner) ? (m.Status == SessionStatus.Ready ? "no winner" : m.Status.ToString().ToLowerInvariant()) : $"won by {(m.Winner == m.Side1.Id ? m.Side1.DisplayName : m.Side2.DisplayName)}";
            Border card = Ui.Card(Ui.Stack(3,
                new Label { Text = $"{(m.Mode == MatchMode.Fight ? "🥊" : "🍿")} {m.Side1.DisplayName} vs {m.Side2.DisplayName}", FontAttributes = FontAttributes.Bold },
                Ui.Muted($"{m.Topic} · {m.StartedAt.LocalDateTime:g} · {winner}")),
                () => m.Mode == MatchMode.Fight ? Shell.Current.GoToAsync($"verdict?id={m.Id.Value}") : Shell.Current.GoToAsync($"watchresult?id={m.Id.Value}"));
            SwipeView swipe = new() { Content = card };
            swipe.RightItems = [new SwipeItem { Text = "Delete", BackgroundColor = Ui.Bad, Command = new Command(async () => { if (await api.DeleteMatchAsync(m.Id)) { _list.Remove(swipe); } }) }];
            _list.Add(swipe);
        }

        _skip += page.Matches.Count;
        if (_skip < page.Total)
        {
            _list.Add(Ui.Secondary("More", MoreAsync));
        }
        else if (page.Total == 0)
        {
            _list.Add(Ui.Muted("Nothing yet — start on Play."));
        }
    }
}

/// <summary>A finished watch: its lines and the stored verdict.</summary>
public sealed class WatchResultPage(ApiClient api) : ContentPage, IQueryAttributable
{
    private MatchId _id;

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("id", out object? raw) && raw is string s)
        {
            _id = MatchId.From(s);
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        VerticalStackLayout body = Ui.Stack(8);
        Content = new ScrollView { Content = body, Padding = 16 };
        Task<MatchDto?> match = api.GetMatchAsync(_id);
        Task<List<TurnDto>?> turns = api.GetTurnsAsync(_id);
        if (await match is not { } m)
        {
            body.Add(Ui.Muted("Not found."));
            return;
        }

        Title = $"{m.Side1.DisplayName} vs {m.Side2.DisplayName}";
        body.Add(Ui.H1(Title));
        body.Add(Ui.Muted(m.Topic));
        body.Add(Ui.Text(m.Verdict));
        foreach (TurnDto t in (await turns ?? []).Where(t => t.Text.Length > 0))
        {
            body.Add(Ui.Text($"{(t.Speaker == Speaker.Player1 ? m.Side1.DisplayName : m.Side2.DisplayName)} ({t.Mood}): {WatchTurns.Shown(t.Text)}", 14));
        }
    }
}

/// <summary>One board: this account's people and the cast on one Elo scale.</summary>
public sealed class LeaderboardPage(ApiClient api) : ContentPage
{
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        Title = "Leaderboard";
        VerticalStackLayout body = Ui.Stack(6, Ui.H1("Leaderboard"));
        Content = new ScrollView { Content = body, Padding = 16 };
        int rank = 0;
        foreach (LeaderboardRowDto row in (await api.GetLeaderboardAsync() ?? []).OrderByDescending(r => r.Rating))
        {
            body.Add(Ui.Field($"#{++rank} {row.DisplayName}{(row.IsPerson ? string.Empty : " (cast)")}", Ui.Text($"{row.Rating} · {row.Wins}/{row.Matches} · {row.WinRate:P0}", 14)));
        }
    }
}
