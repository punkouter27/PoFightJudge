using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using PoFightJudge.Client.Layout.Components;
using PoFightJudge.Client.Services;
using PoFightJudge.Shared.Identifiers;
using PoFightJudge.Shared.Models;
using Radzen;

namespace PoFightJudge.Unit.Client;

/// <summary>
/// A cast card is a glance: who, what they do, how they have done in games, how they lean and their line. The four
/// labelled scales are one press away rather than two thirds of the card.
/// </summary>
public sealed class ProfileCardTests : BunitContext, IAsyncLifetime
{
    public ProfileCardTests()
    {
        Services.AddSingleton(Substitute.For<IApiClient>());
        Services.AddScoped<AudioInterop>();
        Services.AddScoped<SfxInterop>();
        Services.AddSingleton(Substitute.For<Blazored.LocalStorage.ILocalStorageService>());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>The card holds an AudioInterop, which is released asynchronously; bunit's teardown has to be awaited.</summary>
    public new async Task DisposeAsync() => await base.DisposeAsync().ConfigureAwait(false);

    [Fact]
    public void The_four_scales_are_folded_away_behind_a_strip_that_still_shows_their_shape()
    {
        var profile = new ProfileDto
        {
            Id = ProfileId.From("MAH"),
            Persona = new CreateProfileRequest
            {
                Initials = "MAH",
                Name = "Matthew",
                Role = ProfileRole.Husband,
                LogicVsEmotion = 20,
                Patience = 70,
                HoldsGrudges = 40,
                Jealousy = 90,
                Philosophy = "Stay chill.",
            },
            Record = new ProfileRecordDto(Matches: 4, Wins: 2, Losses: 1, Draws: 1, Rating: 1032),
        };

        var cut = Render<ProfileCard>(p => p.Add(c => c.Profile, profile));

        // Its stats are its games, and nothing on the card writes to it.
        cut.Find(".record").TextContent.Should().Contain("2–1–1").And.Contain("1032");
        cut.FindAll("button[aria-label^='Edit'], button[aria-label^='Delete']").Should().BeEmpty("a profile is never edited by hand");

        var traits = cut.Find("details.traits");
        traits.HasAttribute("open").Should().BeFalse("the card opens folded");
        traits.QuerySelectorAll(".stat").Should().HaveCount(4, "every scale is still there when it is opened");
        cut.FindAll("summary .mini-fill").Select(f => f.GetAttribute("style"))
            .Should().Equal("--stat-value:20%", "--stat-value:70%", "--stat-value:40%", "--stat-value:90%");
        cut.Find(".quote").TextContent.Should().Contain("Stay chill.");
    }
}

/// <summary>
/// The cast page lists the cast with their records. Profiles are the pre-made cast or come from play, so the only
/// thing it offers to change is putting the pre-made cast back.
/// </summary>
public sealed class ProfilesPageTests : BunitContext, IAsyncLifetime
{
    private readonly IApiClient _api = Substitute.For<IApiClient>();

    public ProfilesPageTests()
    {
        Services.AddRadzenComponents();
        Services.AddSingleton(TimeProvider.System);
        Services.AddSingleton(_api);
        Services.AddScoped<AudioInterop>();
        Services.AddScoped<SfxInterop>();
        Services.AddSingleton(Substitute.For<Blazored.LocalStorage.ILocalStorageService>());
        _api.GetProfilesAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new ProfileDto
            {
                Id = ProfileId.From("KSH"),
                Persona = new CreateProfileRequest { Initials = "KSH", Name = "Kimberly", Role = ProfileRole.Wife },
                Record = new ProfileRecordDto(3, 2, 1, 0, 1021),
            },
        ]);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public new async Task DisposeAsync() => await base.DisposeAsync().ConfigureAwait(false);

    [Fact]
    public async Task The_cast_is_listed_with_records_and_only_the_default_cast_can_be_put_back()
    {
        var cut = Render<PoFightJudge.Client.Features.Fighters.Profiles>();

        await cut.WaitForAssertionAsync(() => cut.FindAll("article.profile").Should().ContainSingle());
        cut.Find("article.profile .record").TextContent.Should().Contain("2–1");
        var buttons = cut.FindAll(".rz-button").Select(b => b.TextContent).ToList();
        buttons.Should().Contain(t => t.Contains("Load default cast", StringComparison.Ordinal));
        buttons.Should().NotContain(t => t.Contains("New profile", StringComparison.Ordinal), "nobody writes a profile by hand");
    }
}
