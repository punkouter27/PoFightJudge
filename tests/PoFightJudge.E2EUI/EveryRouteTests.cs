using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using PoFightJudge.Api.Features.Records;
using PoFightJudge.Shared.Identifiers;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.E2EUI;

/// <summary>
/// Every route the app answers, at both shapes of screen, measured rather than eyeballed. The other tests in this
/// project each guard one property on the routes it cares about; this one is the sweep, so a page added later is
/// held to the same rules without anybody remembering to add it to five places.
///
/// It seeds an account first. Every list page renders an empty state on a fresh one, and an empty state fits any
/// screen, has no grid to overflow and no rows to get wrong — a sweep that skipped this would pass while looking at
/// almost nothing.
/// </summary>
[Collection(AppCollection.Name)]
public class EveryRouteTests(AppFixture app)
{
    private const string User = "e2e-sweep";

    /// <summary>A page may run over the viewport by this much before it counts as scrolling.</summary>
    private const int Slack = 24;

    /// <summary>
    /// Routes whose job is to ask something and get out of the way. These are held to the viewport on a phone: a
    /// screen that asks a question should be a screen, and a Start button below the fold is how a fight dies on a
    /// muted headset.
    /// </summary>
    private static readonly string[] Screens =
    [
        "/", "/cpu", "/1p", "/2p", "/login", "/watch/play", "/no-such-page",
    ];

    /// <summary>
    /// Routes whose job is to be a list. These are not held to the viewport, and deliberately so: eight profiles,
    /// twenty matches or a roster cannot be made to fit 844 px without either shrinking the rows past reading
    /// or paginating them, and both of those lose more than the scroll costs. They are held to everything else.
    /// </summary>
    private static readonly string[] Lists =
    [
        "/profiles", "/fighters", "/history", "/health",
    ];

    /// <summary>
    /// Both sets. The id-bearing routes (/fight/{id}, /verdict/{id}, /watch/replay/{id}, /v/{token}) are left to
    /// the flow tests, which have an argument to point them at.
    /// </summary>
    private static IEnumerable<string> Routes => [.. Screens, .. Lists];

    /// <summary>
    /// WCAG 2.2 SC 2.5.8 (AA): everything pressable on these clears 24 × 24 CSS px on a phone. Measured, not read off
    /// a stylesheet: Radzen sizes its buttons from <c>--rz-button-size-*</c> in a sheet <c>&lt;RadzenTheme&gt;</c>
    /// injects after app.css, so app.css's 44 px claim once lost and the session buttons measured 36×36.
    /// </summary>
    private static readonly string[] PressRoutes = ["/", "/cpu", "/2p", "/profiles", "/fighters", "/history"];

    private const int MinimumTarget = 24;

    [SkippableFact]
    public async Task Every_route_behaves_on_a_phone_and_on_a_desktop()
    {
        await SeedAsync();

        var problems = new List<string>();
        foreach (var (label, viewport, holdToTheViewport) in new[]
                 {
                     ("phone", AppFixture.Mobile, true),
                     ("desktop", AppFixture.Desktop, false),
                 })
        {
            foreach (var route in Routes)
            {
                var (context, page, errors) = await app.OpenAsync(viewport, route, user: User);
                await using var _ = context;
                await page.WaitForTimeoutAsync(1000);

                // As JSON rather than as a typed result: Playwright's own converter builds the target by activating
                // it and assigning properties, which is fussier than it is worth for a shape only this test uses.
                var json = await page.EvaluateAsync<string>(
                    @"() => JSON.stringify({
                        height: document.scrollingElement.scrollHeight,
                        width: document.scrollingElement.scrollWidth,
                        viewportHeight: window.innerHeight,
                        viewportWidth: window.innerWidth,
                        headings: [...document.querySelectorAll('h1,h2,h3,h4,h5,h6')].map(h => Number(h.tagName[1])),
                        sideways: [...document.querySelectorAll('*')]
                            .filter(e => !e.classList.contains('po-sr-only') && e.scrollWidth > e.clientWidth + 2 && e.clientWidth > 0 && getComputedStyle(e).overflowX !== 'auto' && getComputedStyle(e).overflowX !== 'scroll')
                            .slice(0, 3)
                            .map(e => e.tagName + '.' + String(e.className || '').split(' ').filter(c => !c.startsWith('b-')).join('.')),
                        small: [...document.querySelectorAll('button, a[href], [role=button]')]
                            .filter(e => !e.closest('.brand'))
                            .map(e => ({ e, r: e.getBoundingClientRect() }))
                            .filter(x => x.r.width > 0 && x.r.height > 0 && (x.r.width < " + MinimumTarget + " || x.r.height < " + MinimumTarget + @"))
                            .map(x => (x.e.innerText || x.e.getAttribute('aria-label') || x.e.className || '?').trim().split('\n')[0].slice(0, 24)
                                + ' ' + Math.round(x.r.width) + 'x' + Math.round(x.r.height)),
                    })");
                var measured = JsonSerializer.Deserialize<Measurements>(json, JsonSerializerOptions.Web)!;

                var where = $"[{label}] {route}";

                if (measured.Width > measured.ViewportWidth + 2)
                {
                    problems.Add($"{where}: scrolls sideways ({measured.Width} in {measured.ViewportWidth})");
                }

                // Upright, a screen is meant to be a screen. A list is meant to be a list, and a desktop can scroll.
                if (holdToTheViewport && Screens.Contains(route, StringComparer.Ordinal)
                    && measured.Height > measured.ViewportHeight + Slack)
                {
                    problems.Add($"{where}: {measured.Height}px in {measured.ViewportHeight}px — {measured.Height - measured.ViewportHeight}px over");
                }

                if (measured.Headings.Length == 0 || measured.Headings[0] != 1)
                {
                    problems.Add($"{where}: does not open on an h1");
                }

                for (var i = 1; i < measured.Headings.Length; i++)
                {
                    if (measured.Headings[i] > measured.Headings[i - 1] + 1)
                    {
                        problems.Add($"{where}: heading h{measured.Headings[i - 1]} → h{measured.Headings[i]}");
                    }
                }

                problems.AddRange(measured.Sideways.Select(s => $"{where}: {s} overflows its own box"));
                if (holdToTheViewport && PressRoutes.Contains(route, StringComparer.Ordinal))
                {
                    problems.AddRange(measured.Small.Select(s => $"{where}: {s} is under {MinimumTarget}px to press"));
                }

                problems.AddRange(errors.Take(2).Select(e => $"{where}: {e}"));
            }
        }

        problems.Should().BeEmpty("every route is held to the same rules, including the ones added after these were written");
    }


    private sealed class Measurements
    {
        public int Height { get; set; }

        public int Width { get; set; }

        public int ViewportHeight { get; set; }

        public int ViewportWidth { get; set; }

        public int[] Headings { get; set; } = [];

        public string[] Sideways { get; set; } = [];

        public string[] Small { get; set; } = [];
    }

    /// <summary>A cast, a fighter and both kinds of argument, so no page under test is looking at an empty state.</summary>
    private async Task SeedAsync()
    {
        var (context, page, _) = await app.OpenAsync(AppFixture.Desktop, "/profiles", user: User);
        await using var _ = context;
        var load = page.GetByRole(AriaRole.Button, new() { Name = "Load default cast" });
        await load.First.WaitForAsync(new() { Timeout = 30_000 });
        await load.First.ClickAsync();
        var confirm = page.Locator(".rz-dialog");
        await confirm.WaitForAsync(new() { Timeout = 10_000 });
        await confirm.GetByRole(AriaRole.Button, new() { Name = "Load cast" }).ClickAsync();
        await page.WaitForFunctionAsync("() => document.querySelectorAll('article.profile').length >= 8", null, new() { Timeout = 30_000 });

        var matches = app.Services.GetRequiredService<IMatchRepository>();
        var now = app.Services.GetRequiredService<TimeProvider>().GetUtcNow();
        await matches.UpsertAsync(new MatchDto(
            MatchId.New(), User, MatchMode.Watch, now.AddMinutes(-20), now.AddMinutes(-14), "the thermostat",
            MatchSide.Persona("MAH", "Married Husband"), MatchSide.Persona("KSH", "Karen"),
            SessionPhase.Done, SessionStatus.Ready, "Married Husband", "It was close.", IsFake: true));
        await matches.UpsertAsync(new MatchDto(
            MatchId.New(), User, MatchMode.Fight, now.AddMinutes(-9), now.AddMinutes(-3),
            "who forgot the bins and left them out all week",
            MatchSide.Human("SW1", "Alex"), MatchSide.Human("SW2", "Sam"),
            SessionPhase.Done, SessionStatus.Ready, "Alex", "One of them answered the question.", IsFake: true));
    }
}
