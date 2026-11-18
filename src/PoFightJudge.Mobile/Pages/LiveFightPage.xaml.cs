using PoFightJudge.Mobile.Services;
using PoFightJudge.Shared.Identifiers;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Mobile.Pages;

public partial class LiveFightPage : ContentPage
{
    private readonly PoFightJudgeClient _client;
    private MatchId? _activeMatchId;

    public LiveFightPage(PoFightJudgeClient client)
    {
        _client = client;
        InitializeComponent();
        _client.CaptionReceived += OnCaptionReceived;
        _client.HubNotice += OnHubNotice;
    }

    private void OnCaptionReceived(CaptionDto caption)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (WakeSwitch.IsToggled && caption.Final && HapticFeedback.Default.IsSupported)
            {
                HapticFeedback.Default.Perform(HapticFeedbackType.Click);
            }

            CaptionsList.Add(new Label { Text = $"[{caption.Speaker}] {caption.Text}" });
        });
    }

    private void OnHubNotice(string notice)
        => MainThread.BeginInvokeOnMainThread(() => StatusLabel.Text = notice);

    private async void OnMicCheckClicked(object? sender, EventArgs e)
    {
        var status = await Permissions.CheckStatusAsync<Permissions.Microphone>();
        if (status != PermissionStatus.Granted)
        {
            status = await Permissions.RequestAsync<Permissions.Microphone>();
        }

        StatusLabel.Text = $"Microphone permission: {status}";
    }

    private async void OnStartFightClicked(object? sender, EventArgs e)
    {
        try
        {
            StartFightButton.IsEnabled = false;
            var matchId = await _client.StartFightAsync(
                Player1Entry.Text ?? "MAT",
                Player2Entry.Text ?? "JIL",
                TopicEntry.Text ?? "Household chores");

            _activeMatchId = matchId;
            DeviceDisplay.Current.KeepScreenOn = WakeSwitch.IsToggled;
            EndFightButton.IsEnabled = true;
            CaptionsList.Clear();
            StatusLabel.Text = $"Fight live: {matchId.Value}";
            await _client.ConnectAndJoinFightAsync(matchId);
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Start failed: {ex.Message}";
            StartFightButton.IsEnabled = true;
        }
    }

    private async void OnEndFightClicked(object? sender, EventArgs e)
    {
        if (_activeMatchId is not { } id)
        {
            return;
        }

        try
        {
            await _client.EndFightAsync(id);
            StatusLabel.Text = $"Fight ended ({id.Value}). Verdict analyzing...";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"End failed: {ex.Message}";
        }
        finally
        {
            _activeMatchId = null;
            DeviceDisplay.Current.KeepScreenOn = false;
            StartFightButton.IsEnabled = true;
            EndFightButton.IsEnabled = false;
        }
    }
}
