using PoFightJudge.Client.Features.Auth;
using PoFightJudge.Client.Features.Diagnostics;
using PoFightJudge.Client.Features.Fight;
using PoFightJudge.Client.Features.Fighters;
using PoFightJudge.Client.Features.History;
using PoFightJudge.Client.Features.Home;
using PoFightJudge.Client.Features.Watch;
using PoFightJudge.Client.Services;
using PoFightJudge.Shared.Identifiers;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Unit.Client;

/// <summary>
/// A one-tap rematch is this fight again: the same two tags, the same topic, the same host. The record keeps those;
/// it does not keep the seats or the microphone, which come from the last fight set up on this device — the seats
/// only when that was these two.
/// </summary>
public class RematchTests
{
    private static readonly MatchDto Fought = new(
        MatchId.From("0123456789abcdef0123456789abcdef"), "user", MatchMode.Fight, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
        "the thermostat", MatchSide.Human("AL"), MatchSide.Human("SM"), SessionPhase.Done, SessionStatus.Ready, "AL", null, false)
    {
        Persona = nameof(HostPersonaId.Roastmaster),
    };

    [Fact]
    public void It_is_the_same_fight_again_seated_as_these_two_last_sat_on_this_devices_microphone()
    {
        var card = Verdict.RematchCard(Fought, last: null);

        card.Should().Be(new FightCard("AL", "SM", "the thermostat", ProfileRole.Husband, ProfileRole.Wife, HostPersonaId.Roastmaster));

        // The seats and the microphone come from the last setup of these two.
        var theirs = Verdict.RematchCard(Fought, new FightCard("sm", "al", "something else", ProfileRole.Wife, ProfileRole.Husband, HostPersonaId.Coach) { Device = "usb-mic" });

        theirs.OneRole.Should().Be(ProfileRole.Husband, "AL sat as the husband last time, whichever box the tag was typed in");
        theirs.TwoRole.Should().Be(ProfileRole.Wife);
        theirs.Persona.Should().Be(HostPersonaId.Roastmaster, "the host is this fight's, not the last one set up");
        theirs.Topic.Should().Be("the thermostat");
        theirs.Device.Should().Be("usb-mic");

        // Somebody else's seats are not borrowed.
        var borrowed = Verdict.RematchCard(Fought, new FightCard("KD", "SM", "x", ProfileRole.Wife, ProfileRole.Husband, HostPersonaId.Coach) { Device = "usb-mic" });

        (borrowed.OneRole, borrowed.TwoRole).Should().Be((ProfileRole.Husband, ProfileRole.Wife));
        borrowed.Device.Should().Be("usb-mic", "the microphone belongs to the device, not to the pair");
    }
}
