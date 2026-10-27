using PoFightJudge.Client.Pages;

namespace PoFightJudge.Unit.Client;

/// <summary>
/// The two decisions behind a hands-free turn: when a pause has run long enough to stop recording,
/// and whether what came back is worth sending.
/// </summary>
public class SpokenTurnTests
{
    [Fact]
    public void A_long_enough_pause_ends_the_turn_and_only_words_are_sent()
    {
        // Two seconds of quiet after speaking ends the turn
        WatchPlay.PauseEndsTurn(heardSomething: true, quietFor: TimeSpan.FromSeconds(2))
            .Should().BeTrue("two seconds of quiet after somebody has spoken is the whole stop gesture");

        // A shorter pause is still a pause
        WatchPlay.PauseEndsTurn(heardSomething: true, quietFor: TimeSpan.FromSeconds(1.9))
            .Should().BeFalse("thinking mid-sentence must not send the turn out from under them");

        // Silence before a word is spoken never ends the turn
        WatchPlay.PauseEndsTurn(heardSomething: false, quietFor: TimeSpan.FromMinutes(5))
            .Should().BeFalse("the microphone opens with the turn, so somebody still gathering themselves would be cut off at once");

        WatchPlay.WorthSending("That is not what I said.").Should().BeTrue();
        WatchPlay.WorthSending("  and you know it  ").Should().BeTrue();
        WatchPlay.WorthSending("").Should().BeFalse();
        WatchPlay.WorthSending("   ").Should().BeFalse();
        WatchPlay.WorthSending(null).Should().BeFalse();
    }

}
