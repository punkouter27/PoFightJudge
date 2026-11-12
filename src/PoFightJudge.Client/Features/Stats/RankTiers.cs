namespace PoFightJudge.Client.Features.Stats;

/// <summary>The ladder an Elo rating is read against, lowest first so tiers compare the way they climb.</summary>
public enum Tier
{
    Unranked,
    Bronze,
    Silver,
    Gold,
    Platinum,
    Diamond,
}

/// <summary>
/// Where a rating sits. Everybody starts at 1000 (EloRatings.Start), which is the middle of Silver: one fight either way
/// leaves them there, and a tier fifty points wide takes a run of wins or losses to cross. Nobody is ranked before
/// they have argued at all.
/// </summary>
public static class RankTiers
{
    public static Tier For(int rating, int fights) =>
        fights <= 0 ? Tier.Unranked
        : rating >= 1125 ? Tier.Diamond
        : rating >= 1075 ? Tier.Platinum
        : rating >= 1025 ? Tier.Gold
        : rating >= 975 ? Tier.Silver
        : Tier.Bronze;

    /// <summary>A climb since this person's page was last looked at. Nothing to compare against is not a climb.</summary>
    public static bool Promoted(Tier? before, Tier now) => before is { } was && now > was;
}
