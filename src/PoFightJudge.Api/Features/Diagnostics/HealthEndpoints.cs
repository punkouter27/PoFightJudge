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
/// <c>/api/health/details</c> adds the <c>ai</c> reachability probes and a masked configuration/feature report for
/// the Blazor /health page. All anonymous; all presence-only.
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
                checks.Add(new HealthCheckDto(name, Map(entry.Status), entry.Description ?? entry.Status.ToString(), HealthCategory.Connection));
            }

            AddPresence(checks, "Gemini API key", config[ConfigKeys.Ai.GeminiApiKey], required: env.IsProduction());
            AddPresence(checks, "Entra client id", config[ConfigKeys.AzureAd.ClientId], required: env.IsProduction());
            checks.Add(new HealthCheckDto("Key Vault", string.IsNullOrWhiteSpace(config[ConfigKeys.KeyVault.Uri]) ? HealthState.NotConfigured : HealthState.Ok, SecretMasker.MaskHost(config[ConfigKeys.KeyVault.Uri]), HealthCategory.Configuration));
            checks.Add(new HealthCheckDto("Table endpoint", string.IsNullOrWhiteSpace(config[ConfigKeys.Storage.TableEndpoint]) ? HealthState.Failed : HealthState.Ok, SecretMasker.MaskHost(config[ConfigKeys.Storage.TableEndpoint]), HealthCategory.Configuration));
            checks.Add(new HealthCheckDto("Blob endpoint", string.IsNullOrWhiteSpace(config[ConfigKeys.Storage.BlobEndpoint]) ? HealthState.Failed : HealthState.Ok, SecretMasker.MaskHost(config[ConfigKeys.Storage.BlobEndpoint]), HealthCategory.Configuration));

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
