using PoFightJudge.Mobile.Services;

namespace PoFightJudge.Mobile.Pages;

public partial class HistoryPage : ContentPage
{
    private readonly PoFightJudgeClient _client;

    public HistoryPage(PoFightJudgeClient client)
    {
        _client = client;
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await RefreshAsync();
    }

    private async void OnRefreshClicked(object? sender, EventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        MatchesList.Clear();
        LeaderboardList.Clear();
        try
        {
            var matches = await _client.GetMatchesAsync();
            StatusLabel.Text = $"Loaded {matches.Count} match(es) · Network: {Connectivity.Current.NetworkAccess}";
            foreach (var match in matches.Take(10))
            {
                var shareBtn = new Button { Text = "Share Verdict" };
                shareBtn.Clicked += async (_, _) => await Share.Default.RequestAsync(new ShareTextRequest
                {
                    Title = $"PoFightJudge: {match.Topic}",
                    Text = $"{match.Side1.DisplayName} vs {match.Side2.DisplayName} — Winner: {match.Winner}\n{match.Verdict}",
                });
                MatchesList.Add(new Label { Text = $"{match.Side1.DisplayName} vs {match.Side2.DisplayName} · Winner: {match.Winner} · {match.Topic}" });
                MatchesList.Add(shareBtn);
            }

            if (Connectivity.Current.NetworkAccess == NetworkAccess.Internet)
            {
                var board = await _client.GetLeaderboardAsync();
                foreach (var row in board.Take(10))
                {
                    LeaderboardList.Add(new Label { Text = $"{row.DisplayName}: Elo {row.Rating} ({row.Wins}/{row.Matches} wins)" });
                }
            }
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Load failed: {ex.Message}";
        }
    }
}
