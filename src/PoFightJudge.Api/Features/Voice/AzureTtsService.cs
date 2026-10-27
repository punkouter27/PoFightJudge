using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using PoFightJudge.Api.Features.Ai;
using PoFightJudge.Api.Features.Diagnostics;
using PoFightJudge.Api.Features.Profiles;

namespace PoFightJudge.Api.Features.Voice;

/// <summary>
/// Azure OpenAI speech (<c>gpt-4o-mini-tts</c>): <c>POST audio/speech</c> asking for raw PCM, which is 24 kHz 16-bit
/// mono, the format Gemini TTS returned, so nothing downstream changes. Unlike Gemini this model takes its delivery
/// direction separately, as <c>instructions</c>, so the persona's pitch and pace never get read out as words.
/// </summary>
public sealed partial class AzureTtsService(IHttpClientFactory httpClients, GeminiModelOptions models, AiLatencyTracker latency, ILogger<AzureTtsService> logger) : ITtsProvider
{
    public string Name => "azure";

    public bool IsFake => false;

    public async Task<TtsAudio> SynthesizeAsync(string text, TtsSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var client = httpClients.CreateClient(GeminiHttpClients.Tts);
        using var content = new StringContent(BuildRequest(models.Tts, text, settings).ToJsonString(), Encoding.UTF8, "application/json");

        var started = Stopwatch.GetTimestamp();
        using var response = await client.PostAsync(new Uri("audio/speech", UriKind.Relative), content, ct);
        await GeminiHttp.EnsureSuccessAsync(response, GeminiTtsService.Operation, ct);
        var pcm = await response.Content.ReadAsByteArrayAsync(ct);
        var elapsed = Stopwatch.GetElapsedTime(started);
        latency.Record(GeminiTtsService.Operation, elapsed.TotalMilliseconds);

        LogSynthesized(logger, models.Tts, settings.VoiceName, text.Length, elapsed.TotalMilliseconds, pcm.Length);
        return pcm.Length == 0 ? TtsAudio.None : TtsAudio.Pcm(Convert.ToBase64String(pcm));
    }

    public static JsonObject BuildRequest(string model, string text, TtsSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var request = new JsonObject
        {
            ["model"] = model,
            ["input"] = text,
            ["voice"] = AzureVoices.For(settings.VoiceName),
            ["response_format"] = "pcm",
        };

        if (GeminiTtsService.VoiceStyle(settings) is { } style)
        {
            request["instructions"] = $"Speak {style}.";
        }

        return request;
    }

    [LoggerMessage(EventId = 3111, Level = LogLevel.Information, Message = "Azure TTS {Model} voice {Voice} synthesized {Chars} chars in {ElapsedMs:F0}ms ({Bytes} bytes)")]
    private static partial void LogSynthesized(ILogger logger, string model, string voice, int chars, double elapsedMs, int bytes);
}

/// <summary>
/// The personas and hosts were cast with Gemini's voices. Each one is given the OpenAI voice nearest in register, so
/// a cast keeps its men and women apart and the same persona always sounds the same. Only voices that both speech
/// and the realtime host accept are used, so a host sounds alike on either.
/// </summary>
public static class AzureVoices
{
    private static readonly string[] Lower = ["ash", "ballad", "echo", "verse"];

    private static readonly string[] Higher = ["coral", "sage", "shimmer"];

    private static readonly HashSet<string> Own = new([.. Lower, .. Higher, "alloy"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Gemini's prebuilt voices that are higher in register; the rest are lower.</summary>
    private static readonly HashSet<string> GeminiHigher = new(
        ["Zephyr", "Kore", "Leda", "Aoede", "Callirrhoe", "Autonoe", "Despina", "Erinome", "Laomedeia", "Achernar", "Gacrux", "Pulcherrima", "Vindemiatrix", "Sulafat"],
        StringComparer.OrdinalIgnoreCase);

    public static string For(string? voice)
    {
        if (string.IsNullOrWhiteSpace(voice))
        {
            return "alloy";
        }

        if (Own.Contains(voice))
        {
            return voice.ToLowerInvariant();
        }

        var pool = GeminiHigher.Contains(voice) ? Higher : Lower;

        // A stable spread, not string.GetHashCode, which changes from one process to the next.
        var hash = voice.ToUpperInvariant().Aggregate(17, (h, c) => unchecked((h * 31) + c));
        return pool[(int)((uint)hash % (uint)pool.Length)];
    }
}
