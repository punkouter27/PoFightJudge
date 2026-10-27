using PoFightJudge.Shared.Configuration;

namespace PoFightJudge.Api.Features.Diagnostics;

/// <summary>
/// Checks required configuration once at startup. Production is marked degraded (never crashed); other environments
/// only warn — a missing Gemini key there simply means the fakes take over.
/// </summary>
public sealed partial class StartupSecretValidator(
    IConfiguration configuration,
    IHostEnvironment environment,
    StartupHealthState healthState,
    ILogger<StartupSecretValidator> logger) : IHostedService
{
    public static readonly string[] RequiredKeys =
    [
        ConfigKeys.Ai.GeminiApiKey,
    ];

    /// <summary>
    /// Entra identity and the storage endpoints. The endpoints are unconditional in Production: DefaultAzureCredential
    /// is the only storage credential, so there is no connection string that could stand in for them.
    /// </summary>
    public static readonly string[] ProductionOnlyKeys =
    [
        ConfigKeys.AzureAd.TenantId,
        ConfigKeys.AzureAd.ClientId,
        ConfigKeys.Storage.TableEndpoint,
        ConfigKeys.Storage.BlobEndpoint,
    ];

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var missing = FindMissing(configuration, environment.IsProduction());
        if (missing.Count == 0)
        {
            LogClean(logger);
            return Task.CompletedTask;
        }

        if (environment.IsProduction())
        {
            healthState.MarkDegraded(missing);
            LogDegraded(logger, missing);
        }
        else
        {
            LogMissingDev(logger, environment.EnvironmentName, missing);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public static IReadOnlyList<string> FindMissing(IConfiguration configuration, bool isProduction)
    {
        IEnumerable<string> keys = isProduction ? [.. RequiredKeys, .. ProductionOnlyKeys] : RequiredKeys;

        // Azure OpenAI is reached with the app's identity: with its endpoint set, there is no AI key to be missing.
        var azure = !string.IsNullOrWhiteSpace(configuration[ConfigKeys.Ai.AzureOpenAiEndpoint]);
        return [.. keys.Where(k => string.IsNullOrWhiteSpace(configuration[k]) && !(azure && string.Equals(k, ConfigKeys.Ai.GeminiApiKey, StringComparison.Ordinal)))];
    }

    [LoggerMessage(EventId = 2000, Level = LogLevel.Information, Message = "Startup configuration validation passed")]
    private static partial void LogClean(ILogger logger);

    [LoggerMessage(EventId = 2001, Level = LogLevel.Critical, Message = "PRODUCTION DEGRADED — missing required configuration: {Keys}. Serving the static shell + /api/diag; every other /api/* route answers 503 until Key Vault is fixed.")]
    private static partial void LogDegraded(ILogger logger, IReadOnlyList<string> keys);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Warning, Message = "Configuration missing in {Environment}: {Keys}. AI falls back to the fakes; set the Key Vault secret (PoFightJudge--*) or the env var to use the real provider.")]
    private static partial void LogMissingDev(ILogger logger, string environment, IReadOnlyList<string> keys);
}
