using Microsoft.Playwright;

namespace PoFightJudge.E2EUI;

/// <summary>
/// The WATCH case the two flow tests do not reach: the person arguing first rather than second, on a phone-sized
/// screen. The ruling it used to photograph is now part of <see cref="WatchFlowTests"/>' CPU flow.
/// </summary>
[Collection(AppCollection.Name)]
public class WatchHardeningTests(AppFixture app)
{
    private static readonly string ShotsDirectory = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "screenshots");

    [SkippableFact]
    public async Task A_person_can_open_the_argument_and_the_screen_fits_a_phone()
    {
        var (context, page, errors) = await app.OpenAsync(AppFixture.Mobile, "/profiles", user: "e2e-opener");
        await using var _ = context;
        await AppFixture.ListenForLinesAsync(page);

        // Listening is voice only, so a typed turn is the one whose microphone could not open: refuse it here.
        await page.AddInitScriptAsync(
            "navigator.mediaDevices.getUserMedia = () => Promise.reject(new DOMException('refused', 'NotAllowedError'));");

        await SeedAsync(page);
        await page.GotoAsync($"{app.BaseUrl}/1p");
        await page.Locator("div.picker").First.WaitForAsync(new() { Timeout = 30_000 });

        // Arguing with a wife leaves the husband's seat, which opens the argument: the very first turn is theirs.
        await ChooseAsync(page, side: 0, "Melania");
        await page.Locator("input[name=yourTag]").FillAsync("ABE");
        await AppFixture.ToTheLastStepAsync(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Start the argument" }).ClickAsync();

        var turn = page.Locator("section.your-turn");
        await turn.WaitForAsync(new() { Timeout = 60_000 });
        (await page.Locator("article.line").CountAsync()).Should().Be(0, "nobody has spoken yet");
        (await page.Locator("div.rows[aria-busy=true]").CountAsync())
            .Should().Be(0, "waiting for a person is not the page loading, and a spinner would say it was");

        var scrollWidth = await page.EvaluateAsync<int>("document.scrollingElement.scrollWidth");
        var innerWidth = await page.EvaluateAsync<int>("window.innerWidth");
        scrollWidth.Should().BeLessThanOrEqualTo(innerWidth, "the play screen must never scroll sideways at 390 px");

        // The microphone was refused (see the init script above), so the turn is typed: the box is there already.
        await turn.Locator("textarea[name=line]").FillAsync("We are not having this argument again.");
        // From the keyboard: somebody who typed their answer should not have to find the button to give it.
        await turn.GetByRole(AriaRole.Button, new() { Name = "Say it" }).WaitForAsync(new() { Timeout = 10_000 });
        await turn.Locator("textarea[name=line]").PressAsync("Control+Enter");
        await page.WaitForFunctionAsync($"() => {AppFixture.LinesSaid} >= 2", null, new() { Timeout = 60_000 });
        (await AppFixture.SaidAsync(page)).Should().Contain(l => l.Contains("not having this argument", StringComparison.Ordinal), "what they typed is what was put on screen");

        errors.Should().BeEmpty();
    }

    /// <summary>A full-page photograph into docs/screenshots (ignored by git), once the icon font has landed.</summary>
    internal static async Task ShootAsync(IPage page, string name)
    {
        Directory.CreateDirectory(ShotsDirectory);

        // Without this the icon font can still be loading and every icon photographs as its ligature name.
        await page.EvaluateAsync("() => document.fonts.ready");
        await page.ScreenshotAsync(new() { Path = Path.Combine(ShotsDirectory, $"{name}.png"), FullPage = true });
    }

    private static async Task SeedAsync(IPage page)
    {
        var load = page.GetByRole(AriaRole.Button, new() { Name = "Load default cast" });
        await load.First.WaitForAsync(new() { Timeout = 30_000 });
        await load.First.ClickAsync();
        var confirm = page.Locator(".rz-dialog");
        await confirm.WaitForAsync(new() { Timeout = 10_000 });
        await confirm.GetByRole(AriaRole.Button, new() { Name = "Load cast" }).ClickAsync();
        await page.WaitForFunctionAsync("() => document.querySelectorAll('article.profile').length >= 6", null, new() { Timeout = 30_000 });
    }

    private static async Task ChooseAsync(IPage page, int side, string option)
    {
        await page.Locator("div.picker").Nth(side).Locator(".rz-dropdown").ClickAsync();
        var panel = page.Locator(".rz-dropdown-panel:visible");
        await panel.WaitForAsync(new() { Timeout = 10_000 });
        await panel.Locator(".rz-dropdown-item", new() { HasText = option }).First.ClickAsync();
        await panel.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 10_000 });
    }
}
