using System.Reflection;
using Carter;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PoFightJudge.Shared;
using PoFightJudge.Shared.Configuration;
using PoFightJudge.Shared.Diagnostics;

namespace PoFightJudge.Api.Features.Diagnostics;

/// <summary>
/// Health surface (Po web standard). <c>/api/health</c> and <c>/health</c> run only the <c>ready</c> checks
/// (configuration, storage) — no network probes, so the deploy gate and the tests never depend on a third party being up.
/// <c>/health/live</c> is bare liveness (the X-Commit header is added to every response in Program.cs).
/// <c>/api/diag</c> is the anonymous <see cref="DiagReportDto"/> for the /diag "System status" page: every check,
/// including the <c>ai</c> reachability probes, in plain words — never a value, URI, path or exception message — and
/// cached for <see cref="DiagReportCache.Lifetime"/>.
/// </summary>
public sealed class HealthEndpoints : ICarterModule
{
    public const string ReadyTag = "ready";
    public const string AiTag = "ai";
    public const string ServiceTag = "service";

    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var ready = new HealthCheckOptions { Predicate = h => h.Tags.Contains(ReadyTag) };
        app.MapHealthChecks(ApiRoutes.Health.Url, ready).AllowAnonymous().WithTags("Health");
        app.MapHealthChecks(ApiRoutes.Health.PlatformUrl, ready).AllowAnonymous().WithTags("Health");
        app.MapGet(ApiRoutes.Health.LiveUrl, () => Results.Text("Healthy")).AllowAnonymous().WithTags("Health");

        // Cached (DiagReportCache.Lifetime): the page is anonymous and the host is on the F1 plan.
        app.MapGet(ApiRoutes.Diag.Url, async (HealthCheckService health, IConfiguration config, IHostEnvironment env, TimeProvider clock, DiagReportCache cache, CancellationToken ct) =>
            Results.Ok(await cache.GetAsync(token => BuildReportAsync(health, config, env, clock, token), ct))).AllowAnonymous().WithTags("Diagnostics");
    }

    /// <summary>The build stamp (UTC yyMMdd), from the one <c>BuildVersion</c> assembly metadata key.</summary>
    public static string BuildVersion { get; } =
        typeof(HealthEndpoints).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, "BuildVersion", StringComparison.Ordinal))?.Value ?? "dev";

    /// <summary>Everything /diag shows: the server itself, the live probes, and the services the app is set up to call.</summary>
    public static async Task<DiagReportDto> BuildReportAsync(HealthCheckService health, IConfiguration config, IHostEnvironment env, TimeProvider clock, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(clock);
        var started = clock.GetTimestamp();
        var report = await health.CheckHealthAsync(ct);
        var checks = new List<DiagCheckDto>
        {
            new("api", "api", "PoFightJudge API", Healthy, "Answers every page and runs the fights.", "Answering.", 0, null),
        };

        foreach (var (name, entry) in report.Entries.OrderBy(e => Describe(e.Key).Group == "data" ? 0 : 1))
        {
            var (group, label, purpose) = Describe(name);
            checks.Add(new DiagCheckDto(
                name,
                group,
                label,
                entry.Status.ToString(),
                purpose,
                Plain(entry.Status),
                Math.Round(entry.Duration.TotalMilliseconds, 1),
                entry.Status == HealthStatus.Healthy ? null : Problem(name, entry)));
        }

        if (checks.All(c => !string.Equals(c.Name, "storage", StringComparison.Ordinal)))
        {
            checks.Add(new DiagCheckDto(
                "storage",
                "data",
                "Table and Blob storage",
                Off,
                "Saves profiles, matches, recordings and faces.",
                "Off here: in-memory test storage.",
                0,
                null));
        }

        checks.Add(AiProvider(env, config, report));

        // A service that is configured is probed live (DiagnosticsServiceExtensions); one that is not is named here, so
        // the page still lists every external service the app can use.
        if (!report.Entries.ContainsKey("key-vault"))
        {
            checks.Add(NotConfigured("key-vault", "Not used here; settings come from local files.", env));
        }

        if (!report.Entries.ContainsKey("sign-in"))
        {
            checks.Add(NotConfigured("sign-in", "Microsoft sign-in is not set up here; the guest door is used instead.", env));
        }

        if (!report.Entries.ContainsKey("app-insights"))
        {
            checks.Add(NotConfigured("app-insights", "Not used here; logs stay on this machine.", env));
        }

        var worst = checks.Any(c => c.Status == Unhealthy) ? Unhealthy
            : checks.Any(c => c.Status == Degraded) ? Degraded
            : Healthy;

        return new DiagReportDto(worst, env.EnvironmentName, BuildVersion, clock.GetUtcNow(),
            Math.Round(clock.GetElapsedTime(started).TotalMilliseconds, 1), [.. checks]);
    }

    private const string Healthy = nameof(HealthStatus.Healthy);
    private const string Degraded = nameof(HealthStatus.Degraded);
    private const string Unhealthy = nameof(HealthStatus.Unhealthy);

    /// <summary>Switched off on purpose here (a stand-in is used): neutral, never counted as a problem.</summary>
    public const string Off = "Off";

    /// <summary>A probe's registration name turned into what the page shows: its section, a label and what it is for.</summary>
    private static (string Group, string Label, string Purpose) Describe(string name) => name switch
    {
        "storage" => ("data", "Table and Blob storage", "Saves profiles, matches, recordings and faces."),
        "configuration" => ("service", "Required settings", "Checks the keys the app needs to run are present."),
        "gemini-reachable" => ("service", "Google Gemini", "The AI service used when Azure OpenAI is not set up."),
        "azure-openai" => ("service", "Azure OpenAI", "Writes the arguments, voices the fighters and judges the fights."),
        "key-vault" => ("service", "Key Vault", "Keeps the app's secrets (API keys) out of the code."),
        "sign-in" => ("service", "Microsoft sign-in", "Lets people sign in with a Microsoft account."),
        "app-insights" => ("service", "Application Insights", "Collects errors and usage so problems can be found."),
        _ => ("service", name, "A dependency the app checks on."),
    };

    /// <summary>
    /// What went wrong, in plain words. The probe's own description is left out on purpose: a failure carries hints
    /// (script paths, missing key names) that belong in the log, not on an anonymous page.
    /// </summary>
    private static string Problem(string name, HealthReportEntry entry) => name switch
    {
        "storage" => "Storage could not be reached.",
        "configuration" => "Required settings are missing; see the server log.",
        _ => entry.Exception is not null ? "It did not answer in time." : "It did not answer as expected.",
    };

    /// <summary>
    /// Which AI is answering — the stand-ins, Azure OpenAI or Gemini, named, never its endpoint — and how that one's
    /// live probe went, so the row is never a hard-coded "fine".
    /// </summary>
    private static DiagCheckDto AiProvider(IHostEnvironment env, IConfiguration config, HealthReport report)
    {
        const string Purpose = "Writes the arguments, voices the fighters and judges the fights.";
        if (DiagEndpoints.IsFakeAi(env, config))
        {
            return new DiagCheckDto("ai-provider", "service", "AI provider", Off, Purpose, "Stand-in AI: canned answers, nothing is sent out.", 0, null);
        }

        var (probe, name) = string.IsNullOrWhiteSpace(config[ConfigKeys.Ai.AzureOpenAiEndpoint])
            ? ("gemini-reachable", "Google Gemini")
            : ("azure-openai", "Azure OpenAI");
        if (!report.Entries.TryGetValue(probe, out var entry))
        {
            return new DiagCheckDto("ai-provider", "service", "AI provider", Unhealthy, Purpose, $"{name}: not checked.", 0, "Its address is not set up.");
        }

        return new DiagCheckDto("ai-provider", "service", "AI provider", entry.Status.ToString(), Purpose, $"{name}: {Plain(entry.Status)}",
            Math.Round(entry.Duration.TotalMilliseconds, 1), entry.Status == HealthStatus.Healthy ? null : Problem(probe, entry));
    }

    /// <summary>
    /// A service that is not configured, so nothing was probed: Off outside Production (the local stand-in is used)
    /// and Unhealthy in Production, which always needs it (Application Insights only degrades).
    /// </summary>
    private static DiagCheckDto NotConfigured(string name, string unset, IHostEnvironment env)
    {
        var (group, label, purpose) = Describe(name);
        if (!env.IsProduction())
        {
            return new DiagCheckDto(name, group, label, Off, purpose, unset, 0, null);
        }

        var status = name == "app-insights" ? Degraded : Unhealthy;
        return new DiagCheckDto(name, group, label, status, purpose, "Not set up.", 0, "Production needs it, and it is not configured.");
    }

    private static string Plain(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => "Working normally.",
        HealthStatus.Degraded => "Answering, but not fully.",
        _ => "Not answering.",
    };
}

/// <summary>
/// Reports unhealthy when <see cref="StartupSecretValidator"/> found required configuration missing in Production.
/// The same fact is on <c>/api/diag</c>, but that route is authenticated, so a deploy pipeline cannot see it: without
/// this check a site with no Gemini key answers 200 on every route and only fails once someone starts a fight.
/// Names, never values — the keys are already public constants.
/// </summary>
public sealed class ConfigurationHealthCheck(StartupHealthState state) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(state.IsDegraded
            ? HealthCheckResult.Unhealthy($"Required configuration missing: {string.Join(", ", state.MissingKeys)}")
            : HealthCheckResult.Healthy("Required configuration present."));
}
