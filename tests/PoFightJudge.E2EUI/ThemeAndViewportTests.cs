using Microsoft.Playwright;

namespace PoFightJudge.E2EUI;

[Collection(AppCollection.Name)]
public class ThemeAndViewportTests(AppFixture app)
{
    [SkippableFact]
    public async Task Unauthenticated_visitor_is_sent_to_login_and_guest_returns_them_home()
    {
        var (context, page, errors) = await app.OpenAsync(AppFixture.Desktop, "/", user: null);
        await using var _ = context;

        var guest = page.GetByRole(AriaRole.Button, new() { Name = "Continue as Guest (Dev)" });
        await guest.WaitForAsync(new() { Timeout = 30_000 });
        page.Url.Should().EndWith("/login?returnUrl=%2F", "the protected home page redirects and remembers where to return");
        // Signed out: the sign-in page is the title and one button (this Test build's is the guest door; Production's is
        // Microsoft's), and there is no navigation.
        (await page.Locator("main h1").TextContentAsync())!.Trim().Should().Be("PoFightJudge");
        (await page.Locator("main button, main a[href]").CountAsync()).Should().Be(1, "the sign-in page is the title and one button");
        (await page.Locator("nav").CountAsync()).Should().Be(0, "no navigation until signed in");
        await guest.ClickAsync();

        await page.Locator("article.channel").First.WaitForAsync(new() { Timeout = 30_000 });
        page.Url.Should().Be(app.BaseUrl + "/");
        (await page.Locator("article.channel").CountAsync()).Should().Be(5, "CPU, 1P and 2P, then profiles and the leaderboard");
        (await page.Locator("nav.po-bottom-nav").CountAsync()).Should().Be(1, "signed in, the navigation appears");
        (await page.Locator("header.po-header .po-header__email").InnerTextAsync()).Should().Contain("GUEST-", "a guest has no address, so the header shows its id");
        errors.Should().BeEmpty();
    }

    [SkippableFact]
    public async Task Theme_follows_the_device_and_the_settings_persist_across_reload()
    {
        var (context, page, errors) = await app.OpenAsync(AppFixture.Desktop, "/login", effects: false);
        await using var _ = context;

        // No switch and no attribute: the palette and the browser chrome follow prefers-color-scheme (Po web standard).
        const string Chrome = "document.querySelector('meta[name=theme-color]').content";
        const string Page = "getComputedStyle(document.documentElement).backgroundColor";
        await page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Light });
        await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
        await AppFixture.WaitForAppAsync(page, errors);
        (await page.EvaluateAsync<string?>("document.documentElement.getAttribute('data-theme')")).Should().BeNull("there is no theme switch");
        (await page.EvaluateAsync<string>(Chrome)).Should().Be("#f6f7fb");
        (await page.EvaluateAsync<string>(Page)).Should().Be("rgb(246, 247, 251)");
        await page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Dark });
        await page.WaitForFunctionAsync($"() => {Page} === 'rgb(15, 17, 23)'", null, new() { Timeout = 5_000 });
        await page.WaitForFunctionAsync($"() => {Chrome} === '#0f1117'", null, new() { Timeout = 5_000 });
        await page.WaitForFunctionAsync("() => document.getElementById('radzen-theme').href.endsWith('/material-dark-base.css')", null, new() { Timeout = 5_000 });

        // Sound has no switch: a mute and a level an older build kept are overwritten, so it plays loud and unmuted.
        await page.EvaluateAsync("() => { localStorage.setItem('pofightjudge:sound', 'off'); window.PoSfx.setVolume(0.4); }");
        await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
        await AppFixture.WaitForAppAsync(page, errors);
        await page.WaitForFunctionAsync("() => window.PoSfx.volume() === 1", null, new() { Timeout = 5_000 });
        errors.Should().BeEmpty();
    }
}
