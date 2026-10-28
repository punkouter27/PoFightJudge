using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using PoFightJudge.Api.Features.Fighters;
using PoFightJudge.Api.Features.Records;
using PoFightJudge.Shared.Identifiers;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.E2EUI;

/// <summary>
/// The list pages at 390 px. A RadzenDataGrid whose columns do not fit puts a horizontal scrollbar inside the page,
/// which on a phone is a table read by dragging sideways: /history's fixed widths alone came to 36rem before its
/// two flexible columns, and /fighters' to 46rem.
///
/// The rows are written straight into the store. An empty grid fits any screen, so a version of this test that did
/// not seed would have passed while saying nothing.
/// </summary>
[Collection(AppCollection.Name)]
public class GridOnAPhoneTests(AppFixture app)
{
    private const string User = "e2e-narrow";

    [SkippableFact]
    public async Task No_grid_scrolls_sideways_on_a_phone()
    {
        await RecordAsync(MatchMode.Watch, "the thermostat", MatchSide.Persona("MAH", "Married Husband"), MatchSide.Persona("KSH", "Karen"));
        await RecordAsync(MatchMode.Fight, "who forgot the bins and left them there all week", MatchSide.Human("HA1", "Alex"), MatchSide.Human("HA2", "Sam"));
        // A second, so both have the two games the leaderboard asks for and its board is drawn, not its empty state.
        await RecordAsync(MatchMode.Fight, "the thermostat, again", MatchSide.Human("HA1", "Alex"), MatchSide.Human("HA2", "Sam"));

        var offenders = new List<string>();
        foreach (var route in new[] { "/history", "/fighters", "/status", "/vs/HA1/HA2" })
        {
            var (context, page, _) = await app.OpenAsync(AppFixture.Mobile, route, user: User);
            await using var _ = context;
            await page.WaitForTimeoutAsync(1200);

            if (string.Equals(route, "/fighters", StringComparison.Ordinal))
            {
                // Somebody is on the card, or this page is an empty state and says nothing about its grid.
                (await page.Locator(".roster .po-stacked").CountAsync()).Should().BeGreaterThan(0, "two people have fought");
                (await page.Locator(".board tbody tr").CountAsync()).Should().BeGreaterThan(0, "both have two games, so the board is drawn");
            }

            if (route.StartsWith("/vs/", StringComparison.Ordinal))
            {
                (await page.Locator("section.tale").InnerTextAsync()).Should().ContainEquivalentOf("Alex").And.ContainEquivalentOf("leads");
                var width = await page.EvaluateAsync<int>("document.scrollingElement.scrollWidth - window.innerWidth");
                width.Should().BeLessThanOrEqualTo(0, "the series fits a phone");
            }

            if (Environment.GetEnvironmentVariable("SHOTS") is { Length: > 0 } shots)
            {
                await page.ScreenshotAsync(new() { Path = Path.Combine(shots, route.Trim('/').Replace('/', '-') + "-seeded-phone.png"), FullPage = true });
            }

            var wide = await page.EvaluateAsync<string[]>(
                @"() => [...document.querySelectorAll('.rz-data-grid-data, .rz-grid-table, table')]
                    .filter(e => e.scrollWidth > e.clientWidth + 2 && e.clientWidth > 0)
                    .map(e => (e.className || e.tagName).split(' ')[0] + ' ' + e.scrollWidth + '>' + e.clientWidth)");
            offenders.AddRange(wide.Select(w => $"{route}: {w}"));
        }

        offenders.Should().BeEmpty("a phone reads a list by scrolling down it, not across it");
    }

    private async Task RecordAsync(MatchMode mode, string topic, MatchSide one, MatchSide two)
    {
        var matches = app.Services.GetRequiredService<IMatchRepository>();
        var now = app.Services.GetRequiredService<TimeProvider>().GetUtcNow();
        var id = MatchId.New();
        await matches.UpsertAsync(new MatchDto(
            id, User, mode, now.AddMinutes(-6), now, topic, one, two,
            SessionPhase.Done, SessionStatus.Ready, one.DisplayName, "It was close.", IsFake: true));

        // People, not personas, are on the fighters' card: they need to exist and to have a result each.
        if (!one.IsHuman || !two.IsHuman)
        {
            return;
        }

        var fighters = app.Services.GetRequiredService<IFighterRepository>();
        var results = app.Services.GetRequiredService<IFighterResultRepository>();
        await fighters.EnsureAsync(FighterId.From(one.Id), now, role: null);
        await fighters.EnsureAsync(FighterId.From(two.Id), now, role: null);
        await fighters.RenameAsync(FighterId.From(one.Id), one.DisplayName);
        await fighters.RenameAsync(FighterId.From(two.Id), two.DisplayName);
        await results.SaveAsync(
        [
            new FighterResultDto(one.Id, User, id, mode, now, topic, two.Id, true, false, 72, StyleSnapshot.Empty),
            new FighterResultDto(two.Id, User, id, mode, now, topic, one.Id, false, false, 58, StyleSnapshot.Empty),
        ]);
    }
}
