using Microsoft.Playwright;
using Xunit.Abstractions;

namespace PoFightJudge.E2EUI;

/// <summary>
/// Not a test so much as a camera: opt in with <c>SHOTS</c> (a folder) and it saves a full-page PNG of each route,
/// against the same in-memory, fake-AI host the rest of this suite drives. A cloud session cannot be browsed, so this
/// is how a change gets looked at before it is deployed. <c>ROUTES</c> (comma-separated names or paths) narrows it
/// to the pages a change touched. Skipped when <c>SHOTS</c> is unset, so the suite is unaffected.
/// <c>VIEWPORTS=desktop,phone</c> takes each page at both sizes (a phone shot is named <c>-phone</c>), and
/// <c>SEED=1</c> loads the default cast and plays one watch through first, so the lists have something in them and
/// the finished argument is captured as <c>watch-play</c>; then one fight is played on the fake microphone and
/// captured as <c>fight-live</c>, <c>verdict-reveal</c> and <c>verdict</c>. <c>EFFECTS=off</c> photographs the
/// pages as a first visit sees them, with stage effects at their default.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class ScreenshotRunner(AppFixture app, ITestOutputHelper output)
{
    /// <summary>Every page that renders without a match or a token in its URL. The one list to extend.</summary>
    private static bool Effects => !string.Equals(Environment.GetEnvironmentVariable("EFFECTS"), "off", StringComparison.OrdinalIgnoreCase);

    public static readonly IReadOnlyList<(string Name, string Path)> Routes =
    [
        ("home", "/"),
        ("watch", "/watch"),
        ("one-player", "/1p"),
        ("fight", "/fight"),
        ("fighters", "/fighters"),
        ("profiles", "/profiles"),
        ("history", "/history"),
        ("health", "/health"),
        ("login", "/login"),
    ];

    [SkippableFact]
    public async Task Capture()
    {
        var folder = Environment.GetEnvironmentVariable("SHOTS");
        Skip.If(string.IsNullOrWhiteSpace(folder), "Set SHOTS to a folder to take screenshots.");
        Directory.CreateDirectory(folder!);

        var wanted = (Environment.GetEnvironmentVariable("ROUTES") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var routes = wanted.Length == 0
            ? Routes
            : [.. Routes.Where(r => wanted.Contains(r.Name, StringComparer.OrdinalIgnoreCase) || wanted.Contains(r.Path, StringComparer.OrdinalIgnoreCase))];
        Skip.If(routes.Count == 0, $"ROUTES matched nothing. Known: {string.Join(", ", Routes.Select(r => r.Name))}");

        var sizes = (Environment.GetEnvironmentVariable("VIEWPORTS") ?? "desktop")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => string.Equals(v, "phone", StringComparison.OrdinalIgnoreCase) ? (Suffix: "-phone", Size: AppFixture.Mobile) : (Suffix: string.Empty, Size: AppFixture.Desktop))
            .ToList();

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SEED")))
        {
            await SeedAsync(folder!);
        }

        foreach (var (suffix, size) in sizes)
        {
            foreach (var (name, path) in routes)
            {
                var (context, page, errors) = await app.OpenAsync(size, path, effects: Effects);
                try
                {
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                    await page.WaitForTimeoutAsync(1000); // animations and late renders settle
                    await ShootAsync(page, folder!, name + suffix, path, errors);
                }
                finally
                {
                    await context.CloseAsync();
                }
            }
        }

        if (routes.Any(r => string.Equals(r.Name, "home", StringComparison.Ordinal)))
        {
            await MenusAsync(folder!);
        }

    }

    /// <summary>What the bar opens on a desktop: the settings panel.</summary>
    private async Task MenusAsync(string folder)
    {
        var (context, page, errors) = await app.OpenAsync(AppFixture.Desktop, "/", effects: Effects);
        try
        {
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.GetByRole(AriaRole.Button, new() { Name = "Settings" }).ClickAsync();
            await page.Locator(".po-settings").WaitForAsync(new() { Timeout = 10_000 });
            await page.WaitForTimeoutAsync(400);
            await ShootAsync(page, folder, "home-settings", "/", errors);
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    /// <summary>The default cast, and one whole argument between two of them, so no page is photographed empty.</summary>
    private async Task SeedAsync(string folder)
    {
        var (context, page, errors) = await app.OpenAsync(AppFixture.Desktop, "/profiles", effects: Effects);
        try
        {
            var load = page.GetByRole(AriaRole.Button, new() { Name = "Load default cast" });
            await load.First.WaitForAsync(new() { Timeout = 30_000 });
            await load.First.ClickAsync();
            var confirm = page.Locator(".rz-dialog");
            await confirm.WaitForAsync(new() { Timeout = 10_000 });
            await confirm.GetByRole(AriaRole.Button, new() { Name = "Load cast" }).ClickAsync();
            await page.WaitForFunctionAsync("() => document.querySelectorAll('article.profile').length >= 8", null, new() { Timeout = 30_000 });

            await page.GotoAsync($"{app.BaseUrl}/cpu");
            await page.Locator("div.picker").First.WaitForAsync(new() { Timeout = 30_000 });
            for (var side = 0; side < 2; side++)
            {
                await page.Locator("div.picker").Nth(side).Locator(".rz-dropdown").ClickAsync();
                var panel = page.Locator(".rz-dropdown-panel:visible");
                await panel.WaitForAsync(new() { Timeout = 10_000 });
                await panel.Locator(".rz-dropdown-item").First.ClickAsync();
                await panel.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 10_000 });
            }

            await AppFixture.ToTheLastStepAsync(page);
            await page.GetByRole(AriaRole.Button, new() { Name = "Start the argument" }).ClickAsync();
            await page.Locator("section.verdict").WaitForAsync(new() { Timeout = 150_000 });
            await page.WaitForTimeoutAsync(1500);
            await ShootAsync(page, folder, "watch-play", "/watch/play", errors);
        }
        finally
        {
            await context.CloseAsync();
        }

        await FightAsync(folder);
    }

    /// <summary>
    /// One whole fight on the fake microphone and the scripted host, so the live stage and the ruling it hands over
    /// to are photographed too: <c>fight-live</c> mid-argument, <c>verdict-reveal</c> while the ruling is still
    /// arriving, and <c>verdict</c> once it has settled.
    /// </summary>
    private async Task FightAsync(string folder)
    {
        var (context, page, errors) = await app.OpenAsync(AppFixture.Desktop, "/2p", user: "e2e-shots", effects: Effects);
        try
        {
            foreach (var (name, tag) in new[] { ("player1Tag", "ALI"), ("player2Tag", "SAM") })
            {
                var input = page.Locator($"div.tag-input[data-name={name}] input");
                await input.WaitForAsync(new() { Timeout = 30_000 });
                await input.FillAsync(tag);
                await input.PressAsync("Tab", new() { Delay = 20 });
            }

            // Spinning for a topic (at the Settings heat), mid-reel and landed — then the topic the rest of the shots expect, typed over it.
            await page.GetByRole(AriaRole.Button, new() { Name = "Spin" }).ClickAsync();
            await page.WaitForTimeoutAsync(350);
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(folder, "fight-spin.png") });
            await page.Locator(".reel.landed").WaitForAsync(new() { Timeout = 10_000 });
            await page.WaitForTimeoutAsync(500);
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(folder, "fight-spun.png") });
            Report($"SHOT fight-spin, fight-spun -> {folder}");
            await page.Locator("input[name=topic]").FillAsync("who does the dishes");
            await AppFixture.ToTheLastStepAsync(page);
            await page.GetByRole(AriaRole.Button, new() { Name = "Start the fight" }).ClickAsync();

            // The tale of the tape is up while the host introduces them; it is photographed once it has come in, and
            // the stage once it has gone.
            await page.Locator("section.tape").WaitForAsync(new() { Timeout = 60_000 });
            await page.WaitForTimeoutAsync(1800);
            await ShootAsync(page, folder, "fight-tape", "/fight/{id}", errors);
            await page.Locator("section.tape").WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 30_000 });

            await page.WaitForFunctionAsync("() => document.querySelectorAll('article.caption').length > 1", null, new() { Timeout = 90_000 });
            await ShootAsync(page, folder, "fight-live", "/fight/{id}", errors);

            // The scripted host is never talked over, so the cut-in is forced; and it rules long before the clock runs
            // down, so the last seconds are forced too.
            await MomentAsync(page, folder, "fight-glitch", "() => PoFx.burst('glitch')", 60);
            // Pinned rather than set: the next snapshot would put the clock's own heat straight back.
            await MomentAsync(page, folder, "fight-heat", "() => { const set = PoGfx.set; PoGfx.set = (s, n, v) => set(s, n, n === 'heat' ? 1 : v); PoGfx.set('.stage', 'heat', 1); }", null);

            // Table mode, the way a phone lying between them sees it: landscape, the whole screen. Then back. The
            // window is sized before going fullscreen and after leaving it — a fullscreen window cannot be resized.
            await page.SetViewportSizeAsync(844, 390);
            await page.GetByRole(AriaRole.Button, new() { Name = "Table mode" }).ClickAsync();
            await page.WaitForTimeoutAsync(500);
            await ShootAsync(page, folder, "fight-table", "/fight/{id}", errors);
            await page.GetByRole(AriaRole.Button, new() { Name = "Leave table mode" }).ClickAsync();
            await page.WaitForTimeoutAsync(300);
            await page.SetViewportSizeAsync(AppFixture.Desktop.Width, AppFixture.Desktop.Height);

            // A fresh ruling is revealed: the drumroll, the name landing on the beat, then the settled page.
            await page.Locator("section.ruling").WaitForAsync(new() { Timeout = 180_000 });
            await page.WaitForTimeoutAsync(700);
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(folder, "verdict-reveal.png") });
            Report($"SHOT verdict-reveal (drumroll) -> {Path.Combine(folder, "verdict-reveal.png")}");
            await page.WaitForTimeoutAsync(1150);
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(folder, "verdict-named.png") });
            Report($"SHOT verdict-named (the beat) -> {Path.Combine(folder, "verdict-named.png")}");
            await page.WaitForTimeoutAsync(1400);
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(folder, "verdict-shatter.png"), FullPage = true });
            Report($"SHOT verdict-shatter (the loser's card) -> {Path.Combine(folder, "verdict-shatter.png")}");
            await page.WaitForTimeoutAsync(2600);
            await ShootAsync(page, folder, "verdict", "/verdict/{id}", errors);

            // The gavel's ring again, on its own: it is under a second long, and the beat is easy to miss.
            await MomentAsync(page, folder, "verdict-shock", "() => { scrollTo(0, 0); PoGfx.focus('.verdict', '.ruling h2'); PoGfx.shock('.verdict'); }", null);

            // The fake judge finds no fallacies — it judged nothing — so one stamp is put on the page by hand, dressed
            // by its own component's scoped styles, and armed the way the page arms its own. Caught mid-drop, then landed.
            await page.GetByText("What they claimed").ClickAsync();
            await page.EvaluateAsync(
                """
                () => {
                  const rule = [...document.styleSheets].flatMap(s => { try { return [...s.cssRules]; } catch { return []; } })
                    .find(r => r.selectorText && r.selectorText.startsWith('.fallacy-stamp['));
                  const scope = rule.selectorText.match(/\[(b-[a-z0-9]+)\]/)[1];
                  const column = document.querySelector('.side-by-side.claimed .column');
                  const list = document.createElement('ul');
                  list.innerHTML = '<li><span class="fallacy-stamp" ' + scope + '>FALLACY</span><strong>Appeal to volume</strong> — "I said it louder, so it is true"</li>';
                  column.appendChild(list);
                  list.scrollIntoView({ block: 'center' });
                }
                """);
            await MomentAsync(page, folder, "verdict-stamp", "() => PoFx.stamps('.fallacy-stamp')", 120);
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(folder, "verdict-stamped.png") });
            Report($"SHOT verdict-stamped -> {Path.Combine(folder, "verdict-stamped.png")}");

            // A moment played: the clip, cut from the fake microphone's recording, rolling past as mountains. The
            // highlights are the first tab's, so it is opened again after the claims.
            await page.Locator(".detail .rz-tabview-nav li", new() { HasText = "The fight" }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Hear it" }).First.ClickAsync();
            var terrain = page.Locator("canvas.terrain").First;
            await terrain.WaitForAsync(new() { Timeout = 15_000 });
            await terrain.ScrollIntoViewIfNeededAsync();
            await page.WaitForTimeoutAsync(1200);
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(folder, "verdict-mountains.png") });
            Report($"SHOT verdict-mountains -> {Path.Combine(folder, "verdict-mountains.png")}");

            // The story picture, through the real button: headless Chromium has no share sheet, so it is saved.
            await page.EvaluateAsync("() => scrollTo(0, 0)");
            var story = await page.RunAndWaitForDownloadAsync(() => page.GetByRole(AriaRole.Button, new() { Name = "Story image" }).ClickAsync());
            await story.SaveAsAsync(Path.Combine(folder, "verdict-story.png"));
            Report($"SHOT verdict-story (the picture itself) -> {Path.Combine(folder, "verdict-story.png")}");
            await page.WaitForTimeoutAsync(300);
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(folder, "verdict-story-button.png") });

            // The footer, then one press: the same two, the same topic, the same host, straight back into a fight.
            var rematch = page.GetByRole(AriaRole.Button, new() { Name = "Rematch" });
            await rematch.ScrollIntoViewIfNeededAsync();
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(folder, "verdict-footer.png") });
            Report($"SHOT verdict-footer -> {Path.Combine(folder, "verdict-footer.png")}");
            var verdictUrl = page.Url;
            await rematch.ClickAsync();
            await page.WaitForURLAsync(u => u.Contains("/fight/", StringComparison.Ordinal) && !string.Equals(u, verdictUrl, StringComparison.Ordinal), new() { Timeout = 30_000 });
            await page.Locator("section.tape").WaitForAsync(new() { Timeout = 60_000 });
            await page.WaitForTimeoutAsync(1800);
            await ShootAsync(page, folder, "rematch", "/fight/{id}", errors);

            // A fighter's page, as a browser that last saw them a tier lower: the badge arrives as a promotion. In a
            // tab of its own — the rematch is live, and its guard would hold a navigation away from it.
            var profile = await context.NewPageAsync();
            await profile.GotoAsync(app.BaseUrl + "/fighters", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
            await profile.EvaluateAsync("() => localStorage.setItem('po-tier-ALI', 'Bronze')");
            await profile.GotoAsync(app.BaseUrl + "/fighters/ALI", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
            await profile.Locator(".tier.promoted").First.WaitForAsync(new() { Timeout = 30_000 });
            await profile.EvaluateAsync("() => document.getAnimations().filter(a => a.playState === 'running').forEach(a => { a.pause(); a.currentTime = 300; })");
            Report($"  fighter-promoted: badge is \"{await profile.Locator(".tier").First.GetAttributeAsync("class")}\"");
            await profile.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(folder, "fighter-promoted.png") });
            Report($"SHOT fighter-promoted -> {Path.Combine(folder, "fighter-promoted.png")}");

            // The card's podium, with the pointer resting near the leader's top corner so the foil turns toward it.
            await profile.GotoAsync(app.BaseUrl + "/fighters", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
            var leader = profile.Locator(".foil").First;
            await leader.WaitForAsync(new() { Timeout = 30_000 });
            var box = await leader.BoundingBoxAsync();
            if (box is not null)
            {
                await profile.Mouse.MoveAsync(box.X + (box.Width * 0.82f), box.Y + (box.Height * 0.2f));
            }

            await profile.WaitForTimeoutAsync(900);
            await profile.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(folder, "fighters-podium.png") });
            Report($"SHOT fighters-podium -> {Path.Combine(folder, "fighters-podium.png")}");
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    /// <summary>
    /// An effect too quick to catch in passing, frozen part-way: the script starts it, every CSS animation still
    /// running is paused <paramref name="atMs"/> in, and the window is shot. Finished ones are left alone — rewinding
    /// the page's own fade-in blanks it. Canvas effects have no clock to stop (pass null); they are whatever frame
    /// the shot lands on.
    /// </summary>
    private async Task MomentAsync(IPage page, string folder, string name, string script, int? atMs)
    {
        await page.EvaluateAsync(script);
        await page.WaitForTimeoutAsync(150); // a canvas draws what it was told on the next frame, not this one
        if (atMs is { } at)
        {
            await page.EvaluateAsync("t => document.getAnimations().filter(a => a.playState === 'running').forEach(a => { a.pause(); a.currentTime = t; })", at);
        }

        var file = Path.Combine(folder, name + ".png");
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = file });
        await page.EvaluateAsync("() => document.getAnimations().filter(a => a.playState === 'paused').forEach(a => a.play())");
        Report($"SHOT {name} (moment) -> {file}");
        await page.WaitForTimeoutAsync(1000); // let it finish, so the next moment is not photographed through this one
    }

    private async Task ShootAsync(IPage page, string folder, string name, string path, List<string> errors)
    {
        // The icons are a font: a page photographed before it arrives shows their names spelled out instead.
        // "Ready" only covers faces something has asked for so far, so each declared face is loaded by hand first.
        await page.EvaluateAsync("async () => { await Promise.allSettled([...document.fonts].map(f => f.load())); await document.fonts.ready; }");
        await page.WaitForTimeoutAsync(300);
        var icons = await page.EvaluateAsync<string>("() => [...document.fonts].filter(f => f.family.includes('Material')).map(f => f.status).join(',') + ' / ' + getComputedStyle(document.querySelector('.rzi') ?? document.body).fontFamily");
        Report($"  {name}: icon font {icons}");
        var file = Path.Combine(folder, name + ".png");
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = file, FullPage = true });
        Report($"SHOT {name} {path} -> {file} ({errors.Count} errors)");
        foreach (var error in errors)
        {
            Report($"  {name}: {error}");
        }
    }

    private void Report(string line)
    {
        output.WriteLine(line);
        Console.WriteLine(line);
    }
}
