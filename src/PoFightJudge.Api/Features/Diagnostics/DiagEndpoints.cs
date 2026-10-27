using Carter;
using PoFightJudge.Api.Common;
using PoFightJudge.Api.Features.Auth;
using PoFightJudge.Shared;
using PoFightJudge.Shared.Configuration;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Api.Features.Diagnostics;

/// <summary>
/// Authenticated diagnostics: environment, readiness, which providers are configured (presence, never values), the
/// model ids in use, the toggles as set, and the AI latency/token tables. Admin-only in Production.
/// </summary>
public sealed class DiagEndpoints : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app) =>
        app.MapGet(ApiRoutes.Diag.Url, (HttpContext ctx, IConfiguration config, IHostEnvironment env, StartupHealthState health, AiLatencyTracker latency) =>
        {
            if (env.IsProduction() && !ctx.User.IsAdmin())
            {
                return Results.Forbid();
            }

            var toggles = Toggles.All.ToDictionary(t => t, config.GetValue<bool>, StringComparer.Ordinal);

            return Results.Ok(new DiagDto(
                env.EnvironmentName,
                health.IsDegraded ? "degraded" : "ok",
                health.MissingKeys,
                IsFakeAi(env, config),
                SecretMasker.Presence(config[ConfigKeys.Ai.GeminiApiKey]),
                SecretMasker.Presence(config[ConfigKeys.Storage.TableEndpoint]),
                SecretMasker.Presence(config[ConfigKeys.Storage.BlobEndpoint]),
                new ModelIdsDto(
                    config[ConfigKeys.Ai.RoundModel] ?? string.Empty,
                    config[ConfigKeys.Ai.JudgeModel] ?? string.Empty,
                    config[ConfigKeys.Ai.ProfileModel] ?? string.Empty,
                    config[ConfigKeys.Ai.TtsModel] ?? string.Empty,
                    config[ConfigKeys.Ai.LiveModel] ?? string.Empty,
                    config[ConfigKeys.Ai.TranscribeModel] ?? string.Empty,
                    config[ConfigKeys.Ai.TurnTranscribeModel] ?? string.Empty,
                    config[ConfigKeys.Ai.Voice] ?? string.Empty),
                toggles,
                latency.Snapshot(),
                latency.UsageSnapshot()));
        }).RequireAuthorization().WithTags("Diagnostics");

    /// <summary>
    /// The fakes run only outside Production, when the toggle forces them or when no Gemini key is present. The AI
    /// registration makes the same decision; this stays the single formula both consult.
    /// </summary>
    public static bool IsFakeAi(IHostEnvironment env, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return !env.IsProduction()
            && (config.GetValue<bool>(Toggles.UseFakes)
                || (string.IsNullOrWhiteSpace(config[ConfigKeys.Ai.GeminiApiKey]) && string.IsNullOrWhiteSpace(config[ConfigKeys.Ai.AzureOpenAiEndpoint])));
    }
}
