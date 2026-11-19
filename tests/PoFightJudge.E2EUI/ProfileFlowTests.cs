using Microsoft.Playwright;

namespace PoFightJudge.E2EUI;

[Collection(AppCollection.Name)]
public class ProfileFlowTests(AppFixture app)
{
    [SkippableFact]
    public async Task Loading_the_default_cast_fills_both_role_sections_with_portraits()
    {
        var (context, page, errors) = await app.OpenAsync(AppFixture.Desktop, "/profiles", user: "e2e-seeder");
        await using var _ = context;

        var load = page.GetByRole(AriaRole.Button, new() { Name = "Load default cast" });
        await load.First.WaitForAsync(new() { Timeout = 30_000 });
        await load.First.ClickAsync();
        var confirm = page.Locator(".rz-dialog");
        await confirm.WaitForAsync(new() { Timeout = 10_000 });
        await confirm.GetByRole(AriaRole.Button, new() { Name = "Load cast" }).ClickAsync();

        var cards = page.Locator("article.profile");
        await page.WaitForFunctionAsync("() => document.querySelectorAll('article.profile').length >= 6", null, new() { Timeout = 30_000 });
        (await page.Locator("section.cast").CountAsync()).Should().Be(2, "husbands and wives are separate sections");
        var mah = page.Locator("article.profile", new() { HasText = "DJT" });
        await mah.Locator("img.face").WaitForAsync(new() { Timeout = 15_000 });
        (await mah.Locator("img.face").EvaluateAsync<bool>("img => img.complete && img.naturalWidth === 512")).Should().BeTrue("the seeded portrait is served as the 512 px PNG");

        errors.Should().BeEmpty();
    }
}
