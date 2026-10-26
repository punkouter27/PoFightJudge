using PoFightJudge.Client.Components;

namespace PoFightJudge.Unit.Client;

/// <summary>
/// How hot each fighter is running: the share of their own talking the microphone heard as loud. A share rather than
/// a count, so the one who talks most is not the one who looks angriest; and nothing until they have said enough,
/// so one shout in the first breath does not brand anybody.
/// </summary>
public class CalmMeterTests
{
    [Theory]
    [InlineData(0, 0, CalmMeter.Temper.Calm)]
    [InlineData(2.9, 2.9, CalmMeter.Temper.Calm)]
    [InlineData(1, 20, CalmMeter.Temper.Calm)]
    [InlineData(4, 20, CalmMeter.Temper.Warm)]
    [InlineData(8, 20, CalmMeter.Temper.Heated)]
    public void Loud_talk_as_a_share_of_their_own_talk_sets_the_temper(double loudSeconds, double talkSeconds, CalmMeter.Temper expected) =>
        CalmMeter.Read(loudSeconds, talkSeconds).Should().Be(expected);
}
