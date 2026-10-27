using PoFightJudge.Api.Features.Records;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Unit.Records;

/// <summary>
/// One leaderboard: the people who argued at the microphone and the cast that argues on its own, ranked together by
/// the same rules. Somebody whose own persona has also been argued is on it once, as themselves.
/// </summary>
public class StandingsTests
{
    [Fact]
    public void People_and_the_cast_are_ranked_together_and_a_person_stands_for_their_own_persona()
    {
        LeaderboardRowDto[] people = [Row("AB", 3, 2, 1040), Row("HAM", 1, 1, 1016)];
        LeaderboardRowDto[] cast = [Row("MAH", 5, 4, 1060), Row("KSH", 5, 1, 980), Row("HAM", 4, 3, 1050)];

        var board = LeaderboardEndpoints.Together(people, cast);

        board.Select(r => r.Id).Should().Equal(["MAH", "AB", "KSH"],
            "highest rating first; HAM has one fight of their own, so neither they nor their persona is on it yet");
        board.Select(r => r.IsPerson).Should().Equal([false, true, false]);
        board.Single(r => string.Equals(r.Id, "AB", StringComparison.Ordinal)).WinRate.Should().BeApproximately(0.667, 0.001);
    }

    private static LeaderboardRowDto Row(string id, int matches, int wins, int rating) =>
        new(id, id, matches, wins, 0, 60) { Rating = rating };
}
