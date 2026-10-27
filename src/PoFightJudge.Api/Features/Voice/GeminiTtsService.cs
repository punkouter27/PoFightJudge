using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using PoFightJudge.Api.Features.Ai;
using PoFightJudge.Api.Features.Analysis;
using PoFightJudge.Api.Features.Diagnostics;
using PoFightJudge.Api.Features.Profiles;

namespace PoFightJudge.Api.Features.Voice;

/// <summary>
/// Gemini TTS over the Interactions API (the surface the speech docs document for <c>gemini-3.1-flash-tts-preview</c>,
/// verified 2026-09-06): <c>POST v1beta/interactions</c> with <c>response_format.type = audio</c> and one
/// <c>speech_config</c> voice; the answer carries base64 PCM (24 kHz, 16-bit, mono) in <c>output_audio.data</c>.
/// There is no numeric pitch/rate knob, so a persona's sliders are rendered as a spoken-style instruction prefix.
/// </summary>
public sealed partial class GeminiTtsService(IHttpClientFactory httpClients, GeminiModelOptions models, AiLatencyTracker latency, TimeProvider clock, ILogger<GeminiTtsService> logger) : ITtsProvider
{
    public const string Operation = "tts";

    /// <summary>How long a line may stay "running" before it is given up on and comes back silent.</summary>
    private static readonly TimeSpan PollBudget = TimeSpan.FromSeconds(60);

    public string Name => "gemini";

    public bool IsFake => false;

    public async Task<TtsAudio> SynthesizeAsync(string text, TtsSettings settings, CancellationToken ct = default)
    {
        var client = httpClients.CreateClient(GeminiHttpClients.Tts);
        var started = Stopwatch.GetTimestamp();
        var backoff = new PollBackoff(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(3), PollBudget, clock);
        var node = await GeminiHttp.RunInteractionAsync(client, BuildRequest(models.Tts, text, settings).ToJsonString(JsonSerializerOptions.Web), Operation, backoff, ct);
        var elapsed = Stopwatch.GetElapsedTime(started);
        latency.Record(Operation, elapsed.TotalMilliseconds);

        var audio = ExtractAudio(node);
        LogSynthesized(logger, models.Tts, settings.VoiceName, text.Length, elapsed.TotalMilliseconds, audio.IsEmpty);
        return audio;
    }

    public static JsonObject BuildRequest(string model, string text, TtsSettings settings) => new()
    {
        ["model"] = model,
        ["input"] = ApplyVoiceStyle(text, settings),
        ["response_format"] = new JsonObject { ["type"] = "audio" },
        ["generation_config"] = new JsonObject
        {
            ["speech_config"] = new JsonArray(new JsonObject { ["voice"] = settings.VoiceName }),
        },
    };

    /// <summary>
    /// Turns a persona's numeric pitch (~0.7–1.5) and speed (~0.85–1.35) into the natural-language direction Gemini TTS
    /// honours. Neutral settings add no prefix so lines stay natural.
    /// </summary>
    public static string ApplyVoiceStyle(string text, TtsSettings settings) =>
        VoiceStyle(settings) is { } style ? $"Say {style}: {text}" : text;

    /// <summary>The delivery direction alone ("slowly and deliberately, in a low, deep voice"), or null when neutral.</summary>
    public static string? VoiceStyle(TtsSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var styles = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(settings.Accent))
        {
            styles.Add($"in a {settings.Accent.Trim()} accent");
        }

        if (settings.Pitch <= 0.85)
        {
            styles.Add("in a low, deep voice");
        }
        else if (settings.Pitch >= 1.2)
        {
            styles.Add("in a high-pitched voice");
        }

        if (settings.Speed <= 0.92)
        {
            styles.Add("slowly and deliberately");
        }
        else if (settings.Speed >= 1.15)
        {
            styles.Add("quickly");
        }

        return styles.Count == 0 ? null : string.Join(", ", styles);
    }

    /// <summary>
    /// <c>output_audio.data</c> is what the SDKs expose; the raw resource also lists audio content items under
    /// <c>outputs</c> or <c>steps[].content</c>, so both are read. Empty when the answer carried no audio.
    /// </summary>
    public static TtsAudio ExtractAudio(JsonObject? node)
    {
        if (node?["output_audio"]?["data"]?.GetValue<string>() is { Length: > 0 } direct)
        {
            return TtsAudio.Pcm(direct);
        }

        var items = ((node?["outputs"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .Concat(((node?["steps"] as JsonArray)?.OfType<JsonObject>() ?? []).SelectMany(s => (s["content"] as JsonArray)?.OfType<JsonObject>() ?? []));
        foreach (var item in items)
        {
            if (string.Equals(item["type"]?.GetValue<string>(), "audio", StringComparison.Ordinal) && item["data"]?.GetValue<string>() is { Length: > 0 } data)
            {
                return TtsAudio.Pcm(data);
            }
        }

        return TtsAudio.None;
    }

    [LoggerMessage(EventId = 3101, Level = LogLevel.Information, Message = "Gemini TTS {Model} voice {Voice} synthesized {Chars} chars in {ElapsedMs:F0}ms (empty: {Empty})")]
    private static partial void LogSynthesized(ILogger logger, string model, string voice, int chars, double elapsedMs, bool empty);
}
