using System.Net.Http.Headers;
using PoFightJudge.Api.Features.Ai.Fakes;
using PoFightJudge.Api.Features.Diagnostics;
using PoFightJudge.Api.Features.Voice;
using PoFightJudge.Api.Features.Watch;
using PoFightJudge.Shared.Configuration;

namespace PoFightJudge.Api.Features.Ai;

/// <summary>
/// The one decision every AI seam consults: real providers or the deterministic fakes. Made once at startup from
/// the environment, the Gemini key and the <c>UseFakeAi</c> flag; the reason is logged so a "why is it fake" is a
/// one-line grep. <paramref name="AzureEndpoint"/> set means the provider is Azure OpenAI rather than Gemini.
/// </summary>
public sealed record AiMode(bool UseFakes, bool HasGeminiKey, string Reason, Uri? AzureEndpoint = null)
{
    public bool UsesAzure => !UseFakes && AzureEndpoint is not null;
}

public static class AiServiceExtensions
{
    /// <summary>
    /// Registers the mode, the model ids, the latency tracker, the named Gemini clients and the text seam (real client or
    /// the schema-driven fake). TTS, live and analysis register in their own slices and read <see cref="AiMode"/> to pick
    /// a side; the transport is always there so a real call can be made the moment a key appears.
    /// </summary>
    public static AiMode AddPoAi(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var apiKey = configuration[ConfigKeys.Ai.GeminiApiKey];
        var hasKey = !string.IsNullOrWhiteSpace(apiKey);
        var azure = Uri.TryCreate(configuration[ConfigKeys.Ai.AzureOpenAiEndpoint], UriKind.Absolute, out var endpoint) ? endpoint : null;
        var forced = configuration.GetValue<bool>(Toggles.UseFakes);
        var useFakes = DiagEndpoints.IsFakeAi(environment, configuration);

        var reason = (useFakes, hasKey, forced) switch
        {
            (true, _, true) => $"{Toggles.UseFakes} is on",
            (true, _, _) => "no Gemini key or Azure OpenAI endpoint configured outside Production",
            (false, _, _) when azure is not null => $"Azure OpenAI at {azure.Host}",
            (false, true, _) => "Gemini key present",
            (false, false, _) => "Production without a Gemini key — AI calls fail until the secret is set (degraded mode)",
        };

        var mode = new AiMode(useFakes, hasKey, reason, azure);
        services.AddSingleton(mode);
        services.AddSingleton(GeminiModelOptions.FromConfiguration(configuration));
        services.AddSingleton<AiLatencyTracker>();
        services.AddGeminiClients(apiKey, mode.UsesAzure ? azure : null);

        if (useFakes)
        {
            services.AddSingleton<IGeminiText>(sp => new FakeGeminiText(sp.GetRequiredService<AiLatencyTracker>()));
            services.AddSingleton<ITtsProvider, FakeTts>();

            // GET /features reads it; without a registration the endpoint infers a body and the host will not start.
            services.AddSingleton(new FishAudioOptions(Enabled: false));

            // The WATCH fake sits at the game's seam rather than the transport's: a whole match plays out from the
            // personas' own words, which the schema-driven text fake could not do on its own.
            services.AddSingleton<IWatchAi>(_ => new FakeWatchAi());
        }
        else if (mode.UsesAzure)
        {
            services.AddSingleton<IGeminiText, AzureOpenAiText>();
            AddFishAudio(services, configuration);
            services.AddSingleton<ITtsProvider, AzureTtsService>();
            services.AddSingleton<IWatchAi, WatchAi>();
        }
        else
        {
            services.AddSingleton<IGeminiText, GeminiTextClient>();

            // Registration order is the voice chain. A persona's own cloned voice leads where there is one, because
            // sounding like the character is the entire point of giving it one; Gemini speaks for everybody else and
            // catches anything Fish refuses.
            AddFishAudio(services, configuration);
            services.AddSingleton<ITtsProvider, GeminiTtsService>();
            services.AddSingleton<IWatchAi, WatchAi>();
        }

        return mode;
    }

    /// <summary>
    /// Fish Audio, when a key is configured. Absent one the provider is not registered at all, so the chain is
    /// Gemini alone and a persona's reference id sits there harmlessly until somebody sets the secret.
    /// </summary>
    private static void AddFishAudio(IServiceCollection services, IConfiguration configuration)
    {
        var key = configuration[ConfigKeys.Ai.FishAudioApiKey]
            ?? Environment.GetEnvironmentVariable(ConfigKeys.Ai.FishAudioApiKeyEnvVar);
        if (string.IsNullOrWhiteSpace(key))
        {
            services.AddSingleton(new FishAudioOptions(Enabled: false));
            return;
        }

        services.AddSingleton(new FishAudioOptions(Enabled: true, configuration[ConfigKeys.Ai.FishDefaultReferenceId]));
        services.AddHttpClient(FishAudioService.ClientName, client =>
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);

            // Cloning a voice takes longer than reading one off a shelf, and the round is already waiting on it.
            client.Timeout = TimeSpan.FromSeconds(60);
        });

        services.AddSingleton<ITtsProvider, FishAudioService>();
    }
}
