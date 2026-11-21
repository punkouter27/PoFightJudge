using System.Text.Json;
using Microsoft.Playwright;

namespace PoFightJudge.E2EUI;

/// <summary>
/// The app can be installed to a home screen and opened in its own window. It is install-only: the worker caches
/// nothing, because everything here is live and a cached answer would be a wrong one.
/// </summary>
[Collection(AppCollection.Name)]
public class PwaTests(AppFixture app)
{
    /// <summary>
    /// The manifest describes an installable app with icons a launcher can use, and the worker registers without
    /// caching anything. One page serves both halves: they are the same install.
    /// </summary>
    [SkippableFact]
    public async Task The_app_installs_from_its_manifest_and_its_worker_caches_nothing()
    {
        var (context, page, errors) = await app.OpenAsync(AppFixture.Desktop);
        await using var _ = context;

        (await page.Locator("link[rel=manifest]").GetAttributeAsync("href")).Should().Be("manifest.webmanifest");

        var response = await page.APIRequest.GetAsync(app.BaseUrl + "/manifest.webmanifest");
        response.Status.Should().Be(200);
        using var manifest = JsonDocument.Parse(await response.TextAsync());
        var root = manifest.RootElement;

        root.GetProperty("display").GetString().Should().Be("standalone", "an installed app opens in its own window");
        root.GetProperty("start_url").GetString().Should().Be("/");
        root.GetProperty("name").GetString().Should().Be("PoFightJudge");
        root.GetProperty("theme_color").GetString().Should().Be("#4f46e5", "the window chrome matches the Po indigo accent");

        var icons = root.GetProperty("icons").EnumerateArray().ToList();
        icons.Select(i => i.GetProperty("sizes").GetString()).Should().Contain(["192x192", "512x512"]);
        icons.Should().Contain(i => string.Equals(i.GetProperty("purpose").GetString(), "maskable", StringComparison.Ordinal),
            "a round launcher crops a square icon into a mess without one");

        foreach (var icon in icons)
        {
            var src = icon.GetProperty("src").GetString();
            var image = await page.APIRequest.GetAsync($"{app.BaseUrl}/{src}");
            image.Status.Should().Be(200, $"the launcher will ask for {src}");
            (await image.BodyAsync()).Length.Should().BeGreaterThan(0);
        }

        // Registration is deferred to load, so wait for it rather than assuming it has already happened.
        var registered = await page.WaitForFunctionAsync(
            "async () => (await navigator.serviceWorker.getRegistrations()).length > 0",
            null,
            new() { Timeout = 30_000 });
        registered.Should().NotBeNull();

        var worker = await page.APIRequest.GetAsync(app.BaseUrl + "/sw.js");
        worker.Status.Should().Be(200);
        var source = await worker.TextAsync();
        source.Should().NotContain("addEventListener('fetch'", "a worker that answers requests is a worker that can serve a stale one");
        source.Should().NotContain("cache.put").And.NotContain("cache.addAll");

        // Nothing was stored, so a fresh window still asks the network for everything.
        (await page.EvaluateAsync<int>("async () => (await caches.keys()).length")).Should().Be(0);

        errors.Should().BeEmpty();
    }
}
