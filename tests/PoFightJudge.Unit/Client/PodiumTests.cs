using PoFightJudge.Client.Layout.Components;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Unit.Client;

/// <summary>The top of the card: the three best ratings among people who have actually argued.</summary>
public class PodiumTests
{
    [Fact]
    public void The_three_best_rated_who_have_fought_best_first()
    {
        FighterStatsDto Fighter(string tag, int rating, int fights) =>
            FighterStatsDto.Empty(tag, tag) with { Rating = rating, Fights = fights };

        var podium = FoilCard.Podium(
        [
            Fighter("AA", 1010, 2), Fighter("BB", 1090, 6), Fighter("CC", 1000, 0),
            Fighter("DD", 960, 3), Fighter("EE", 1040, 4),
        ]);

        podium.Select(f => f.Tag).Should().Equal(["BB", "EE", "AA"], "CC has the third-best number and has never argued");
    }
}
