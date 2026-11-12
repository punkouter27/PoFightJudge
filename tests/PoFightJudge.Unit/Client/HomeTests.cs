using Blazored.LocalStorage;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using PoFightJudge.Client.Features.Auth;
using PoFightJudge.Client.Features.Diagnostics;
using PoFightJudge.Client.Features.Fight;
using PoFightJudge.Client.Features.Fighters;
using PoFightJudge.Client.Features.History;
using PoFightJudge.Client.Features.Home;
using PoFightJudge.Client.Features.Watch;
using PoFightJudge.Client.Services;
using Radzen;

namespace PoFightJudge.Unit.Client;

/// <summary>
/// Home is the three channels, with the cast and the leaderboard beside them, and nothing else. Its title is there
/// for a screen reader, which needs the page to open on one.
/// </summary>
public sealed class HomeTests : BunitContext
{
    private readonly IApiClient _api = Substitute.For<IApiClient>();

    public HomeTests()
    {
        Services.AddRadzenComponents();
        Services.AddSingleton(Substitute.For<ILocalStorageService>());
        Services.AddSingleton(_api);
        Services.AddScoped<GfxInterop>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void Home_is_the_three_channels_and_the_cast_and_leaderboard_beside_them_and_asks_the_api_for_nothing()
    {
        var cut = Render<Home>();

        cut.FindAll("article.channel").Select(c => c.GetAttribute("data-channel"))
            .Should().Equal(["CPU", "1P", "2P", "CAST", "TOP"], "the three channels, then the profiles and the leaderboard");
        cut.Find("h1").ClassList.Should().Contain("po-sr-only", "the page still opens on its title, for a screen reader");
        cut.FindAll(".tally, .replays, .standings, .hero-side").Should().BeEmpty("those pages are in the nav bar");
        _api.ReceivedCalls().Should().BeEmpty("nothing on the page is read from the API");
    }
}
