using System.Text.Json;
using PoFightJudge.Shared;
using PoFightJudge.Shared.Identifiers;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Unit.Shared;

/// <summary>
/// The three ids are Vogen value objects: normalised on construction, validated once, serialised as bare strings,
/// and parsable so Minimal APIs bind them straight from a route segment.
/// </summary>
/// <summary>
/// The three ids are Vogen value objects: normalised on construction, validated once, serialised as bare strings,
/// and parsable so Minimal APIs bind them straight from a route segment.
/// </summary>
public class IdentifierTests
{
    [Fact]
    public void ProfileId_upper_cases_the_initials_and_FighterId_is_an_arcade_tag_of_one_to_three_alphanumerics()
    {
        var id = ProfileId.From("kdh");

        id.Value.Should().Be("KDH");
        id.ToString(System.Globalization.CultureInfo.InvariantCulture).Should().Be("KDH");
        ProfileId.From(" k.d.h ").Should().Be(id, "punctuation and whitespace are stripped like PoArgueJudge's Initials");

        FighterId.From("mlt").Value.Should().Be("MLT");
        FighterId.From("a1").Value.Should().Be("A1");
        FighterId.From("SELF").Value.Should().Be("SEL", "tags truncate to three characters — SELF is a WATCH concept, never a fighter");
        FighterId.TryFrom("K-D", out var kd).Should().BeTrue();
        kd.Value.Should().Be("KD", "non-alphanumerics are dropped before validation");
        FighterId.TryFrom("---", out _).Should().BeFalse();
        kd.Value.Should().Be("KD", "a tag already on the record still reads, however it was made");

        // A new tag is exactly three letters: what a person types to play, at either seat.
        Initials.IsTag("mah").Should().BeTrue("case does not matter");
        Initials.IsTag(" KSH ").Should().BeTrue("the edges are trimmed");
        Initials.IsTag("KD").Should().BeFalse("two letters is not a tag");
        Initials.IsTag("KDHX").Should().BeFalse("nor is four");
        Initials.IsTag("A1B").Should().BeFalse("letters only");
        Initials.IsTag("K-D").Should().BeFalse("nothing is dropped to make one fit");
        Initials.IsTag(null).Should().BeFalse();
    }
}
