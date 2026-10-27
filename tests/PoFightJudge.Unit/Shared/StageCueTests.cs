using PoFightJudge.Shared.Models;

namespace PoFightJudge.Unit.Shared;

/// <summary>
/// A generated line ends with a stage cue — "(smug)", "(seething)" — that the voice acts out. On screen it is
/// noise: the mood chip already says it, so the transcript shows the words alone.
/// </summary>
public class StageCueTests
{
    [Fact]
    public void Shown_drops_the_cue_and_tidies_the_gap_but_never_blanks_a_line_that_is_only_a_cue()
    {
        foreach (var (line, shown) in new[]
        {
            ("Put down the phone. (smug)", "Put down the phone."), ("That is inconvenient.(frustrated)", "That is inconvenient."),
            ("(coldly) You again.", "You again."), ("Fine (snaps) — whatever you say.", "Fine — whatever you say."),
            ("No cue here.", "No cue here."), ("(sighs)", "(sighs)"),
        })
        {
            WatchTurns.Shown(line).Should().Be(shown);
        }
    }
}
