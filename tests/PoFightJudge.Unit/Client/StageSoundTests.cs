using Bunit;
using Microsoft.Extensions.DependencyInjection;
using PoFightJudge.Client.Components;
using NSubstitute;
using PoFightJudge.Client.Services;

namespace PoFightJudge.Unit.Client;

/// <summary>
/// The live stage's components make a sound when something changes hands — not on every snapshot, which would be a
/// noise for every tick of the clock, and not on the first render, which changes nothing.
/// </summary>
public class StageSoundTests : BunitContext
{
    public StageSoundTests()
    {
        Services.AddScoped<SfxInterop>();
        Services.AddSingleton(Substitute.For<Blazored.LocalStorage.ILocalStorageService>());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private string[] Played() =>
        [.. JSInterop.Invocations.Where(i => string.Equals(i.Identifier, SfxInterop.Play, StringComparison.Ordinal)).Select(i => (string)i.Arguments[0]!)];

    [Fact]
    public void The_momentum_bar_sloshes_when_the_lead_changes_sides_and_only_then()
    {
        var cut = Render<MomentumBar>(p => p.Add(b => b.Share, 0.4));
        Played().Should().BeEmpty("nothing has changed hands yet");

        cut.Render(p => p.Add(b => b.Share, 0.49));
        cut.Render(p => p.Add(b => b.Share, 0.51));
        Played().Should().BeEmpty("a hair either side of even is not a lead");

        cut.Render(p => p.Add(b => b.Share, 0.6));
        cut.Render(p => p.Add(b => b.Share, 0.65));
        Played().Should().Equal(Sfx.Slosh);

        cut.Render(p => p.Add(b => b.Share, 0.3));
        Played().Should().Equal(Sfx.Slosh, Sfx.Slosh);
    }
}
