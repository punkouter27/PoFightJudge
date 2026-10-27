using Microsoft.Playwright;

namespace PoFightJudge.E2EUI;

/// <summary>
/// Getting around, on both shapes of screen. There is no nav: Home is every destination (the three channels, the cast
/// and the leaderboard), the logo leads back to it from anywhere, and the end of a match leads on to the history.
/// </summary>
[Collection(AppCollection.Name)]
public class NavigationTests(AppFixture app)
{
    [SkippableFact]
    public async Task The_bar_is_the_logo_and_one_settings_button()
    {
        var (context, page, errors) = await app.OpenAsync(AppFixture.Desktop, "/");
        await using var _ = context;

        (await page.Locator("header.topbar nav, nav.po-drawer").CountAsync()).Should().Be(0, "Home is the nav");
        (await page.Locator("header.topbar .session button:visible").CountAsync()).Should().Be(1, "user, sound, volume, theme and sign out are one panel");

        await page.GetByRole(AriaRole.Button, new() { Name = "Settings" }).ClickAsync();
        var panel = page.Locator(".po-settings");
        await panel.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        (await panel.Locator(".po-volume").CountAsync()).Should().Be(1);
        (await panel.Locator(".host-list").CountAsync()).Should().Be(1, "who hosts is a setting, not a setup question");
        (await panel.Locator(".rz-selectbar:has-text('Spicy')").CountAsync()).Should().Be(1, "so is how hot a spin runs");
        await page.Keyboard.PressAsync("Escape");
        await panel.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 10_000 });

        await page.GotoAsync(app.BaseUrl + "/2p");
        await page.GetByRole(AriaRole.Button, new() { Name = "Spin" }).WaitForAsync(new() { Timeout = 15_000 });
        (await page.Locator("main .host-list, main .rz-selectbar:has-text('Spicy')").CountAsync()).Should().Be(0, "setup asks only what this fight needs");
        errors.Should().BeEmpty();
    }

    /// <summary>The logo is the way home, on a desktop and on a phone, which has no menu button to look for.</summary>
    [SkippableFact]
    public async Task The_logo_leads_home_from_anywhere()
    {
        foreach (var viewport in new[] { AppFixture.Desktop, AppFixture.Mobile })
        {
            var (context, page, errors) = await app.OpenAsync(viewport, "/profiles");
            await using var _ = context;

            await page.Locator("article.profile, .po-empty, h1").First.WaitForAsync(new() { Timeout = 30_000 });
            (await page.GetByRole(AriaRole.Button, new() { Name = "Menu" }).CountAsync()).Should().Be(0, "there is no drawer to open");
            await page.Locator("header.topbar a.brand").ClickAsync();
            await page.WaitForFunctionAsync("() => location.pathname === '/'", null, new() { Timeout = 15_000 });
            (await page.Locator("article.channel").CountAsync()).Should().Be(5, "every destination is a card on Home");
            errors.Should().BeEmpty();
        }
    }
}
