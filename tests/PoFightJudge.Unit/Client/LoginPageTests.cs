using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using PoFightJudge.Client.Pages;
using PoFightJudge.Client.Services;
using PoFightJudge.Shared.Models;
using Radzen;

namespace PoFightJudge.Unit.Client;

/// <summary>
/// Microsoft is a door everywhere but the Test host; Development adds the guest door beside it. These run as
/// Development, so both doors are checked, and what the page says when the guest one is shut.
/// </summary>
public class LoginPageTests : BunitContext
{
    private readonly IApiClient _api = Substitute.For<IApiClient>();

    public LoginPageTests()
    {
        Services.AddRadzenComponents();
        Services.AddScoped<ThemeInterop>();
        Services.AddSingleton(_api);
        // Program.cs loads the gate before the first render; under bunit the component's own fallback load does it.
        Services.AddSingleton<FeatureGate>();
        Services.AddScoped<AuthenticationStateProvider>(_ => new ApiAuthStateProvider(_api));
        var env = Substitute.For<IWebAssemblyHostEnvironment>();
        env.Environment.Returns("Development");
        Services.AddSingleton(env);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public async Task The_guest_door_says_how_to_open_it_when_shut_and_in_development_returns_to_a_safe_url_only()
    {
        _api.GetFeaturesAsync(Arg.Any<CancellationToken>()).Returns(new FeatureFlagsDto(false, false, true, FishVoices: false));

        var shut = Render<Login>();

        await shut.WaitForAssertionAsync(() => shut.Markup.Should().Contain("The guest door is off"));
        shut.Markup.Should().NotContain("Continue as Guest").And.Contain("Sign in with Microsoft", "Microsoft does not depend on the guest flag");

        // The door opens: the gate is read again, as Program.cs reads it before a page is rendered.
        _api.GetFeaturesAsync(Arg.Any<CancellationToken>()).Returns(new FeatureFlagsDto(false, true, true, FishVoices: false));
        await Services.GetRequiredService<FeatureGate>().LoadAsync();
        _api.GuestSignInAsync(Arg.Any<CancellationToken>()).Returns(new AuthMeDto(true, "GUEST-1", "GUEST-1", "DevGuest"));
        var nav = Services.GetRequiredService<BunitNavigationManager>();

        // A crafted absolute return url must not bounce the user off-site.
        nav.NavigateTo("login?returnUrl=https%3A%2F%2Fevil.example%2Fphish");
        var cut = Render<Login>();
        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("Continue as Guest (Dev)"));
        cut.Markup.Should().Contain("Sign in with Microsoft", "Development offers Microsoft sign-in beside the guest door");

        await cut.Find(".rz-button.rz-secondary").ClickAsync(new());

        await _api.Received(1).GuestSignInAsync(Arg.Any<CancellationToken>());
        nav.Uri.Should().Be(nav.BaseUri, "an off-site return url falls back to home");

        // When Microsoft refuses an account because its organisation has not allowed the app, the page says that
        // and what to do, rather than printing a paragraph of ids.
        PoFightJudge.Client.Services.MicrosoftSignIn.Explain("AADSTS500011: The resource principal named api://x was not found in the tenant named y.")
            .Should().Contain("organisation").And.Contain("personal Microsoft account");
        PoFightJudge.Client.Services.MicrosoftSignIn.Explain("AADSTS70008: expired").Should().NotContain("organisation");
    }
}
