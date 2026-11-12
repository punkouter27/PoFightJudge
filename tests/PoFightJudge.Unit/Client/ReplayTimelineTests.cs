using Bunit;
using Microsoft.Extensions.DependencyInjection;
using PoFightJudge.Client.Layout.Components;
using PoFightJudge.Shared.Identifiers;
using PoFightJudge.Shared.Models;
using Radzen;

namespace PoFightJudge.Unit.Client;

/// <summary>The whole fight on one line: who held the floor when, and the moments worth hearing again.</summary>
public class ReplayTimelineTests : BunitContext
{
    private static readonly MatchId Match = MatchId.From("0123456789abcdef0123456789abcdef");

    public ReplayTimelineTests()
    {
        Services.AddRadzenComponents();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void Turns_sit_in_their_speakers_lane_where_they_happened_a_cut_in_is_marked_and_a_moment_is_heard_from_its_marker()
    {
        // The line is as long as the last thing on it.
        ReplayTimeline.Length([Turn(Speaker.Player1, 0, 40), Turn(Speaker.Host, 41, 47)], [Moment(30, 52)], []).Should().Be(52);
        ReplayTimeline.Length([], [], []).Should().Be(1, "an empty fight still has a line to draw, not a division by zero");

        int? heard = null;
        var cut = Render<ReplayTimeline>(p => p
            .Add(t => t.Player1, "AL")
            .Add(t => t.Player2, "SM")
            .Add(t => t.Turns, [Turn(Speaker.Player1, 0, 25), Turn(Speaker.Player2, 25, 50), Turn(Speaker.Player2, 75, 100, TurnKind.Interrupt)])
            .Add(t => t.Highlights, [Moment(50, 60), Moment(80, 90)])
            .Add(t => t.OnHighlight, (int index) => heard = index));

        var one = cut.Find(".lane.p1 .turn");
        one.GetAttribute("style").Should().Contain("left: 0%").And.Contain("width: 25%");
        cut.FindAll(".lane.p2 .turn").Should().HaveCount(2);
        cut.Find(".lane.p2 .turn.cut").GetAttribute("style").Should().Contain("left: 75%");
        cut.Find(".marks .rz-button").GetAttribute("aria-label").Should().Be("Hear Peak moment at 0:50");

        cut.FindAll(".marks .rz-button")[1].Click();

        heard.Should().Be(1);
    }

    private static TurnDto Turn(Speaker speaker, double start, double end, TurnKind kind = TurnKind.Talk) =>
        new(Match, 0, speaker, kind, "words") { StartSeconds = start, EndSeconds = end };

    private static HighlightDto Moment(double start, double end) => new(Speaker.Player1, "Peak moment", "quote", start, end);
}
