using PoFightJudge.Api.Features.Analysis;
using PoFightJudge.Client.Features.Auth;
using PoFightJudge.Client.Features.Diagnostics;
using PoFightJudge.Client.Features.Fight;
using PoFightJudge.Client.Features.Fighters;
using PoFightJudge.Client.Features.History;
using PoFightJudge.Client.Features.Home;
using PoFightJudge.Client.Features.Watch;
using PoFightJudge.Shared.Identifiers;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Unit.Client;

/// <summary>
/// The ruling as a story-sized picture: what it was about, who took it, how the room split, what was said about it,
/// and the worst slip — the loser's, because that is the one the picture is for.
/// </summary>
public class StoryCardTests
{
    private static readonly MatchId Match = MatchId.From("0123456789abcdef0123456789abcdef");

    [Fact]
    public void The_card_names_the_winner_and_carries_the_losers_slip_and_a_fight_nobody_took_says_so()
    {
        var card = Verdict.Story(Report(ruled: true), rehearsal: false);

        card.Headline.Should().Be("AL took it.");
        card.Topic.Should().Be("the thermostat");
        (card.One, card.Two).Should().Be(("AL", "SM"));
        card.OneShare.Should().BeApproximately(0.6, 0.001);
        card.TwoShare.Should().BeApproximately(0.4, 0.001);
        card.Fallacy.Should().Be("Moving the goalposts", "the loser's slip, not the winner's");
        card.FallacyQuote.Should().Be("that is not what I meant");
        card.FileName.Should().Be("the-thermostat-ruling.png");

        // A fight nobody took says so.
        var unruled = Verdict.Story(Report(ruled: false), rehearsal: true);

        unruled.Headline.Should().Be("Nobody took it.");
        unruled.Rehearsal.Should().BeTrue("a rehearsal is marked as one on the picture as well as the page");
    }

    private static AnalysisReportDto Report(bool ruled)
    {
        var empty = SpeechMetrics.Compute(Speaker.Player1, new MappedTranscript([], false, string.Empty), []);
        return new AnalysisReportDto(
            Match,
            "the thermostat",
            new PlayerReportDto("AL", empty with { TalkShare = 0.6 }, Assessment(new FallacyDto("Appeal to volume", "I said it louder"))),
            new PlayerReportDto("SM", empty with { TalkShare = 0.4 }, Assessment(new FallacyDto("Moving the goalposts", "that is not what I meant"))),
            new OverallVerdictDto(Speaker.Player1, Speaker.Player1, Speaker.Player1, ["louder"], "One of them meant it.") { Ruled = ruled },
            null,
            SpeakerMappingFlagged: false,
            SpeakerMappingNote: string.Empty,
            AnalysisSeconds: 3,
            GeneratedAt: DateTimeOffset.UnixEpoch);
    }

    private static PlayerAssessmentDto Assessment(FallacyDto slip) => new(
        "B2", "clear enough", 1, [], 6, 7, 8, [slip], 5, 6, 7, 80, [],
        new EmotionProfileDto(10, 40, 30, 5, 0, 15), [], "quote", ["even"],
        "steady", "even", "varied", 7, 6, 4, 5, "best", "worst", ["one", "two", "three"]);
}
