using PoFightJudge.Client.Components;

namespace PoFightJudge.Unit.Client;

/// <summary>
/// How hot each fighter is running: the share of their own talking the microphone heard as loud. A share rather than
/// a count, so the one who talks most is not the one who looks angriest; and nothing until they have said enough,
/// so one shout in the first breath does not brand anybody.
/// </summary>
public class CalmMeterTests
{
    [Fact]
    public void Loud_talk_as_a_share_of_their_own_talk_sets_the_temper()
    {
        foreach (var (loud, talk, temper) in new[]
        {
            (0, 0, CalmMeter.Temper.Calm), (2.9, 2.9, CalmMeter.Temper.Calm), (1, 20, CalmMeter.Temper.Calm),
            (4, 20, CalmMeter.Temper.Warm), (8, 20, CalmMeter.Temper.Heated),
        })
        {
            CalmMeter.Read(loud, talk).Should().Be(temper, $"{loud}s loud of {talk}s");
        }
    }
}
