using Blazored.LocalStorage;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PoFightJudge.Client.Services;
using PoFightJudge.Shared.Models;
using Radzen;

// The domain tests own the namespace PoFightJudge.Unit.Fight, which shadows the page type here.
using FightPage = PoFightJudge.Client.Features.Fight.Fight;

namespace PoFightJudge.Unit.Client;

/// <summary>
/// Who hosts a 2P fight: the Settings choice, unless a rematch was asked for, which replays the host that fight had.
/// </summary>
public class FightSetupTests : BunitContext, IAsyncLifetime
{
    private readonly IApiClient _api = Substitute.For<IApiClient>();
    private readonly ILocalStorageService _storage = Substitute.For<ILocalStorageService>();

    public FightSetupTests()
    {
        Services.AddRadzenComponents();
        Services.AddSingleton(_storage);
        Services.AddScoped<SetupMemory>();
        Services.AddScoped<Preferences>();
        Services.AddScoped<Viewport>();
        Services.AddSingleton(TimeProvider.System);
        Services.AddScoped<SfxInterop>();
        Services.AddScoped<MicInterop>();
        Services.AddSingleton(_api);
        JSInterop.Mode = JSRuntimeMode.Loose;
        _api.GetFightersAsync(Arg.Any<CancellationToken>()).Returns([]);

        // The page reports a failed start rather than navigating, which is all this needs: what it asked for.
        _api.StartFightAsync(Arg.Any<CreateFightRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("down"));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>The mic check holds a MicInterop, which is released asynchronously; bunit's teardown has to be awaited.</summary>
    public new async Task DisposeAsync() => await base.DisposeAsync().ConfigureAwait(false);

    [Theory]
    [InlineData(false, HostPersonaId.Referee)]
    [InlineData(true, HostPersonaId.Coach)]
    public async Task The_host_is_the_setting_unless_a_rematch_brings_its_own(bool rematch, HostPersonaId expected)
    {
        var card = new FightCard("ALI", "SAM", "the thermostat", ProfileRole.Husband, ProfileRole.Wife, HostPersonaId.Coach);
        if (rematch)
        {
            Services.GetRequiredService<SetupMemory>().Rematch(card);
        }
        else
        {
            // Last night's card, remembered: the tags come back, the host is whatever Settings says now.
            _storage.GetItemAsync<FightCard>(SetupMemory.FightKey, Arg.Any<CancellationToken>()).Returns(card);
        }

        var cut = Render<FightPage>();
        await cut.WaitForAssertionAsync(() => cut.FindAll(".rz-button").Single(b => b.TextContent.Contains("Start the fight", StringComparison.Ordinal))
            .HasAttribute("disabled").Should().BeFalse());
        await cut.FindAll(".rz-button").Single(b => b.TextContent.Contains("Start the fight", StringComparison.Ordinal)).ClickAsync(new());

        await _api.Received().StartFightAsync(Arg.Is<CreateFightRequest>(r => r.Persona == expected && r.Player1Stance == Stance.For), Arg.Any<CancellationToken>());
    }
}
