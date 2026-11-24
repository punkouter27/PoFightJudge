using Microsoft.Playwright;

namespace PoFightJudge.E2EUI;

[Collection(AppCollection.Name)]
public class WatchFlowTests(AppFixture app)
{
    [SkippableFact]
    public async Task Two_personas_argue_from_the_setup_screen_through_to_the_ruling()
    {
        var (context, page, errors) = await app.OpenAsync(AppFixture.Desktop, "/profiles", user: "e2e-watcher");
        await using var _ = context;

        await LoadTheCastAsync(page);

        await page.GotoAsync($"{app.BaseUrl}/cpu");
        await page.Locator("div.picker").First.WaitForAsync(new() { Timeout = 30_000 });

        await ChooseAsync(page, side: 0, "Donald");
        await ChooseAsync(page, side: 1, "Melania");

        await AppFixture.ToTheLastStepAsync(page);
        // A topic gives each of them a side: For / Against is offered, and both cards say which side they argue.
        await page.Locator("input[name=topic]").FillAsync("turning the heating up");
        await page.Locator("fieldset.sides").WaitForAsync(new() { Timeout = 10_000 });
        (await page.Locator("article.speaker .stance").AllInnerTextsAsync()).Select(t => t.Trim()).Should().Equal(["FOR", "AGAINST"]);
        await page.GetByRole(AriaRole.Button, new() { Name = "Start the argument" }).ClickAsync();

        // Six lines, spoken one after another, then the judge.
        await page.WaitForFunctionAsync($"() => {AppFixture.LinesSaid} >= 6", null, new() { Timeout = 90_000 });
        var verdict = page.Locator("section.verdict");
        await verdict.WaitForAsync(new() { Timeout = 60_000 });

        (await verdict.InnerTextAsync()).Should().NotBeNullOrWhiteSpace();
        (await page.EvaluateAsync<int>(AppFixture.LinesSaid)).Should().Be(6, "three rounds each, no slap");
        (await page.Locator("article.line").CountAsync()).Should().Be(0, "once the judge rules the last line is gone and the ruling has the floor (07f0e4b)");
        (await verdict.InnerTextAsync()).Should().ContainEquivalentOf("took it", "the ruling says who took it, and innerText carries the display font uppercase");
        if (await verdict.Locator("h2").InnerTextAsync() is var headline && !headline.Contains("NOBODY", StringComparison.OrdinalIgnoreCase))
        {
            (await verdict.Locator(".winning-side").InnerTextAsync()).Should().MatchRegex("(?i)^(for|against) wins$", "a sided argument says which side carried it");
        }

        await WatchHardeningTests.ShootAsync(page, "watch-verdict");

        // A rematch starts a fresh argument rather than appending to the one just judged.
        await page.GetByRole(AriaRole.Button, new() { Name = "Argue again" }).ClickAsync();
        await page.WaitForFunctionAsync(
            $"() => document.querySelectorAll('section.verdict').length === 0 && {AppFixture.LinesSaid} <= 2",
            null,
            new() { Timeout = 30_000 });

        // The matchup lives in the tab, not only in memory: a reload plays the same two again rather than dumping
        // whoever was watching back at the setup screen.
        await page.ReloadAsync();
        await page.Locator("article.line").First.WaitForAsync(new() { Timeout = 90_000 });
        page.Url.Should().EndWith("/watch/play");

        errors.Should().BeEmpty();
    }

    [SkippableFact]
    public async Task A_person_takes_a_side_and_the_argument_waits_for_them()
    {
        var (context, page, errors) = await app.OpenAsync(AppFixture.Desktop, "/profiles", user: "e2e-arguer");
        await using var _ = context;

        await AppFixture.ListenForLinesAsync(page);

        // Every microphone stream the page is handed is kept, so the test can stop talking later on.
        await page.AddInitScriptAsync(
            "window.__streams = []; const real = navigator.mediaDevices.getUserMedia.bind(navigator.mediaDevices);"
            + " navigator.mediaDevices.getUserMedia = async c => { const s = await real(c); window.__streams.push(s); return s; };");

        await LoadTheCastAsync(page);

        await page.GotoAsync($"{app.BaseUrl}/1p");
        await page.Locator("div.picker").First.WaitForAsync(new() { Timeout = 30_000 });

        // 1P asks one thing: who you are arguing with. Donald is a husband, so the wife's seat is yours.
        await ChooseAsync(page, side: 0, "Donald");
        await page.Locator("input[name=yourTag]").FillAsync("KDX");

        await AppFixture.ToTheLastStepAsync(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Start the argument" }).ClickAsync();

        var turn = page.Locator("section.your-turn");
        await turn.WaitForAsync(new() { Timeout = 90_000 });
        (await page.Locator("article.line").CountAsync()).Should().Be(1, "the argument stops at the person's turn rather than speaking for them");

        // A stray tap on the logo mid-argument asks first, and "Stay" means nothing happened.
        await page.Locator("header.po-header a.po-header__title").ClickAsync();
        var asking = page.Locator(".rz-dialog", new() { HasText = "Leave the argument?" });
        await asking.WaitForAsync(new() { Timeout = 10_000 });
        await asking.GetByRole(AriaRole.Button, new() { Name = "Stay" }).ClickAsync();
        await asking.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 10_000 });
        page.Url.Should().EndWith("/watch/play");

        // Spoken, not typed, and nothing to press: the turn opens the microphone itself, the fake device plays the
        // fixture into it, and a pause is what ends it. The fixture never pauses, so the test goes quiet the way a
        // person does, after a second and a half of talking: the track is muted, and the words then send themselves.
        await turn.Locator(".meter--on").WaitForAsync(new() { Timeout = 30_000 });
        (await turn.Locator("textarea[name=line]").CountAsync()).Should().Be(0, "while it listens there is no box taking up the panel");
        await WatchHardeningTests.ShootAsync(page, "watch-turn-listening");
        await page.WaitForTimeoutAsync(1_500);
        await page.EvaluateAsync("() => window.__streams.forEach(s => s.getAudioTracks().forEach(t => { t.enabled = false; }))");

        await page.WaitForFunctionAsync($"() => {AppFixture.LinesSaid} >= 3", null, new() { Timeout = 60_000 });
        var theirs = (await AppFixture.SaidAsync(page)).Should().Contain(l => l.Contains("KDX", StringComparison.OrdinalIgnoreCase), "the person's line was shown under their own tag").Subject;
        theirs.Should().MatchRegex("(?i)rich, coming from you|raised yours first|last weekend|know you are losing", "what the transcriber heard is what went on the record");

        errors.Should().BeEmpty();
    }

    private static async Task LoadTheCastAsync(IPage page)
    {
        var load = page.GetByRole(AriaRole.Button, new() { Name = "Load default cast" });
        await load.First.WaitForAsync(new() { Timeout = 30_000 });
        await load.First.ClickAsync();
        var confirm = page.Locator(".rz-dialog");
        await confirm.WaitForAsync(new() { Timeout = 10_000 });
        await confirm.GetByRole(AriaRole.Button, new() { Name = "Load cast" }).ClickAsync();
        await page.WaitForFunctionAsync("() => document.querySelectorAll('article.profile').length >= 6", null, new() { Timeout = 30_000 });
    }

    /// <summary>Picks an option out of one side's dropdown; side 0 is the husband, side 1 the wife.</summary>
    private static async Task ChooseAsync(IPage page, int side, string option)
    {
        await page.Locator("div.picker").Nth(side).Locator(".rz-dropdown").ClickAsync();
        var panel = page.Locator(".rz-dropdown-panel:visible");
        await panel.WaitForAsync(new() { Timeout = 10_000 });
        await panel.Locator(".rz-dropdown-item", new() { HasText = option }).First.ClickAsync();
        await panel.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 10_000 });
    }
}
