using PoFightJudge.Mobile.Services;

namespace PoFightJudge.Mobile.Pages;

public partial class SettingsPage : ContentPage
{
    private readonly MobileConfig _config;
    private readonly SecureSessionStore _store;
    private readonly PoFightJudgeClient _client;

    public SettingsPage(MobileConfig config, SecureSessionStore store, PoFightJudgeClient client)
    {
        _config = config;
        _store = store;
        _client = client;
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        BaseUrlEntry.Text = _config.BaseUrl;
        FakeUserEntry.Text = await _store.GetFakeUserAsync();
        TokenEntry.Text = await _store.GetBearerTokenAsync();
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        _config.BaseUrl = BaseUrlEntry.Text;
        await _store.SetFakeUserAsync(FakeUserEntry.Text);
        await _store.SetBearerTokenAsync(TokenEntry.Text);
        ResultLabel.Text = $"Saved settings for {_config.BaseUrl}";
    }

    private async void OnTestClicked(object? sender, EventArgs e)
    {
        try
        {
            var me = await _client.GetAuthMeAsync();
            ResultLabel.Text = me is { Authenticated: true }
                ? $"Authenticated as {me.Name} ({me.AuthType})"
                : "Connected, anonymous.";
        }
        catch (Exception ex)
        {
            ResultLabel.Text = $"Error: {ex.Message}";
        }
    }
}
