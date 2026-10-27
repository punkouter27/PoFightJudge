using PoFightJudge.Api.Features.Fight;
using PoFightJudge.Api.Features.Profiles;
using PoFightJudge.Api.Features.Storage;
using PoFightJudge.Api.Features.Watch;
using PoFightJudge.Shared.Identifiers;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Unit.Shared;

/// <summary>
/// Before the first word, everybody knows who is for the topic and who is against it: the two sides always oppose,
/// the AI argues the side it was given, the 2P host says who is on which, and the sides are kept with the match.
/// </summary>
public class SidesTests
{
    [Fact]
    public void Each_side_is_for_or_against_and_everybody_downstream_is_told_which()
    {
        // The two always oppose, and the topic as the prompts read it says who holds which.
        Stance.For.Opposite().Should().Be(Stance.Against);
        Stance.Against.Opposite().Should().Be(Stance.For);
        Sides.Describe("turning the heat up", Stance.Against, "the husband", "the wife")
            .Should().Be("turning the heat up (the husband is AGAINST it; the wife is FOR it)");
        Sides.Describe("turning the heat up", null, "a", "b").Should().Be("turning the heat up", "no sides chosen, nothing added");
        Sides.Describe("  ", Stance.For, "a", "b").Should().BeNullOrWhiteSpace("a side of nothing is no side");

        // Which side won: the winner's own, the other side's opposite, nothing for a draw or a match without sides.
        Sides.OfWinner("SAM", "ALI", "SAM", Stance.For).Should().Be(Stance.Against);
        Sides.OfWinner("ALI", "ALI", "SAM", Stance.For).Should().Be(Stance.For);
        Sides.OfWinner(string.Empty, "ALI", "SAM", Stance.For).Should().BeNull("a draw has no winning side");
        Sides.OfWinner("ALI", "ALI", "SAM", null).Should().BeNull();

        // CPU / 1P: each persona is told to argue its own side and never give it up.
        var husband = Profile.Create("HUS", ProfileRole.Husband, "a", "b", "c", "d");
        var wife = Profile.Create("WIF", ProfileRole.Wife, "a", "b", "c", "d");
        var prompt = RoundPromptBuilder.Build(husband, wife, [], "wife", "calm", wasSlapped: false,
            topic: Sides.Describe("turning the heat up", Stance.For, "the husband", "the wife"));
        prompt.User.Should().Contain("the husband is FOR it; the wife is AGAINST it");
        prompt.User.Should().Contain("never concede", "a side that drifts is no side");

        // 2P: the host says who is on which side at the top of the show.
        var setup = new ShowSetup(HostPersonaId.Referee, "ALI", "SAM", "turning the heat up") { Player1Stance = Stance.For };
        HostPersona.SystemInstruction(new DebateOptions(), setup).Should().Contain("ALI is FOR it").And.Contain("SAM is AGAINST it");
        new DebateSession(new DebateOptions(), MatchId.New(), DateTimeOffset.UnixEpoch, setup).Snapshot(DateTimeOffset.UnixEpoch)
            .Player1Stance.Should().Be(Stance.For, "the live page shows the sides too");

        // And the sides are kept with the match, so the judge after the fight reads them as well.
        var match = new MatchDto(MatchId.New(), "u", MatchMode.Fight, DateTimeOffset.UnixEpoch, null, "turning the heat up",
            MatchSide.Human("ALI"), MatchSide.Human("SAM"), SessionPhase.Done, SessionStatus.Ready, string.Empty, null, IsFake: true)
        { Side1Stance = Stance.Against };
        MatchEntity.From(match).ToDto().Side1Stance.Should().Be(Stance.Against);
        MatchEntity.From(match with { Side1Stance = null }).ToDto().Side1Stance.Should().BeNull("an old match had no sides");
    }
}
