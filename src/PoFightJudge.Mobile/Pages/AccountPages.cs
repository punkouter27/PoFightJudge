using PoFightJudge.Mobile.Services;
using PoFightJudge.Shared.Diagnostics;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Mobile.Pages;

/// <summary>Sign in: Microsoft (Entra PKCE → bearer, also used on the live hub) or, in Debug builds, the API's guest cookie.</summary>
public sealed class LoginPage(ApiClient api, Session session, EntraAuthService entra, MobileConfig config, CookieJar cookies) : ContentPage
{
    private readonly Label _status = Ui.Muted(null);

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Title = "Sign in";
        Shell.SetTabBarIsVisible(this, false);
        VerticalStackLayout body = Ui.Stack(14,
            Ui.H1("PoFightJudge"),
            Ui.Text("Have the argument. Get judged."),
            Ui.Primary("Continue with Microsoft", MicrosoftAsync));
        if (MobileConfig.GuestAllowed)
        {
            body.Add(Ui.Secondary("Continue as guest (dev)", GuestAsync));
        }

        body.Add(_status);
        Content = new ScrollView { Content = body, Padding = 20 };
    }

    private async Task MicrosoftAsync()
    {
        if (string.IsNullOrWhiteSpace(config.EntraClientId))
        {
            _status.Text = "Microsoft sign-in needs the app's Entra client id — set it under Settings → Server.";
            return;
        }

        try
        {
            cookies.Clear();
            await entra.SignInWithPkceAsync();
            await FinishAsync();
        }
        catch (TaskCanceledException)
        {
            _status.Text = "Sign-in was cancelled.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Sign-in failed: {ex.Message}";
        }
    }

    private async Task GuestAsync()
    {
        if (!await api.GuestLoginAsync())
        {
            _status.Text = $"Guest sign-in failed — is the API at {config.BaseUrl} running in Development?";
            return;
        }

        await cookies.SaveAsync();
        await FinishAsync();
    }

    private async Task FinishAsync()
    {
        if (await session.RefreshAsync() && session.SignedIn)
        {
            await Shell.Current.GoToAsync("..");
        }
        else
        {
            _status.Text = "Signed in, but the server didn't accept the session.";
        }
    }
}

public sealed class SettingsPage(ApiClient api, Session session, MobileConfig config, EntraAuthService entra, CookieJar cookies, ISpeechToText speech) : ContentPage
{
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        Title = "Settings";
        VerticalStackLayout body = Ui.Stack(10, Ui.H1("Settings"));
        Content = new ScrollView { Content = body, Padding = 16 };
        bool signedIn = await session.RefreshAsync() && session.SignedIn;
        body.Add(Ui.Card(Ui.Stack(6,
            Ui.H2("Account"),
            Ui.Text(signedIn ? session.Me?.Email ?? session.Me?.Name ?? session.Me?.UserId : "Not signed in"),
            signedIn ? Ui.Secondary("Sign out", SignOutAsync) : Ui.Primary("Sign in", () => Shell.Current.GoToAsync("login")))));

        body.Add(Ui.Card(Ui.Stack(6,
            Ui.H2("This phone"),
            Ui.Field("Haptics", Ui.Toggle(Haptics.Enabled, on => { Haptics.Enabled = on; Haptics.Tap(); return Task.CompletedTask; })),
            Ui.Muted(speech.IsAvailable
                ? $"Speech recognition: {(speech.OnDevice ? "on this phone (nothing leaves it)" : "the system recognizer (may use the network)")}."
                : "No speech recognizer on this phone: spoken turns are sent to the server to read."),
            Ui.Muted("Fights keep going with the screen locked; a notification shows while one runs."))));

        Entry baseUrl = new() { Text = config.BaseUrl, Keyboard = Keyboard.Url };
        Entry clientId = new() { Text = config.EntraClientId, Placeholder = "Entra app client id" };
        Label diag = Ui.Muted(null);
        body.Add(Ui.Card(Ui.Stack(6,
            Ui.H2("Server"),
            Ui.Muted($"Microsoft sign-in needs the redirect {MobileConfig.RedirectUri} on the app registration. Debug: scripts/mobile-dev.ps1."),
            baseUrl, clientId,
            Ui.Secondary("Save", async () =>
            {
                bool moved = baseUrl.Text?.TrimEnd('/') != config.BaseUrl;
                config.BaseUrl = baseUrl.Text ?? string.Empty;
                config.EntraClientId = clientId.Text ?? string.Empty;
                if (moved)
                {
                    await SignOutAsync();
                }
            }),
            Ui.Secondary("System status", async () =>
            {
                DiagReportDto? d = await api.GetDiagAsync();
                diag.Text = d is null ? "Can't reach the API." : $"{d.Status} · {d.Environment} · v{d.Version} · {string.Join(" · ", d.Checks.Select(c => $"{c.Label}: {c.Status}"))}";
            }),
            diag)));
        body.Add(Ui.Muted($"v{AppInfo.Current.VersionString}"));
    }

    private async Task SignOutAsync()
    {
        await api.LogoutAsync();
        await entra.SignOutAsync();
        cookies.Clear();
        session.Clear();
        await Shell.Current.GoToAsync("login");
    }
}
