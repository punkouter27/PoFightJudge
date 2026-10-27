using Blazored.LocalStorage;
using Bunit;
using Bunit.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using NSubstitute;
using PoFightJudge.Client.Components;
using PoFightJudge.Client.Pages;
using PoFightJudge.Client.Services;
using PoFightJudge.Shared.Identifiers;
using PoFightJudge.Shared.Models;
using Radzen;
using Radzen.Blazor;
using FightersPage = PoFightJudge.Client.Pages.Fighters;
using ProfilesPage = PoFightJudge.Client.Pages.Profiles;

namespace PoFightJudge.Unit.Client;

/// <summary>
/// The history page: two kinds of argument in one list, and the one destructive thing in the app.
/// </summary>
/// <summary>
/// The history page: two kinds of argument in one list, and the one destructive thing in the app.
/// </summary>
public class HistoryPageTests : BunitContext
{
    private static readonly DateTimeOffset Night = new(2026, 9, 1, 20, 0, 0, TimeSpan.Zero);

    private readonly IApiClient _api = Substitute.For<IApiClient>();
    private readonly MatchId _watch = MatchId.New();
    private readonly MatchId _fight = MatchId.New();

    public HistoryPageTests()
    {
        Services.AddRadzenComponents();
        // The setup screens open on the last card played; a substitute storage means every test opens blank.
        Services.AddSingleton(Substitute.For<ILocalStorageService>());
        Services.AddScoped<SetupMemory>();
        Services.AddSingleton(_api);
        // Under bunit JS is loose, so the viewport reports the wide layout — the one that declares every column.
        Services.AddScoped<Viewport>();
        // T113: the aurora mounts through the real GfxInterop. Under bunit's loose JSInterop the call throws
        // JSException, which MountAsync swallows and answers false — the same shape the real interop gives on
        // a no-WebGL2 browser, and no substitute required.
        Services.AddScoped<GfxInterop>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _api.GetMatchPageAsync(Arg.Any<MatchQuery>(), Arg.Any<CancellationToken>()).Returns(_ => Page(Both()));
    }

    /// <summary>One page holding everything given to it: the tests are about the screen, not about paging arithmetic.</summary>
    private static MatchPageDto Page(IReadOnlyList<MatchDto> matches) =>
        new(matches, matches.Count, 0, MatchQuery.DefaultTake);

    private IReadOnlyList<MatchDto> Both() =>
    [
        Match(_watch, MatchMode.Watch, "the thermostat", MatchSide.Persona("MAH", "Married Husband"), MatchSide.Persona("KSH", "Karen")),
        Match(_fight, MatchMode.Fight, "who forgot the bins", MatchSide.Human("AB"), MatchSide.Human("CD")),
    ];

    private static MatchDto Match(MatchId id, MatchMode mode, string topic, MatchSide one, MatchSide two) =>
        new(id, "u", mode, Night, Night.AddMinutes(6), topic, one, two, SessionPhase.Done, SessionStatus.Ready, one.DisplayName, "It was close.", IsFake: false);

    /// <summary>
    /// The page under a dialog host, because the confirm the delete button raises is part of what is being tested:
    /// answering it through a service hook would test the mock rather than the button somebody presses.
    /// </summary>
    private IRenderedComponent<ContainerFragment> RenderHistory()
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<RadzenDialog>(0);
            builder.CloseComponent();
            builder.OpenComponent<History>(1);
            builder.CloseComponent();
        });

        cut.WaitForAssertion(() => cut.FindAll(".history .match").Should().HaveCount(2));
        return cut;
    }

    /// <summary>Answers the confirm the way a person would: by pressing one of its two buttons.</summary>
    private static async Task AnswerConfirmAsync(IRenderedComponent<ContainerFragment> cut, bool answer)
    {
        await cut.WaitForAssertionAsync(() => cut.FindAll(".rz-dialog button").Should().NotBeEmpty());
        var button = cut.FindAll(".rz-dialog button")
            .First(b => b.TextContent.Contains(answer ? "Delete" : "Keep it", StringComparison.Ordinal));
        await button.ClickAsync(new());
    }

    [Fact]
    public async Task Both_kinds_of_argument_are_listed_on_one_page_and_the_dates_and_the_order_wait_behind_one_filters_button()
    {
        var cut = RenderHistory();

        cut.FindComponents<ModeBadge>().Select(b => b.Instance.Mode)
            .Should().BeEquivalentTo([MatchMode.Watch, MatchMode.Fight]);
        cut.Markup.Should().Contain("the thermostat").And.Contain("who forgot the bins");
        cut.FindAll(".rz-pager").Should().BeEmpty("two arguments fit on the first page");

        _api.ReceivedCalls().Should().Contain(c => c.GetArguments().OfType<MatchQuery>().Any(q => q.Sort == MatchSort.Newest));
        MatchQuery.DefaultTake.Should().BePositive();
        new MatchQuery(Sort: MatchSort.TopicAscending).Order(Both()).Select(m => m.Topic).Should().Equal("the thermostat", "who forgot the bins");
        new MatchQuery(Sort: MatchSort.TopicDescending).Order(Both()).Select(m => m.Topic).Should().Equal("who forgot the bins", "the thermostat");

        // The dates and the order wait behind one filters button, and the order is asked of the server.
        cut.FindAll(".filters").Should().BeEmpty("one row of controls until somebody wants more");

        await cut.FindAll(".narrow .rz-button").Single(b => b.TextContent.Contains("Filters", StringComparison.Ordinal)).ClickAsync(new());
        cut.FindAll(".filters .rz-datepicker").Should().HaveCount(2);

        await cut.FindComponent<RadzenDropDown<MatchSort>>().InvokeAsync(() =>
            cut.FindComponent<RadzenDropDown<MatchSort>>().Instance.Change.InvokeAsync(MatchSort.TopicAscending));

        await cut.WaitForAssertionAsync(() => _api.ReceivedCalls().Should()
            .Contain(c => c.GetArguments().OfType<MatchQuery>().Any(q => q.Sort == MatchSort.TopicAscending)));
    }

    [Fact]
    public async Task A_yes_deletes_the_argument_and_takes_the_row_with_it()
    {
        var cut = RenderHistory();

        var clicked = cut.Find(".match button[aria-label='Delete the thermostat']").ClickAsync(new());
        await AnswerConfirmAsync(cut, answer: true);
        await clicked;

        await _api.Received(1).DeleteMatchAsync(_watch, Arg.Any<CancellationToken>());
        await cut.WaitForAssertionAsync(() => cut.FindAll(".history .match").Should().HaveCount(1));
        cut.Markup.Should().NotContain("the thermostat");

        // An argument with no topic says so rather than leaving a blank.
        _api.GetMatchPageAsync(Arg.Any<MatchQuery>(), Arg.Any<CancellationToken>()).Returns(_ => Page(
        [
            Match(_watch, MatchMode.Watch, string.Empty, MatchSide.Persona("MAH", "Married Husband"), MatchSide.Persona("KSH", "Karen")),
        ]));

        var open = Render<History>();

        await open.WaitForAssertionAsync(() => open.Markup.Should().Contain("an open topic"));
        open.FindAll("button[aria-label='Delete an open topic']").Should().ContainSingle();
    }
}

/// <summary>The standings, on the two pages that list the people they rank.</summary>
/// <summary>The standings, on the two pages that list the people they rank.</summary>
public class StandingsTests : BunitContext, IAsyncLifetime
{
    private readonly IApiClient _api = Substitute.For<IApiClient>();

    public StandingsTests()
    {
        Services.AddRadzenComponents();
        Services.AddSingleton(Substitute.For<ILocalStorageService>());
        Services.AddScoped<SetupMemory>();
        Services.AddSingleton(_api);
        Services.AddSingleton(TimeProvider.System);
        // A cast card previews the persona's voice, so the page needs the interop even though nothing here clicks it.
        Services.AddScoped<AudioInterop>();
        Services.AddScoped<SfxInterop>();
        // Under bunit JS is loose, so the viewport reports the wide layout — the one that declares every column.
        Services.AddScoped<Viewport>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _api.GetLeaderboardAsync(Arg.Any<CancellationToken>())
            .Returns([Row("MAH", "Married Husband", 5, 4), Row("AB", "Alex", 3, 2) with { IsPerson = true }, Row("KSH", "Karen", 5, 1)]);
        _api.GetProfilesAsync(Arg.Any<CancellationToken>()).Returns([]);
        _api.GetRosterAsync(Arg.Any<CancellationToken>()).Returns([]);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>A cast card holds an AudioInterop, which is released asynchronously; bunit's teardown has to be awaited.</summary>
    public new async Task DisposeAsync() => await base.DisposeAsync().ConfigureAwait(false);

    private static LeaderboardRowDto Row(string id, string name, int matches, int wins) =>
        new(id, name, matches, wins, (double)wins / matches, 61.5);

    [Fact]
    public async Task The_leaderboard_ranks_people_and_the_cast_on_one_board_and_marks_the_cast()
    {
        _api.GetRosterAsync(Arg.Any<CancellationToken>()).Returns([FighterStatsDto.Empty("AB", "Alex") with { Fights = 3, Wins = 2, Losses = 1 }]);

        var cut = Render<FightersPage>();
        await cut.WaitForAssertionAsync(() => cut.Find("h1").TextContent.Should().Be("Leaderboard"));
        await cut.WaitForAssertionAsync(() => cut.FindComponent<Board>().Instance.Rows.Select(r => r.DisplayName)
            .Should().Equal(["Married Husband", "Alex", "Karen"], "one board, in the order the server ranked it"));
        cut.FindAll(".board .cast-mark").Should().HaveCount(2, "the two personas are marked; the person is not");
        // Four columns, so it fits a narrow window without scrolling sideways or squeezing the names out.
        cut.FindAll(".board thead th").Select(th => th.TextContent.Trim()).Should().Equal(["#", "Who", "Rating", "Record"]);
        cut.Find(".board tbody tr").TextContent.Should().Contain("4 of 5").And.Contain("80%");
        cut.FindAll("[aria-label='Whose standings']").Should().BeEmpty("there is no switch between two boards");
    }
}
