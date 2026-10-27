using PoFightJudge.Client.Pages;

namespace PoFightJudge.Unit.Client;

/// <summary>
/// A ruling that has only just come in is revealed — a drumroll, then the name. One opened from the history a day
/// later is a document, and is simply shown: a drumroll every time somebody looks something up would be a toll.
/// </summary>
public class VerdictRevealTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 21, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Only_a_fresh_ruling_is_staged()
    {
        foreach (var (minutesOld, staged) in new[] { (0, true), (14, true), (16, false), (60 * 24, false) })
        {
            Verdict.Staged(Now.AddMinutes(-minutesOld), Now).Should().Be(staged, $"{minutesOld} minutes old");
        }
    }
}
