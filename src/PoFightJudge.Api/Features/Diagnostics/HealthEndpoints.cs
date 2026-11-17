using System.Globalization;
using Carter;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PoFightJudge.Api.Common;
using PoFightJudge.Api.Features.Auth;
using PoFightJudge.Shared;
using PoFightJudge.Shared.Configuration;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Api.Features.Diagnostics;

/// <summary>
/// Health surface. <c>/api/health</c> runs only the <c>ready</c> checks (configuration, storage)
/// — no network probes, so the deploy gate and the tests never depend on a third party being up.
/// <c>/api/health/details</c> adds the <c>ai</c> reachability probes, their latency, and a presence-only
/// configuration/feature report for the Blazor /diag page. All anonymous: no value, URI, path or exception text.
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

        app.MapGet(ApiRoutes.Health.DetailsUrl, async (HttpContext http, HealthCheckService health, IConfiguration config, IHostEnvironment env, TimeProvider clock, AiLatencyTracker tracker, CancellationToken ct) =>
        {
            var report = await health.CheckHealthAsync(ct);
            var checks = new List<HealthCheckDto>();

            foreach (var (name, entry) in report.Entries)
            {
                var (label, purpose, category) = Describe(name);
                checks.Add(new HealthCheckDto(label, Map(entry.Status), Plain(entry.Status), category, purpose, Math.Round(entry.Duration.TotalMilliseconds, 1)));
            }

            AddPresence(checks, "Gemini API key", config[ConfigKeys.Ai.GeminiApiKey], required: env.IsProduction());
            AddPresence(checks, "Entra client id", config[ConfigKeys.AzureAd.ClientId], required: env.IsProduction());
            AddPresence(checks, "Table endpoint", config[ConfigKeys.Storage.TableEndpoint], required: true);
            AddPresence(checks, "Blob endpoint", config[ConfigKeys.Storage.BlobEndpoint], required: true);
            checks.Add(AiProvider(env, config));
            checks.Add(new HealthCheckDto(
                "Key Vault",
                string.IsNullOrWhiteSpace(config[ConfigKeys.KeyVault.Uri]) ? HealthState.NotConfigured : HealthState.Ok,
                string.IsNullOrWhiteSpace(config[ConfigKeys.KeyVault.Uri]) ? "Not used here; settings come from local files." : "Connected at startup.",
                HealthCategory.Service,
                "Keeps the app's secrets (API keys) out of the code."));
            checks.Add(new HealthCheckDto(
                "Sign-in",
                string.IsNullOrWhiteSpace(config[ConfigKeys.AzureAd.ClientId]) ? HealthState.NotConfigured : HealthState.Ok,
                string.IsNullOrWhiteSpace(config[ConfigKeys.AzureAd.ClientId]) ? "Microsoft sign-in is not set up here." : "Microsoft sign-in is set up.",
                HealthCategory.Service,
                "Lets people sign in with a Microsoft account."));

            // Anonymous route, so only a signed-in caller gets a row, and only their own number.
            if (http.User.UserIdOrNull() is { } caller && config.GetValue<long>(ConfigKeys.Ai.DailyTokenBudget) is > 0 and var limit)
            {
                var used = tracker.TokensToday(caller);
                checks.Add(new HealthCheckDto(
                    "AI allowance today",
                    used >= limit ? HealthState.Degraded : HealthState.Ok,
                    string.Create(CultureInfo.InvariantCulture, $"{used:N0} of {limit:N0} tokens ({used * 100.0 / limit:0}%)"),
                    HealthCategory.Feature));
            }

            foreach (var toggle in Toggles.All)
            {
                checks.Add(new HealthCheckDto($"Toggle: {toggle}", HealthState.Ok, config.GetValue<bool>(toggle) ? "on" : "off", HealthCategory.Feature));
            }

            return Results.Ok(new HealthReportDto(env.EnvironmentName, Aggregate(checks), clock.GetUtcNow(), checks));
        }).AllowAnonymous().WithTags("Health");
    }

    /// <summary>A probe's registration name turned into what the page shows: a label, what it is for, and its section.</summary>
    private static (string Label, string Purpose, HealthCategory Category) Describe(string name) => name switch
    {
        "storage" => ("Table and Blob storage", "Saves profiles, matches, recordings and faces.", HealthCategory.Storage),
        "configuration" => ("Required settings", "Checks the keys the app needs to run are present.", HealthCategory.Service),
        "gemini-reachable" => ("Google Gemini reachable", "The AI service used when Azure OpenAI is not set up.", HealthCategory.Service),
        _ => (name, "A dependency the app checks on.", HealthCategory.Service),
    };

    /// <summary>Which AI is answering: the stand-ins, Azure OpenAI or Gemini. Named, never its endpoint.</summary>
    private static HealthCheckDto AiProvider(IHostEnvironment env, IConfiguration config)
    {
        const string Purpose = "Writes the arguments, voices the fighters and judges the fights.";
        if (DiagEndpoints.IsFakeAi(env, config))
        {
            return new HealthCheckDto("AI provider", HealthState.Ok, "Stand-in AI: canned answers, nothing is sent out.", HealthCategory.Service, Purpose);
        }

        return string.IsNullOrWhiteSpace(config[ConfigKeys.Ai.AzureOpenAiEndpoint])
            ? new HealthCheckDto("AI provider", HealthState.Ok, "Google Gemini.", HealthCategory.Service, Purpose)
            : new HealthCheckDto("AI provider", HealthState.Ok, "Azure OpenAI.", HealthCategory.Service, Purpose);
    }

    /// <summary>
    /// The probe's own description is left out on purpose: a failure carries hints (script paths, missing key names)
    /// that belong in the log, not on an anonymous page.
    /// </summary>
    private static string Plain(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => "Working normally.",
        HealthStatus.Degraded => "Answering, but not fully.",
        _ => "Not answering.",
    };

    private static void AddPresence(List<HealthCheckDto> checks, string name, string? value, bool required)
    {
        var present = !string.IsNullOrWhiteSpace(value);
        var state = present ? HealthState.Ok : required ? HealthState.Failed : HealthState.NotConfigured;
        // Present or not, and nothing more: this route answers anybody, and eight characters of a key is eight too many.
        checks.Add(new HealthCheckDto(name, state, present ? "set" : "not set", HealthCategory.Configuration));
    }

    private static HealthState Map(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => HealthState.Ok,
        HealthStatus.Degraded => HealthState.Degraded,
        _ => HealthState.Failed,
    };

    private static HealthState Aggregate(IReadOnlyList<HealthCheckDto> checks) =>
        checks.Any(c => c.State == HealthState.Failed) ? HealthState.Failed
        : checks.Any(c => c.State == HealthState.Degraded) ? HealthState.Degraded
        : HealthState.Ok;
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
