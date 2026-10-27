using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace PoFightJudge.Api.Features.Diagnostics;

public static class DiagnosticsServiceExtensions
{
    /// <summary>
    /// Registers the health checks. <c>ready</c>: configuration and storage. <c>ai</c>: TLS reachability of
    /// the AI hosts — any HTTP answer counts, because the point is DNS + TLS, not an authenticated call; failures only
    /// degrade, never fail, so an upstream blip cannot take the site out of rotation.
    /// </summary>
    public static IServiceCollection AddPoDiagnostics(this IServiceCollection services, IConfiguration configuration)
    {
        var checks = services.AddHealthChecks()
            .AddCheck<ConfigurationHealthCheck>("configuration", tags: [ReadyTagName]);

        AddReachability(checks, "gemini-reachable", "https://generativelanguage.googleapis.com/");

        return services;
    }

    private const string ReadyTagName = HealthEndpoints.ReadyTag;

    private static void AddReachability(IHealthChecksBuilder checks, string name, string url) =>
        checks.AddUrlGroup(
            o => o.AddUri(new Uri(url), s => s.ExpectHttpCodes(200, 499)),
            name: name,
            failureStatus: HealthStatus.Degraded,
            tags: [HealthEndpoints.AiTag],
            timeout: TimeSpan.FromSeconds(5));
}
