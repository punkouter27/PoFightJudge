using System.Text;
using System.Text.Json.Nodes;
using PoFightJudge.Api.Features.Ai;
using PoFightJudge.Api.Features.Live;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Api.Features.Analysis;

/// <summary>Diarized transcription of a whole recording, used when the live captions are missing or unusable.</summary>
public interface IGeminiTranscribeClient
{
    Task<TranscriptDto> TranscribeAsync(string fileUri, string mimeType, CancellationToken ct);
}

public sealed class GeminiTranscribeClient(IHttpClientFactory factory, GeminiModelOptions models, TimeProvider clock) : IGeminiTranscribeClient
{
    public async Task<TranscriptDto> TranscribeAsync(string fileUri, string mimeType, CancellationToken ct)
    {
        using var http = factory.CreateClient(GeminiHttpClients.Analysis);
        var backoff = new PollBackoff(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(4), clock);
        var node = await GeminiHttp.RunInteractionAsync(http, BuildRequest(models.Transcribe, fileUri, mimeType), "transcribe", backoff, ct);
        return ParseResponse(node);
    }

    public static string BuildRequest(string model, string fileUri, string mimeType) => new JsonObject
    {
        ["model"] = model,
        ["input"] = new JsonArray(new JsonObject { ["type"] = "audio", ["uri"] = fileUri, ["mime_type"] = mimeType }),
        ["generation_config"] = new JsonObject
        {
            ["transcription_config"] = new JsonObject
            {
                // Without the granularity the response carries text only, and the words have no offsets to map.
                ["mode"] = new JsonObject
                {
                    ["type"] = "verbatim",
                    ["diarization_mode"] = "speaker",
                    ["timestamp_granularities"] = new JsonArray("word"),
                },
            },
        },
    }.ToJsonString();

    /// <summary>
    /// Reads whichever shape the answer came back in. The words matter more than the text: without offsets there is
    /// nothing to attribute to a speaker.
    /// </summary>
    public static TranscriptDto ParseResponse(JsonNode? root)
    {
        var words = new List<TranscriptWord>();
        var text = new StringBuilder();
        foreach (var content in GeminiHttp.OutputContents(root))
        {
            if (content["text"] is { } spoken)
            {
                if (text.Length > 0)
                {
                    text.Append(' ');
                }

                text.Append(spoken.GetValue<string>());
            }

            foreach (var annotation in (content["annotations"] as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                if (annotation["type"]?.GetValue<string>() is not (null or "word_info"))
                {
                    continue;
                }

                var word = annotation["text"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(word))
                {
                    continue;
                }

                words.Add(new TranscriptWord(
                    word,
                    annotation["speaker"]?.GetValue<string>() ?? "spk_1",
                    LiveMessageParser.ParseDuration(annotation["start_offset"]?.ToString()).TotalSeconds,
                    LiveMessageParser.ParseDuration(annotation["end_offset"]?.ToString()).TotalSeconds));
            }
        }

        return new TranscriptDto(text.ToString(), words);
    }
}
