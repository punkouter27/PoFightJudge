using PoFightJudge.Api.Features.Records;
using PoFightJudge.Shared.Identifiers;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Unit.Records;

/// <summary>
/// A profile's stats are its games: a pre-made persona's from the watches it was argued in, a person's from the
/// fights they had. Counted from result rows on read, rated the same way the leaderboards rate them.
/// </summary>
public class ProfileRecordsTests
{
    private static readonly DateTimeOffset Night = new(2026, 10, 20, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_record_is_counted_from_its_games_and_rated_like_the_leaderboard()
    {
        WatchResultDto[] watches =
        [
            Watch("KSH", won: true, draw: false, "MAH"), Watch("MAH", won: false, draw: false, "KSH"),
            Watch("KSH", won: false, draw: true, "MAH"), Watch("MAH", won: false, draw: true, "KSH"),
            Watch("KSH", won: false, draw: false, "MAH"), Watch("MAH", won: true, draw: false, "KSH"),
        ];
        FighterResultDto[] fights = [Fight("AL", won: true, "SM"), Fight("SM", won: false, "AL")];

        var cast = ProfileRecords.From(watches);
        cast["KSH"].Should().Be(new ProfileRecordDto(Matches: 3, Wins: 1, Losses: 1, Draws: 1, Rating: EloRatings.Compute(watches)["KSH"]));

        var people = ProfileRecords.From(fights);
        people["AL"].Should().Be(new ProfileRecordDto(1, 1, 0, 0, EloRatings.Compute(fights)["AL"]));
        people["AL"].Rating.Should().BeGreaterThan(EloRatings.Start, "a win over somebody rated the same lifts you");

        ProfileRecordDto.None.Should().Be(new ProfileRecordDto(0, 0, 0, 0, EloRatings.Start), "nobody who has not played is anywhere but the start");
    }

    private static WatchResultDto Watch(string initials, bool won, bool draw, string opponent) =>
        new(initials, MatchId.New(), Night, "the bins", opponent, won, draw, won ? 70 : 40, AdvancedStatsDto.Empty);

    private static FighterResultDto Fight(string tag, bool won, string opponent) =>
        new(tag, "u", MatchId.New(), MatchMode.Fight, Night, "the bins", opponent, won, Draw: false, won ? 70 : 40, StyleSnapshot.Empty);
}
