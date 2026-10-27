using PoFightJudge.Api.Features.Ai;
using PoFightJudge.Api.Features.Ai.Fakes;
using PoFightJudge.Api.Features.Fight;

namespace PoFightJudge.Api.Features.Analysis;

public static class AnalysisServiceExtensions
{
    /// <summary>
    /// The post-fight pipeline and the three clients it drives. Registered after the fight, so the real intake
    /// replaces the placeholder that only logs: from here a finished fight is actually read.
    /// </summary>
    public static IServiceCollection AddPoAnalysis(this IServiceCollection services, IConfiguration configuration, AiMode ai)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(ai);

        services.Configure<AnalysisOptions>(configuration.GetSection(AnalysisOptions.Section));

        if (ai.UseFakes)
        {
            services.AddSingleton<IGeminiFilesClient, FakeAnalysisClients.Files>();
            services.AddSingleton<IGeminiTranscribeClient, FakeAnalysisClients.Transcriber>();
            services.AddSingleton<IGeminiJudgeClient, FakeAnalysisClients.Judge>();
        }
        else if (ai.UsesAzure)
        {
            services.AddSingleton<IGeminiFilesClient, AzureRecordingFiles>();
            services.AddSingleton<IGeminiTranscribeClient, AzureTranscribeClient>();
            services.AddSingleton<IGeminiJudgeClient, AzureJudgeClient>();
            services.AddSingleton<IClaimChecker, AzureClaimChecker>();
        }
        else
        {
            services.AddSingleton<IGeminiFilesClient, GeminiFilesClient>();
            services.AddSingleton<IGeminiTranscribeClient, GeminiTranscribeClient>();
            services.AddSingleton<IGeminiJudgeClient, GeminiJudgeClient>();

            // Real only: the stand-in judge makes no claims, so there is nothing for a stand-in checker to check.
            services.AddSingleton<IClaimChecker, GeminiClaimChecker>();
        }

        services.AddSingleton<AnalysisPipeline>();
        services.AddSingleton<IAnalysisIntake>(sp => sp.GetRequiredService<AnalysisPipeline>());
        services.AddHostedService(sp => sp.GetRequiredService<AnalysisPipeline>());

        // The repository is scoped, so the resumer is too; the hosted service opens a scope for its one pass.
        services.AddScoped<AnalysisResumer>();
        services.AddHostedService<AnalysisResumeService>();
        return services;
    }
}
