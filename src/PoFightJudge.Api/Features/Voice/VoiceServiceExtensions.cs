using PoFightJudge.Api.Features.Ai;
using PoFightJudge.Api.Features.Ai.Fakes;
using PoFightJudge.Shared.Configuration;

namespace PoFightJudge.Api.Features.Voice;

/// <summary>
/// Registers the voice slice: the caching service in front of the one provider <c>AddPoAi</c> registered (Gemini TTS,
/// or the fake), and the transcriber that finishes a spoken turn.
/// </summary>
public static class VoiceServiceExtensions
{
    public static IServiceCollection AddPoVoice(this IServiceCollection services, IConfiguration configuration, AiMode mode)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(mode);

        services.AddSingleton(new TtsRoutingOptions(
            WireFormat: TtsAudioFormats.Normalize(configuration[ConfigKeys.Ai.TtsWireFormat]),
            CacheEnabled: configuration.GetValue<bool>(Toggles.TtsCache)));

        // Caching is a flag: off means every line is synthesized fresh, which is what a prompt-tuning session wants.
        if (configuration.GetValue<bool>(Toggles.TtsCache))
        {
            services.AddSingleton<ITtsCache, BlobTtsCache>();
        }
        else
        {
            services.AddSingleton<ITtsCache, NullTtsCache>();
        }

        services.AddSingleton<CachingTtsService>();

        // The fake stands in whenever the AI mode is fake, so a spoken turn works with no key at all.
        if (mode.UseFakes)
        {
            services.AddSingleton<ITranscriptionService, FakeTranscription>();
        }
        else if (mode.UsesAzure)
        {
            services.AddSingleton<ITranscriptionService, AzureTranscriptionService>();
        }
        else
        {
            services.AddSingleton<ITranscriptionService, GeminiTranscriptionService>();
        }

        return services;
    }
}
