using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Playwright;
using PoFightJudge.Api.Features.Auth;
using PoFightJudge.Api.Features.Fighters;
using PoFightJudge.Api.Features.Profiles;
using PoFightJudge.Api.Features.Records;
using PoFightJudge.Api.Features.Storage;
using PoFightJudge.Api.Features.Watch;
using PoFightJudge.Shared.Configuration;
using PoFightJudge.TestSupport;

namespace PoFightJudge.E2EUI;

/// <summary>
/// Hosts the real API on Kestrel (a free port, static web assets on so the WASM client is served) under the
/// <c>Test</c> environment, then drives headless Chromium with a fake microphone that plays
/// <c>fixtures/debate-60s.wav</c>. In-memory stores stand in for storage; the AI fakes join in T15.
/// Set <c>POFIGHTJUDGE_E2E_REAL=1</c> (with <c>GEMINI_API_KEY</c>) to drive the real providers instead.
/// </summary>
public sealed class AppFixture : IAsyncLifetime
{
    public static bool RealMode => string.Equals(Environment.GetEnvironmentVariable("POFIGHTJUDGE_E2E_REAL"), "1", StringComparison.Ordinal);

    public static readonly ViewportSize Desktop = new() { Width = 1440, Height = 900 };

    public static readonly ViewportSize Mobile = new() { Width = 390, Height = 844 };

    public static string FixtureWav => Path.Combine(AppContext.BaseDirectory, "fixtures", "debate-60s.wav");

    private HostFactory? _factory;
    private IPlaywright? _playwright;

    public IBrowser? Browser { get; private set; }

    /// <summary>The running host's services, so a test can put a finished argument in the store without arguing one.</summary>
    public IServiceProvider Services => _factory?.Services ?? throw new InvalidOperationException("The host has not started.");

    public string Unavailable { get; private set; } = "Playwright has not started.";

    public string BaseUrl { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        _factory = new HostFactory();
        _factory.UseKestrel(0);
        _factory.StartServer();

        // The address Kestrel actually bound, not the client default (which would send the browser to :5000).
        BaseUrl = _factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault() is { } bound
            ? bound.Replace("[::]", "127.0.0.1", StringComparison.Ordinal).Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal).TrimEnd('/')
            : _factory.ClientOptions.BaseAddress.ToString().TrimEnd('/');

        try
        {
            _playwright = await Playwright.CreateAsync();
            Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args =
                [
                    "--use-fake-device-for-media-stream",
                    "--use-fake-ui-for-media-stream",
                    $"--use-file-for-fake-audio-capture={FixtureWav}",
                    "--autoplay-policy=no-user-gesture-required",
                ],
            });
        }
        catch (PlaywrightException ex)
        {
            Unavailable = $"Playwright unavailable — UI tests skip. Run playwright.ps1 install chromium. {ex.Message}";
            Console.WriteLine($"[AppFixture] {Unavailable}");
            Browser = null;
        }
    }

    /// <summary>
    /// Opens a page and waits for the Blazor app to boot (the layout's top bar). <paramref name="user"/> null = no
    /// credentials (the anonymous path). Console errors, page errors and failed/4xx+ requests are collected so a
    /// failing test names the cause instead of timing out silently.
    /// </summary>
    public async Task<(IBrowserContext Context, IPage Page, List<string> Errors)> OpenAsync(ViewportSize viewport, string path = "/", string? user = "e2e-ui", bool effects = true)
    {
        Skip.If(Browser is null, Unavailable);
        var options = new BrowserNewContextOptions { ViewportSize = viewport, Permissions = ["microphone"] };
        if (user is not null)
        {
            options.ExtraHTTPHeaders = new Dictionary<string, string>(StringComparer.Ordinal) { [FakeAuthOptions.UserHeader] = user };
        }

        var context = await Browser!.NewContextAsync(options);

        // Stage effects are off until somebody turns them on in Settings. The tests that exercise them, and the
        // screenshots of the moments they live in, are about the effects, so they open with the switch on.
        if (effects)
        {
            await context.AddInitScriptAsync("try { localStorage.setItem('pofightjudge:effects', 'on'); } catch (e) { }");
        }
        // Extra headers apply to every request; keep the fake-auth header off third-party hosts (fonts) to avoid CORS noise.
        await context.RouteAsync(url => !url.StartsWith(BaseUrl, StringComparison.OrdinalIgnoreCase), route =>
            route.ContinueAsync(new RouteContinueOptions
            {
                Headers = route.Request.Headers.Where(h => !h.Key.Equals(FakeAuthOptions.UserHeader, StringComparison.OrdinalIgnoreCase)).ToDictionary(h => h.Key, h => h.Value, StringComparer.Ordinal),
            }));
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, e) => errors.Add("[PAGE ERROR] " + e);
        page.Console += (_, m) =>
        {
            if (string.Equals(m.Type, "error", StringComparison.Ordinal))
            {
                errors.Add("[CONSOLE ERROR] " + m.Text);
            }
        };
        // Aborted requests are routine (a navigation or a theme stylesheet swap cancels the in-flight one); real failures are not.
        page.RequestFailed += (_, r) =>
        {
            if (r.Failure?.Contains("ERR_ABORTED", StringComparison.Ordinal) != true)
            {
                errors.Add($"[REQUEST FAILED] {r.Url} {r.Failure}");
            }
        };
        page.Response += (_, r) =>
        {
            if (r.Status >= 400 && r.Url.StartsWith(BaseUrl, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"[HTTP {r.Status}] {r.Url}");
            }
        };

        await page.GotoAsync(BaseUrl + path, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
        await WaitForAppAsync(page, errors);
        var blazorError = page.Locator("#blazor-error-ui");
        if (await blazorError.CountAsync() > 0 && await blazorError.IsVisibleAsync())
        {
            errors.Add("[#blazor-error-ui] Unhandled Blazor exception banner was visible.");
        }
        return (context, page, errors);
    }

    /// <summary>
    /// Walks a setup wizard to its last step. Both setup screens are a RadzenSteps since T76, so the button that
    /// starts an argument is on the last one; Radzen refuses a step the page has said is not ready, and the loop
    /// stops there rather than pretending it got through.
    /// </summary>
    /// <summary>
    /// The watch page shows one line at a time, so a test that wants to know what was said has to have been
    /// listening. Call before the page is opened; <see cref="SaidAsync"/> then returns every line in order.
    /// </summary>
    public static Task ListenForLinesAsync(IPage page) => page.AddInitScriptAsync(
        "window.__said = []; new MutationObserver(() => { const l = document.querySelector('article.line'); const t = l && l.innerText;"
        + " if (t && window.__said[window.__said.length - 1] !== t) window.__said.push(t); })"
        + ".observe(document, { subtree: true, childList: true, characterData: true });");

    public static Task<string[]> SaidAsync(IPage page) => page.EvaluateAsync<string[]>("() => window.__said");

    /// <summary>How many lines the argument on screen has had so far.</summary>
    public const string LinesSaid = "Number(document.querySelector('.transcript')?.dataset.lines ?? 0)";

    public static async Task ToTheLastStepAsync(IPage page)
    {
        for (var step = 0; step < 4; step++)
        {
            var next = page.Locator("button.rz-steps-next");
            if (await next.CountAsync() == 0 || !await next.IsEnabledAsync())
            {
                return;
            }

            await next.ClickAsync();
            await page.WaitForTimeoutAsync(120);
        }
    }

    /// <summary>The layout's top bar is rendered by Blazor, so its presence is the app having booted.</summary>
    public static async Task WaitForAppAsync(IPage page, IReadOnlyCollection<string> errors)
    {
        try
        {
            await page.Locator("header.topbar, .po-bare-shell").First.WaitForAsync(new() { Timeout = 45_000 });
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException("The Blazor app did not boot within 45 s.\n" + string.Join('\n', errors), ex);
        }
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null)
        {
            await Browser.DisposeAsync();
        }

        _playwright?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }
}

/// <summary>The Test-environment host: no Key Vault, fake credentials allowed, static web assets on, in-memory stores, no storage probe.</summary>
internal sealed class HostFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");
        // Port 0 = whatever is free. The default :5000 collides with whichever Po* app a developer has running.
        builder.UseUrls("http://127.0.0.1:0");
        builder.UseStaticWebAssets(); // only automatic in Development; the WASM client lives in static web assets
        builder.UseSetting(ConfigKeys.KeyVault.Uri, string.Empty);
        builder.UseSetting(ConfigKeys.Auth.AllowFakeAuth, "true");
        builder.UseSetting(Toggles.UseAzurite, "false");
        // No storage account in this host, so the blob TTS cache would spend its retry budget on every call.
        builder.UseSetting(Toggles.TtsCache, "false");
        // Real mode drives the real providers; otherwise the toggle routes every AI call to the deterministic fakes.
        builder.UseSetting(Toggles.UseFakes, AppFixture.RealMode ? "false" : "true");
        builder.UseSetting(ConfigKeys.Ai.GeminiApiKey, AppFixture.RealMode ? Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? string.Empty : "test-key");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IProfileRepository>();
            services.AddSingleton<IProfileRepository, InMemoryProfileRepository>();
            services.RemoveAll<IProfileImageService>();
            services.AddSingleton<IProfileImageService, InMemoryProfileImageService>();
            services.RemoveAll<IWatchResultRepository>();
            services.AddSingleton<IWatchResultRepository, InMemoryWatchResultRepository>();
            services.RemoveAll<IFighterResultRepository>();
            services.AddSingleton<IFighterResultRepository, InMemoryFighterResultRepository>();
            services.RemoveAll<IFighterRepository>();
            services.AddSingleton<IFighterRepository, InMemoryFighterRepository>();
            services.RemoveAll<IFighterWordsRepository>();
            services.AddSingleton<IFighterWordsRepository, InMemoryFighterWordsRepository>();
            services.RemoveAll<IMatchRepository>();
            services.AddSingleton<IMatchRepository, InMemoryMatchRepository>();
            services.RemoveAll<IWatchAudioStore>();
            services.AddSingleton<IWatchAudioStore, InMemoryWatchAudioStore>();

            // A fight's recording and its transcripts, so the analysis has something real to read without Azure.
            services.RemoveAll<IAudioBlobStore>();
            services.AddSingleton<IAudioBlobStore, InMemoryAudioBlobStore>();
            services.Configure<HealthCheckServiceOptions>(o =>
            {
                foreach (var registration in o.Registrations.Where(r => string.Equals(r.Name, "storage", StringComparison.Ordinal)).ToList())
                {
                    o.Registrations.Remove(registration);
                }
            });
        });
    }
}

[CollectionDefinition(Name)]
public sealed class AppCollection : ICollectionFixture<AppFixture>
{
    public const string Name = "App";
}
