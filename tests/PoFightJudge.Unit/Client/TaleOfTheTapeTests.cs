using Bunit;
using Microsoft.Extensions.DependencyInjection;
using PoFightJudge.Client.Components;
using PoFightJudge.Shared.Models;
using Radzen;

namespace PoFightJudge.Unit.Client;

/// <summary>The fight card before the bell: the two records side by side, and the one line about their series.</summary>
public class TaleOfTheTapeTests : BunitContext
{
    public TaleOfTheTapeTests()
    {
        Services.AddRadzenComponents();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void The_series_is_one_line()
    {
        TaleOfTheTape.Series(Meeting(0, 0, 0), "AL", "SM").Should().Be("First time these two have met.");
        TaleOfTheTape.Series(Meeting(4, 3, 1), "AL", "SM").Should().Be("AL leads the series 3–1.");
        TaleOfTheTape.Series(Meeting(4, 1, 3), "AL", "SM").Should().Be("SM leads the series 3–1.");
        TaleOfTheTape.Series(Meeting(5, 2, 2), "AL", "SM").Should().Be("Level at 2–2, with 1 drawn.");
    }

    [Fact]
    public void Somebody_new_is_said_to_be_new_rather_than_shown_as_a_row_of_zeroes()
    {
        var veteran = FighterStatsDto.Empty("AL", "Al") with { Fights = 6, Wins = 4, Losses = 2, Streak = 2 };
        var cut = Render<TaleOfTheTape>(p => p
            .Add(t => t.Player1, "AL")
            .Add(t => t.Player2, "SM")
            .Add(t => t.One, veteran with { Rating = 1048 })
            .Add(t => t.Two, null));

        var rows = cut.FindAll(".row").Select(r => r.TextContent).ToList();
        rows.Should().Contain(r => r.Contains("1048") && r.Contains("Rating") && r.Contains("New"));
        rows.Should().Contain(r => r.Contains("4–2") && r.Contains("Record"));
        rows.Should().Contain(r => r.Contains("W2") && r.Contains("Run"));
    }

    private static HeadToHeadDto Meeting(int meetings, int wins, int losses) =>
        new("AL", "Al", "SM", "Sam", meetings, wins, losses, meetings - wins - losses, 0, 0, []);
}
