using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PoFightJudge.Api.Features.Ai;
using PoFightJudge.Api.Features.Ai.Fakes;
using PoFightJudge.Api.Features.Live;

namespace PoFightJudge.Api.Features.Fight;

public static partial class FightServiceExtensions
{
    /// <summary>
    /// Everything the live fight needs: the timing rules, the socket, a client factory that answers with either the
    /// real host or the scripted one, the registry that owns running fights, and SignalR carrying MessagePack.
    /// </summary>
    /// <remarks>
    /// MessagePack rather than JSON because the payload is mostly raw audio in both directions, and base64 in JSON
    /// would add a third again to every frame of it.
    /// </remarks>
    public static IServiceCollection AddPoFight(this IServiceCollection services, IConfiguration configuration, AiMode ai)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(ai);

        services.Configure<DebateOptions>(configuration.GetSection(DebateOptions.Section));
        services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<DebateOptions>>().Value);
        services.AddSingleton(LiveOptions.FromConfiguration(configuration));
        services.AddSingleton<LiveSocketConnector>(_ => LiveSockets.ConnectAsync);

        services.AddSingleton<LiveClientFactory>(sp => ai.UseFakes
            ? () => new FakeLiveClient(sp.GetRequiredService<TimeProvider>())
            : ai.UsesAzure
            ? () => new AzureRealtimeClient(
                ct => AzureRealtimeClient.ConnectAsync(
                    AzureRealtimeClient.AddressFor(ai.AzureEndpoint!, sp.GetRequiredService<GeminiModelOptions>().Live),
                    sp.GetRequiredService<Azure.Core.TokenCredential>(),
                    ct),
                sp.GetRequiredService<LiveOptions>(),
                sp.GetRequiredService<GeminiModelOptions>(),
                sp.GetRequiredService<ILogger<AzureRealtimeClient>>())
            : () => new GeminiLiveClient(
                sp.GetRequiredService<LiveSocketConnector>(),
                sp.GetRequiredService<LiveOptions>(),
                sp.GetRequiredService<GeminiModelOptions>(),
                sp.GetRequiredService<ILogger<GeminiLiveClient>>()));

        services.AddSingleton<LiveSinkFactory>(sp =>
            matchId => new HubClientSink(sp.GetRequiredService<IHubContext<LiveHub>>(), matchId));

        // Somewhere for a finished fight to be handed over. The pipeline that actually reads it registers later and
        // wins the resolve; until then a fight is still recorded and marked for analysis, it just is not analysed yet.
        services.TryAddSingleton<IAnalysisIntake, UnreadAnalysisIntake>();

        services.AddSingleton<SessionRegistry>();
        services.AddHostedService(sp => sp.GetRequiredService<SessionRegistry>());

        services.AddSignalR(hub => hub.EnableDetailedErrors = false).AddMessagePackProtocol();
        return services;
    }

    /// <summary>Accepts a finished fight and does nothing with it. Replaced by the real pipeline when it arrives.</summary>
    private sealed partial class UnreadAnalysisIntake(ILogger<UnreadAnalysisIntake> logger) : IAnalysisIntake
    {
        public ValueTask SubmitAsync(string userId, Shared.Identifiers.MatchId matchId, CancellationToken ct)
        {
            LogNotAnalysed(logger, matchId.Value);
            return ValueTask.CompletedTask;
        }

        [LoggerMessage(EventId = 4110, Level = LogLevel.Information, Message = "Fight {MatchId} is recorded but there is no analysis pipeline to read it yet")]
        private static partial void LogNotAnalysed(ILogger logger, string matchId);
    }
}
