using PoFightJudge.Api.Features.Watch;
using PoFightJudge.Shared.Identifiers;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Unit.Watch;

public class WatchMatchTests
{
    private static readonly DateTimeOffset Ended = new(2026, 9, 6, 20, 41, 0, TimeSpan.Zero);

    private static WatchRound Round(string speaker = WatchRound.Husband) => new(speaker, "You left the freezer open.", "angry");

    private static List<WatchRound> Rounds(int count) => [.. Enumerable.Range(0, count).Select(i => Round(i % 2 == 0 ? WatchRound.Husband : WatchRound.Wife))];

    private static WatchMatch Complete(int roundCount, string winner = "MAH", MatchSide? husband = null, MatchSide? wife = null) => WatchMatch.Complete(
        MatchId.New(),
        husband ?? MatchSide.Persona("MAH", "Matthew"),
        wife ?? MatchSide.Persona("KSH", "Kimberly"),
        "user-1",
        "the freezer",
        Rounds(roundCount),
        winner,
        "The husband held the point.",
        AdvancedStats.Empty,
        AdvancedStats.Empty,
        Ended);

    [Fact]
    public void A_match_carries_six_lines_or_seven_when_a_slap_added_one_and_is_complete_only_when_both_sides_have_had_their_rounds()
    {
        WatchMatch.MaxRoundsPerMatch.Should().Be(3);
        WatchMatch.MaxLinesPerMatch.Should().Be(7, "a slap inserts one extra reaction, and every slapped match failed at the verdict without the allowance");

        Complete(6).Rounds.Should().HaveCount(6);
        Complete(WatchMatch.MaxLinesPerMatch).Rounds.Should().HaveCount(7);

        var tooMany = () => Complete(WatchMatch.MaxLinesPerMatch + 1);
        tooMany.Should().Throw<InvalidOperationException>().WithMessage("*at most 7*");

        var none = () => WatchMatch.Complete(MatchId.New(), MatchSide.Persona("MAH"), MatchSide.Persona("KSH"), "u", null, [], "MAH", "v", AdvancedStats.Empty, AdvancedStats.Empty, Ended);
        none.Should().Throw<InvalidOperationException>();

        // Complete only when both sides have had all their rounds.
        List<WatchRound> Lines(int husband, int wife) =>
        [
            .. Enumerable.Repeat(new WatchRound(WatchRound.Husband, "h", "angry"), husband),
            .. Enumerable.Repeat(new WatchRound(WatchRound.Wife, "w", "angry"), wife),
        ];

        WatchRules.IsComplete(Lines(3, 3)).Should().BeTrue();
        WatchRules.IsComplete(Lines(3, 2)).Should().BeFalse();
        WatchRules.IsComplete(Lines(4, 3)).Should().BeTrue("the slap reaction is an extra line, not an extra round");
        WatchRules.IsComplete([]).Should().BeFalse();
    }
}

public class WatchRulesTests
{
    [Fact]
    public void Every_line_carries_a_point_the_judge_can_weigh_keeps_its_closing_cue_and_against_a_person_answers_them_first()
    {
        foreach (var opponentIsHuman in new[] { false, true })
        {
            var rules = WatchRules.LineRulesFor(opponentIsHuman);

            rules.Should().Contain("ONE CONCRETE POINT", "the judge scores on evidence and rebuttal; two sides trading only insults came back a draw in two of four real matches");
            rules.Should().NotContain("No stage directions,", "the output contract asks for a closing stage cue, and the rules must not forbid what the contract demands");

            if (opponentIsHuman)
            {
                // The human rules put the player's words first and allow deflection only out loud.
                rules.Should().Contain("ANSWER THEIR LAST LINE", "the player said those words out loud and is waiting to hear them engaged");
                rules.Should().Contain("never by ignoring it", "refusing to answer is legitimate; silently changing the subject is the failure mode");
            }
        }
    }
}
