using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using PoFightJudge.Api.Features.Ai;
using PoFightJudge.Api.Features.Diagnostics;

namespace PoFightJudge.Api.Features.Voice;

/// <summary>
/// Azure OpenAI speech-to-text for a spoken turn: the WAV goes up as a multipart <c>audio/transcriptions</c> call.
/// A transcription model only transcribes, so the "you are not a participant" instruction Gemini needed has no
/// job here; the prompt is a hint for the silence token instead.
/// </summary>
public sealed partial class AzureTranscriptionService(IHttpClientFactory httpClients, GeminiModelOptions models, AiLatencyTracker latency, ILogger<AzureTranscriptionService> logger) : ITranscriptionService
{
    public string Name => "azure";

    public bool IsEnabled => true;

    public async Task<string> TranscribeAsync(byte[] wav, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wav);
        if (wav.Length == 0)
        {
            return string.Empty;
        }

        // The gpt-4o-transcribe family needs a chunking strategy; whisper, which would not, invents "you" for a
        // silent clip where this family returns nothing (measured 2026-10-27).
        using var form = AzureAudio.TranscriptionForm(wav, models.TurnTranscribe, "json", ("chunking_strategy", "auto"));
        var started = Stopwatch.GetTimestamp();
        var body = await AzureAudio.TranscribeAsync(httpClients.CreateClient(GeminiHttpClients.Fast), models.TurnTranscribe, form, GeminiTranscriptionService.Operation, ct);
        var elapsed = Stopwatch.GetElapsedTime(started);
        latency.Record(GeminiTranscriptionService.Operation, elapsed.TotalMilliseconds);
        LogCompleted(logger, models.TurnTranscribe, wav.Length, elapsed.TotalMilliseconds);

        return Transcripts.Clean(body?["text"]?.GetValue<string>() ?? string.Empty);
    }

    [LoggerMessage(EventId = 6112, Level = LogLevel.Information, Message = "Transcribe completed via azure {Model}: {Bytes} bytes in {ElapsedMs:F0}ms")]
    private static partial void LogCompleted(ILogger logger, string model, int bytes, double elapsedMs);
}

/// <summary>The <c>audio/transcriptions</c> call both the turn and the fight transcriber make.</summary>
/// <remarks>
/// On the deployment's own path, not the v1 one: on this resource v1 answers DeploymentNotFound for every
/// transcription deployment while the deployment path serves them (measured 2026-10-27).
/// </remarks>
public static class AzureAudio
{
    public const string ApiVersion = "2025-03-01-preview";

    /// <summary>Relative to the v1 base address the named clients carry.</summary>
    public static Uri TranscriptionsPath(string deployment) =>
        new($"../deployments/{Uri.EscapeDataString(deployment)}/audio/transcriptions?api-version={ApiVersion}", UriKind.Relative);

    /// <summary>The audio and its settings as one form; <paramref name="extra"/> adds fields one model needs and another refuses.</summary>
    public static MultipartFormDataContent TranscriptionForm(byte[] wav, string model, string responseFormat, params (string Name, string Value)[] extra)
    {
        ArgumentNullException.ThrowIfNull(extra);
#pragma warning disable CA2000 // The form owns its parts and disposes them with itself.
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        var form = new MultipartFormDataContent
        {
            { file, "file", "audio.wav" },
            { new StringContent(model), "model" },
            { new StringContent(responseFormat), "response_format" },
        };
        foreach (var (name, value) in extra)
        {
            form.Add(new StringContent(value), name);
        }
#pragma warning restore CA2000

        return form;
    }

    public static async Task<JsonObject?> TranscribeAsync(HttpClient client, string deployment, MultipartFormDataContent form, string operation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        using var response = await client.PostAsync(TranscriptionsPath(deployment), form, ct);
        await GeminiHttp.EnsureSuccessAsync(response, operation, ct);
        return GeminiHttp.TryParse(await response.Content.ReadAsStringAsync(ct));
    }
}
