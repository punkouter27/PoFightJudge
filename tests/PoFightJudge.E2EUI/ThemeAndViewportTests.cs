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
        await guest.ClickAsync();

        await page.Locator("article.channel").First.WaitForAsync(new() { Timeout = 30_000 });
        page.Url.Should().Be(app.BaseUrl + "/");
        (await page.Locator("article.channel").CountAsync()).Should().Be(5, "CPU, 1P and 2P, then profiles and the leaderboard");
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

        // The volume is the browser's to keep, and one slider sets it for effects and voices both.
        await page.GetByRole(AriaRole.Link, new() { Name = "Settings" }).ClickAsync();
        await page.Locator(".po-volume").WaitForAsync(new() { Timeout = 10_000 });
        await page.EvaluateAsync("() => window.PoSfx.setVolume(0.4)");

        // Stage effects are off until asked for, and kept.
        // Radzen's switch is a visually hidden checkbox (role=switch) inside the span a person actually clicks.
        var effects = page.Locator(".po-settings .rz-switch:has(input[aria-label='Stage effects'])");
        (await effects.Locator("input").GetAttributeAsync("aria-checked")).Should().Be("false", "the simplest stage is the default");
        await effects.ClickAsync();
        await page.WaitForFunctionAsync("() => localStorage.getItem('pofightjudge:effects') === 'on'", null, new() { Timeout = 5_000 });

        await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
        await AppFixture.WaitForAppAsync(page, errors);
        (await page.EvaluateAsync<double>("window.PoSfx.volume()")).Should().Be(0.4);
        (await page.EvaluateAsync<bool>("window.PoMotion.still()")).Should().BeFalse("the switch was turned on before the reload");
        (await page.Locator(".po-volume input, .po-volume [role=slider]").First.GetAttributeAsync("aria-valuenow") ?? await page.Locator(".po-volume input").First.InputValueAsync())
            .Should().Be("40", "the slider opens where it was left");
        errors.Should().BeEmpty();
    }
}
