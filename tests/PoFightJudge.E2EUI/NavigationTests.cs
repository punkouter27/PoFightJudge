using Microsoft.Playwright;

namespace PoFightJudge.E2EUI;

/// <summary>
/// Getting around, on both shapes of screen. There is no nav: Home is every destination (the three channels, the cast
/// and the leaderboard), the app name in the Po header leads back to it from anywhere, and the end of a match leads on to the history.
/// </summary>
[Collection(AppCollection.Name)]
public class NavigationTests(AppFixture app)
{
    [SkippableFact]
    public async Task The_header_is_the_name_the_email_and_one_settings_gear()
    {
        // A test sign-in whose name is an address: the header must show exactly that address, top right.
        const string Email = "judge.e2e@example.com";
        var (context, page, errors) = await app.OpenAsync(AppFixture.Desktop, "/", user: Email);
        await using var _ = context;

        (await page.Locator("header.po-header nav, nav.po-drawer").CountAsync()).Should().Be(0, "Home is the nav");
        var email = page.Locator("header.po-header .po-header__email");
        (await email.CountAsync()).Should().Be(1, "who is signed in, top right");
        (await email.InnerTextAsync()).Trim().Should().Be(Email, "the signed-in address is shown as is");
        var emailBox = (await email.BoundingBoxAsync())!;
        var titleBox = (await page.Locator("header.po-header .po-header__title").BoundingBoxAsync())!;
        titleBox.X.Should().BeLessThan(AppFixture.Desktop.Width / 4f, "the app title is top left");
        (emailBox.X + emailBox.Width).Should().BeGreaterThan(AppFixture.Desktop.Width * 3 / 4f, "the email is top right");
        emailBox.Y.Should().BeLessThan(80, "the email is in the header");
        (await page.Locator("header.po-header .po-header__actions button").CountAsync()).Should().Be(0, "the gear is the one way to the settings");

        await page.GetByRole(AriaRole.Link, new() { Name = "Settings" }).ClickAsync();
        await page.WaitForFunctionAsync("() => location.pathname === '/settings'", null, new() { Timeout = 15_000 });
        var panel = page.Locator(".po-settings");
        await panel.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        (await panel.Locator(".po-volume, .host-list, .rz-selectbar, .rz-switch").CountAsync()).Should().Be(0, "how the app plays is fixed, not a setting");

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
            await page.Locator("header.po-header a.po-header__title").ClickAsync();
            await page.WaitForFunctionAsync("() => location.pathname === '/'", null, new() { Timeout = 15_000 });
            (await page.Locator("article.channel").CountAsync()).Should().Be(5, "every destination is a card on Home");
            errors.Should().BeEmpty();
        }
    }
}
