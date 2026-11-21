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
/// including the <c>ai</c> reachability probes, in plain words — never a value, URI, path or exception message.
/// </summary>
public sealed class HealthEndpoints : ICarterModule
{
    public const string ReadyTag = "ready";
    public const string AiTag = "ai";

    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var ready = new HealthCheckOptions { Predicate = h => h.Tags.Contains(ReadyTag) };
        app.MapHealthChecks(ApiRoutes.Health.Url, ready).AllowAnonymous().WithTags("Health");
        app.MapHealthChecks(ApiRoutes.Health.PlatformUrl, ready).AllowAnonymous().WithTags("Health");
        app.MapGet(ApiRoutes.Health.LiveUrl, () => Results.Text("Healthy")).AllowAnonymous().WithTags("Health");

        app.MapGet(ApiRoutes.Diag.Url, async (HealthCheckService health, IConfiguration config, IHostEnvironment env, TimeProvider clock, CancellationToken ct) =>
            Results.Ok(await BuildReportAsync(health, config, env, clock, ct))).AllowAnonymous().WithTags("Diagnostics");
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

        checks.Add(AiProvider(env, config));
        checks.Add(Configured(
            "key-vault",
            "Key Vault",
            "Keeps the app's secrets (API keys) out of the code.",
            config[ConfigKeys.KeyVault.Uri],
            "Connected at startup.",
            "Not used here; settings come from local files.",
            env));
        checks.Add(Configured(
            "sign-in",
            "Microsoft sign-in",
            "Lets people sign in with a Microsoft account.",
            config[ConfigKeys.AzureAd.ClientId],
            "Microsoft sign-in is set up.",
            "Microsoft sign-in is not set up here; the guest door is used instead.",
            env));

        var worst = checks.Any(c => c.Status == Unhealthy) ? Unhealthy
            : checks.Any(c => c.Status == Degraded) ? Degraded
            : Healthy;

        return new DiagReportDto(worst, env.EnvironmentName, BuildVersion, clock.GetUtcNow(),
            Math.Round(clock.GetElapsedTime(started).TotalMilliseconds, 1), [.. checks]);
    }

    private const string Healthy = nameof(HealthStatus.Healthy);
    private const string Degraded = nameof(HealthStatus.Degraded);
    private const string Unhealthy = nameof(HealthStatus.Unhealthy);

    /// <summary>A probe's registration name turned into what the page shows: its section, a label and what it is for.</summary>
    private static (string Group, string Label, string Purpose) Describe(string name) => name switch
    {
        "storage" => ("data", "Table and Blob storage", "Saves profiles, matches, recordings and faces."),
        "configuration" => ("service", "Required settings", "Checks the keys the app needs to run are present."),
        "gemini-reachable" => ("service", "Google Gemini", "The AI service used when Azure OpenAI is not set up."),
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
        _ => entry.Exception is { } ex ? $"It did not answer ({ex.GetType().Name})." : "It did not answer as expected.",
    };

    /// <summary>Which AI is answering: the stand-ins, Azure OpenAI or Gemini. Named, never its endpoint.</summary>
    private static DiagCheckDto AiProvider(IHostEnvironment env, IConfiguration config)
    {
        const string Purpose = "Writes the arguments, voices the fighters and judges the fights.";
        var detail = DiagEndpoints.IsFakeAi(env, config) ? "Stand-in AI: canned answers, nothing is sent out."
            : string.IsNullOrWhiteSpace(config[ConfigKeys.Ai.AzureOpenAiEndpoint]) ? "Google Gemini."
            : "Azure OpenAI.";
        return new DiagCheckDto("ai-provider", "service", "AI provider", Healthy, Purpose, detail, 0, null);
    }

    /// <summary>
    /// A service switched on by configuration: Healthy when set; when unset, Healthy outside Production (the local
    /// stand-in is used) and Unhealthy in Production, which always needs it.
    /// </summary>
    private static DiagCheckDto Configured(string name, string label, string purpose, string? value, string set, string unset, IHostEnvironment env)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return new DiagCheckDto(name, "service", label, Healthy, purpose, set, 0, null);
        }

        return env.IsProduction()
            ? new DiagCheckDto(name, "service", label, Unhealthy, purpose, "Not set up.", 0, "Production needs it, and it is not configured.")
            : new DiagCheckDto(name, "service", label, Healthy, purpose, unset, 0, null);
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
