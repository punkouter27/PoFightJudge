using PoFightJudge.Mobile.Services;

namespace PoFightJudge.Mobile;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
        => new(new AppShell());

    /// <summary>A shared ruling's link (https://…/v/{token} or pofightjudge://v/{token}) opens it in the app (item 8).</summary>
    protected override void OnAppLinkRequestReceived(Uri uri)
    {
        base.OnAppLinkRequestReceived(uri);
        string[] parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? token = uri.Scheme == MobileConfig.AppScheme && uri.Host == "v" && parts.Length == 1 ? parts[0]
            : uri.Scheme == Uri.UriSchemeHttps && parts is ["v", var t] ? t
            : null;
        if (token is null)
        {
            return;
        }

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            for (int i = 0; Shell.Current is null && i < 50; i++)
            {
                await Task.Delay(100);
            }

            if (Shell.Current is { } shell)
            {
                await shell.GoToAsync($"shared?token={Uri.EscapeDataString(token)}");
            }
        });
    }
}
