using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using PoFightJudge.Client.Components;
using PoFightJudge.Client.Services;
using PoFightJudge.Shared.Models;

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

    [Fact]
    public void The_calm_meter_beats_a_heart_on_the_side_of_whoever_has_just_got_hotter()
    {
        var talk = new FloorClock();
        var loud = new FloorClock();
        var cut = Render<CalmMeter>(p => p.Add(m => m.Talk, talk).Add(m => m.Loud, loud));

        Speak(talk, loud, seconds: 20, loudSeconds: 1);
        cut.Render(p => p.Add(m => m.Talk, talk).Add(m => m.Loud, loud));
        Played().Should().BeEmpty("calm talk is not news");

        Speak(talk, loud, seconds: 2, loudSeconds: 4);
        cut.Render(p => p.Add(m => m.Talk, talk).Add(m => m.Loud, loud));
        cut.Render(p => p.Add(m => m.Talk, talk).Add(m => m.Loud, loud));
        Played().Should().Equal(Sfx.Heartbeat);
        JSInterop.Invocations.Last(i => string.Equals(i.Identifier, SfxInterop.Play, StringComparison.Ordinal)).Arguments[2]
            .Should().Be(AudioInterop.PanFor(husband: false), "fighter two is heard from their own side");
    }

    /// <summary>
    /// Fighter two talks for <paramref name="seconds"/>, <paramref name="loudSeconds"/> of it loud — fed a second at
    /// a time, because a clock credits at most <see cref="FloorClock.LongestGap"/> per tick.
    /// </summary>
    private static void Speak(FloorClock talk, FloorClock loud, double seconds, double loudSeconds)
    {
        var (voiced, shouted) = (talk.Two, loud.Two);
        talk.Tick(Speaker.Player2, hostSpeaking: false, voiced);
        loud.Tick(Speaker.Player2, hostSpeaking: false, shouted);
        for (var i = 0; i < Math.Max(seconds, loudSeconds); i++)
        {
            talk.Tick(Speaker.Player2, hostSpeaking: false, voiced += i < seconds ? 1 : 0);
            loud.Tick(Speaker.Player2, hostSpeaking: false, shouted += i < loudSeconds ? 1 : 0);
        }
    }
}
