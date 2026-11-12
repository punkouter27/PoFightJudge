using PoFightJudge.Client.Layout.Components;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Unit.Client;

/// <summary>
/// The live share of the room: seconds a fighter actually spent talking while they had the floor. Not floor time —
/// the host hands the floor out in even slices, so that bar would sit in the middle all night. The browser counts
/// voiced seconds frame by frame; the clock credits each snapshot's worth of them to whoever held the floor.
/// </summary>
public class FloorClockTests
{
    [Fact]
    public void Only_voice_heard_while_a_fighter_has_the_floor_is_theirs_so_it_is_level_until_then()
    {
        new FloorClock().Share.Should().Be(0.5, "nobody has said anything, so it is level");

        var clock = new FloorClock();
        clock.Tick(Speaker.Player1, hostSpeaking: false, voicedSeconds: 2.0);
        clock.Tick(Speaker.Player2, hostSpeaking: false, voicedSeconds: 2.8);
        clock.Tick(Speaker.Player2, hostSpeaking: false, voicedSeconds: 3.0);

        clock.One.Should().BeApproximately(0.8, 0.001, "what was voiced up to the hand-over belongs to whoever held the floor through it");
        clock.Two.Should().BeApproximately(0.2, 0.001);
        clock.Share.Should().BeApproximately(0.8, 0.001);

        // Voice under the host, before anybody has the floor or across a stall counts for nobody.
        var under = new FloorClock();
        under.Tick(null, hostSpeaking: false, voicedSeconds: 0);
        under.Tick(Speaker.Player1, hostSpeaking: true, voicedSeconds: 0.5);
        under.Tick(Speaker.Player1, hostSpeaking: false, voicedSeconds: 0.9);

        under.One.Should().Be(0, "the room talking over the host is the host's time, not theirs");

        // A stall in the snapshots is not handed to whoever had the floor before it.
        var stalled = new FloorClock();
        stalled.Tick(Speaker.Player1, hostSpeaking: false, voicedSeconds: 0);
        stalled.Tick(Speaker.Player2, hostSpeaking: false, voicedSeconds: 30);

        stalled.One.Should().Be(FloorClock.LongestGap.TotalSeconds, "after half a minute without word, nobody knows whose turn it was");
    }

}
