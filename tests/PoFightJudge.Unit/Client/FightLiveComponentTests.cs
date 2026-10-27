using Bunit;
using Microsoft.Extensions.DependencyInjection;
using PoFightJudge.Client.Components;
using PoFightJudge.Shared.Identifiers;
using PoFightJudge.Shared.Models;
using Radzen;

namespace PoFightJudge.Unit.Client;

/// <summary>The pieces of the live stage: where the fight is, how long is left, who is talking, and what was said.</summary>
/// <summary>The pieces of the live stage: where the fight is, how long is left, who is talking, and what was said.</summary>
public class FightLiveComponentTests : BunitContext
{
    public FightLiveComponentTests()
    {
        Services.AddRadzenComponents();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void Captions_are_labelled_with_their_tag_and_capped_and_the_momentum_bar_splits_the_room_between_the_two()
    {
        var log = new CaptionLog();
        log.Add(new CaptionDto(Speaker.Player1, "you never listen", true));
        log.Add(new CaptionDto(Speaker.Host, "one at a time", true));

        var cut = Render<CaptionFeed>(p => p
            .Add(f => f.Log, log)
            .Add(f => f.Player1Name, "AB")
            .Add(f => f.HostName, "The Referee"));

        var lines = cut.FindAll("article.caption");
        lines.Should().HaveCount(2);
        lines[0].TextContent.Should().Contain("AB").And.Contain("you never listen");
        lines[0].ClassList.Should().Contain("p1");
        lines[1].TextContent.Should().Contain("The Referee");
        lines[1].ClassList.Should().Contain("host");

        // The momentum bar splits the room between the two tags.
        var bar = Render<MomentumBar>(p => p
            .Add(b => b.Player1, "AL")
            .Add(b => b.Player2, "SM")
            .Add(b => b.Share, 0.624));

        bar.Find(".name.p1").TextContent.Should().Contain("AL").And.Contain("62%");
        bar.Find(".name.p2").TextContent.Should().Contain("38%").And.Contain("SM");
        bar.Find(".rope").GetAttribute("aria-valuenow").Should().Be("62");
        bar.Find(".momentum").GetAttribute("style").Should().Contain("--share: 0.624");

        // A long fight does not grow the page without limit.
        var longFight = new CaptionLog();

        for (var i = 0; i < CaptionLog.MaxLines + 40; i++)
        {
            longFight.Add(new CaptionDto(Speaker.Player1, $"line {i}", true));
        }

        longFight.Lines.Should().HaveCount(CaptionLog.MaxLines);
        longFight.Lines[^1].Text.Should().Be($"line {CaptionLog.MaxLines + 39}", "it is the oldest lines that fall off");
    }
}

/// <summary>
/// The riser is punctuation, not a bed (T65: an open microphone hears whatever the speakers play), so it is played
/// on the snapshot that crosses a mark, once, and only while the clock is actually running.
/// </summary>
public class RiserTests
{
    [Theory]
    [InlineData(SessionPhase.Debate, 31, 30, true)]
    [InlineData(SessionPhase.Debate, 30, 29, false)]
    [InlineData(SessionPhase.Debate, 11, 10, true)]
    [InlineData(SessionPhase.Debate, 12, 9, true)]
    [InlineData(SessionPhase.Debate, 1, 0, false)]
    [InlineData(SessionPhase.Probe, 31, 30, false)]
    [InlineData(SessionPhase.Debate, int.MaxValue, 180, false)]
    public void A_riser_marks_half_a_minute_and_ten_seconds_left(SessionPhase phase, int before, int now, bool due) =>
        PoFightJudge.Client.Pages.FightLive.RiserDue(phase, before, now).Should().Be(due);

    [Fact]
    public void A_fallacy_is_stamped_once_when_the_host_calls_it_and_not_again_on_every_snapshot_after()
    {
        var strawman = new LiveFlagDto(Speaker.Player2, "Strawman", "so you want us to freeze");
        var quiet = new DebateSnapshotDto(MatchId.New(), SessionPhase.Debate, "t", "AB", "CD", Speaker.Player1, 10, 170, null);
        var called = quiet with { Flags = [strawman] };

        PoFightJudge.Client.Pages.FightLive.NewFlag(quiet, quiet).Should().BeNull();
        PoFightJudge.Client.Pages.FightLive.NewFlag(quiet, called).Should().Be(strawman);
        PoFightJudge.Client.Pages.FightLive.NewFlag(called, called with { DebateElapsedSeconds = 11 }).Should().BeNull("the clock ticking is not a new call");
        PoFightJudge.Client.Pages.FightLive.NewFlag(null, called).Should().Be(strawman, "a page that joins late still shows the latest one");
    }
}
