using Microsoft.Extensions.Diagnostics.HealthChecks;
using PoFightJudge.Shared.Configuration;

namespace PoFightJudge.Api.Features.Diagnostics;

public static class DiagnosticsServiceExtensions
{
    /// <summary>
    /// Registers the health checks. <c>ready</c>: configuration and storage. <c>ai</c> and <c>service</c>: live TLS
    /// reachability of every external service the app is set up to call (Gemini, Azure OpenAI, Key Vault, Microsoft
    /// sign-in, Application Insights). Any HTTP answer counts for the hosts that want a token, because the point is DNS
    /// + TLS + the service answering, not an authenticated call; failures only degrade, never fail, so an upstream blip
    /// cannot take the site out of rotation. A service that is not configured is not probed — /diag says so instead.
    /// </summary>
    public static IServiceCollection AddPoDiagnostics(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var checks = services.AddHealthChecks()
            .AddCheck<ConfigurationHealthCheck>("configuration", tags: [ReadyTagName]);

        AddReachability(checks, "gemini-reachable", "https://generativelanguage.googleapis.com/", HealthEndpoints.AiTag);

        if (AbsoluteUri(configuration[ConfigKeys.Ai.AzureOpenAiEndpoint]) is { } openAi)
        {
            AddReachability(checks, "azure-openai", openAi, HealthEndpoints.AiTag);
        }

        if (AbsoluteUri(configuration[ConfigKeys.KeyVault.Uri]) is { } vault)
        {
            AddReachability(checks, "key-vault", vault, HealthEndpoints.ServiceTag);
        }

        if (!string.IsNullOrWhiteSpace(configuration[ConfigKeys.AzureAd.ClientId]))
        {
            var instance = AbsoluteUri(configuration[$"{ConfigKeys.AzureAd.Section}:Instance"]) ?? "https://login.microsoftonline.com/";
            var tenant = configuration[ConfigKeys.AzureAd.TenantId] is { Length: > 0 } t ? t : "common";
            AddReachability(checks, "sign-in", $"{instance.TrimEnd('/')}/{tenant}/v2.0/.well-known/openid-configuration", HealthEndpoints.ServiceTag, okOnly: true);
        }

        if (AppInsightsIngestion() is { } ingestion)
        {
            AddReachability(checks, "app-insights", ingestion, HealthEndpoints.ServiceTag);
        }

        return services;
    }

    private const string ReadyTagName = HealthEndpoints.ReadyTag;

    /// <summary>The ingestion host from the connection string App Service injects; null when telemetry is off.</summary>
    internal static string? AppInsightsIngestion()
    {
        if (Environment.GetEnvironmentVariable(ConfigKeys.Telemetry.AppInsightsEnvVar) is not { Length: > 0 } connection)
        {
            return null;
        }

        var endpoint = connection.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2))
            .FirstOrDefault(kv => kv.Length == 2 && kv[0].Equals("IngestionEndpoint", StringComparison.OrdinalIgnoreCase))?[1];
        return AbsoluteUri(endpoint) ?? "https://dc.services.visualstudio.com/";
    }

    private static string? AbsoluteUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.ToString() : null;

    private static void AddReachability(IHealthChecksBuilder checks, string name, string url, string tag, bool okOnly = false) =>
        checks.AddUrlGroup(
            o => o.AddUri(new Uri(url), s => s.ExpectHttpCodes(200, okOnly ? 299 : 499)),
            name: name,
            failureStatus: HealthStatus.Degraded,
            tags: [tag],
            timeout: TimeSpan.FromSeconds(5));
}
