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
        await page.GetByRole(AriaRole.Button, new() { Name = "Settings" }).ClickAsync();
        (await page.Locator(".po-settings .user").InnerTextAsync()).Should().Contain("GUEST-");
        errors.Should().BeEmpty();
    }

    [SkippableFact]
    public async Task Theme_toggle_persists_across_reload()
    {
        var (context, page, errors) = await app.OpenAsync(AppFixture.Desktop, "/login", effects: false);
        await using var _ = context;

        (await page.EvaluateAsync<string?>("document.documentElement.getAttribute('data-theme')")).Should().Be("dark", "dark is the default");

        await page.GetByRole(AriaRole.Button, new() { Name = "Settings" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Switch to light theme" }).ClickAsync();
        await page.WaitForFunctionAsync("() => document.documentElement.getAttribute('data-theme') === 'light'");
        const string Chrome = "document.querySelector('meta[name=theme-color]').content";
        await page.WaitForFunctionAsync($"() => {Chrome} === '#f4f1ea'", null, new() { Timeout = 5_000 });

        // The volume is the browser's to keep, like the theme, and one slider sets it for effects and voices both.
        await page.Locator(".po-volume").WaitForAsync(new() { Timeout = 10_000 });
        await page.EvaluateAsync("() => window.PoSfx.setVolume(0.4)");

        // Stage effects are off until asked for, and kept like the theme.
        // Radzen's switch is a visually hidden checkbox (role=switch) inside the span a person actually clicks.
        var effects = page.Locator(".po-settings .rz-switch:has(input[aria-label='Stage effects'])");
        (await effects.Locator("input").GetAttributeAsync("aria-checked")).Should().Be("false", "the simplest stage is the default");
        await effects.ClickAsync();
        await page.WaitForFunctionAsync("() => localStorage.getItem('pofightjudge:effects') === 'on'", null, new() { Timeout = 5_000 });

        await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
        await AppFixture.WaitForAppAsync(page, errors);
        (await page.EvaluateAsync<string?>("document.documentElement.getAttribute('data-theme')")).Should().Be("light");
        (await page.EvaluateAsync<string?>("localStorage.getItem('pofightjudge:theme')")).Should().Be("light");
        (await page.EvaluateAsync<string>(Chrome)).Should().Be("#f4f1ea", "the browser's own chrome is light before the app has even started");
        (await page.EvaluateAsync<double>("window.PoSfx.volume()")).Should().Be(0.4);
        (await page.EvaluateAsync<bool>("window.PoMotion.still()")).Should().BeFalse("the switch was turned on before the reload");
        await page.GetByRole(AriaRole.Button, new() { Name = "Settings" }).ClickAsync();
        (await page.Locator(".po-volume input, .po-volume [role=slider]").First.GetAttributeAsync("aria-valuenow") ?? await page.Locator(".po-volume input").First.InputValueAsync())
            .Should().Be("40", "the slider opens where it was left");
        errors.Should().BeEmpty();
    }
}
