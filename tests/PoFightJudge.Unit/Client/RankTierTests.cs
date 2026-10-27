using PoFightJudge.Client.Stats;

namespace PoFightJudge.Unit.Client;

/// <summary>
/// Where an Elo rating sits on the ladder. Everybody starts at 1000 — Silver, the middle of it — so one fight either
/// way leaves them there and it takes a run to move; nobody is ranked until they have argued at all.
/// </summary>
public class RankTierTests
{
    [Fact]
    public void A_rating_has_a_tier_and_only_a_climb_since_last_time_is_celebrated()
    {
        foreach (var (rating, fights, tier) in new[]
        {
            (1000, 0, Tier.Unranked), (950, 4, Tier.Bronze), (975, 1, Tier.Silver), (1016, 1, Tier.Silver),
            (1025, 3, Tier.Gold), (1100, 9, Tier.Platinum), (1125, 12, Tier.Diamond),
        })
        {
            RankTiers.For(rating, fights).Should().Be(tier, $"{rating} after {fights} fights");
        }

        foreach (var (before, now, promoted) in new (Tier?, Tier, bool)[]
        {
            (null, Tier.Gold, false), (Tier.Silver, Tier.Gold, true), (Tier.Unranked, Tier.Silver, true),
            (Tier.Gold, Tier.Gold, false), (Tier.Gold, Tier.Silver, false),
        })
        {
            RankTiers.Promoted(before, now).Should().Be(promoted, $"{before} to {now}");
        }
    }

}
