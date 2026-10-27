using System.Globalization;
using System.Text.Json.Nodes;
using PoFightJudge.Api.Features.Ai;
using PoFightJudge.Api.Features.Voice;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Api.Features.Analysis;

/// <summary>
/// Azure has no Files API: a transcription call takes the audio itself. So the "upload" is a temp file the
/// transcriber reads from, and "delete" removes it — the pipeline's lifetime for the recording stays exactly as it is.
/// </summary>
public sealed class AzureRecordingFiles : IGeminiFilesClient
{
    public async Task<GeminiFile> UploadAsync(Stream content, long length, string mimeType, string displayName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        var path = Path.Combine(Path.GetTempPath(), $"pofight-{Guid.NewGuid():N}.wav");
        await using (var file = File.Create(path))
        {
            await content.CopyToAsync(file, ct);
        }

        return new GeminiFile(path, path, "ACTIVE", mimeType);
    }

    public Task<GeminiFile> GetAsync(string name, CancellationToken ct) =>
        Task.FromResult(new GeminiFile(name, name, File.Exists(name) ? "ACTIVE" : "FAILED", "audio/wav"));

    public Task DeleteAsync(string name, CancellationToken ct)
    {
        File.Delete(name);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Diarized transcription on <c>gpt-4o-transcribe-diarize</c>. The service takes 25 MB a call, which a long fight
/// passes, so the recording goes in ten-minute pieces. Each piece names its own speakers ("A", "B"), so the labels
/// are kept per piece; the speaker mapper assigns every label to a player by when it spoke, which does not care how
/// many labels there are.
/// </summary>
public sealed class AzureTranscribeClient(IHttpClientFactory factory, GeminiModelOptions models) : IGeminiTranscribeClient
{
    public const double PieceSeconds = 600;

    public async Task<TranscriptDto> TranscribeAsync(string fileUri, string mimeType, CancellationToken ct)
    {
        var wav = await File.ReadAllBytesAsync(fileUri, ct);
        var http = factory.CreateClient(GeminiHttpClients.Analysis);
        var words = new List<TranscriptWord>();
        var text = new List<string>();
        for (var piece = 0; WavSlicer.Slice(wav, piece * PieceSeconds, (piece + 1) * PieceSeconds) is { } clip; piece++)
        {
            using var form = AzureAudio.TranscriptionForm(clip, models.Transcribe, "diarized_json", ("chunking_strategy", "auto"));
            var body = await AzureAudio.TranscribeAsync(http, models.Transcribe, form, "transcribe", ct);
            var (spoken, pieceWords) = ParseResponse(body, piece * PieceSeconds, $"p{piece}_");
            text.Add(spoken);
            words.AddRange(pieceWords);
        }

        return new TranscriptDto(string.Join(' ', text.Where(t => t.Length > 0)), words);
    }

    /// <summary>
    /// The answer has segments (speaker, start, end, text) rather than words. The words a segment holds are spread
    /// evenly across it, which is as close as the speaker mapper needs: it weighs time, not exact word onsets.
    /// </summary>
    public static (string Text, List<TranscriptWord> Words) ParseResponse(JsonObject? body, double offset, string labelPrefix)
    {
        var words = new List<TranscriptWord>();
        foreach (var segment in (body?["segments"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            var spoken = (segment["text"]?.GetValue<string>() ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (spoken.Length == 0)
            {
                continue;
            }

            var start = Seconds(segment["start"]);
            var step = Math.Max(0, Seconds(segment["end"]) - start) / spoken.Length;
            var label = labelPrefix + (segment["speaker"]?.ToString() ?? "A");
            for (var i = 0; i < spoken.Length; i++)
            {
                words.Add(new TranscriptWord(spoken[i], label, offset + start + (i * step), offset + start + ((i + 1) * step)));
            }
        }

        return (body?["text"]?.GetValue<string>() ?? string.Join(' ', words.Select(w => w.Text)), words);
    }

    private static double Seconds(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<double>(out var s) ? s : double.TryParse(node?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
}

/// <summary>
/// The same three-call judge as <see cref="GeminiJudgeClient"/> — each player, then the ruling — through the text
/// seam. Chat models here take no audio, so the judge reads the diarized transcript and the measurements; the
/// shared prefix sits in the system message, where Azure's automatic prompt cache serves it to the second call.
/// </summary>
public sealed class AzureJudgeClient(IGeminiText text, GeminiModelOptions models) : IGeminiJudgeClient
{
    public const string Instructions = """
        You are the analyst for a two-person argument show.
        There is no recording, only the diarized transcript, the turn timeline and the measured metrics: read tone,
        pace, emotion and hesitation from those. Quote the players' actual words.
        Be exhaustive and honest. Scores are 1-10 unless stated. Never exceed the item limits on a list; pick the most
        consequential entries instead. Return only JSON matching the schema you were given.
        """;

    public async Task<JudgeOutputDto> JudgeAsync(JudgeRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var shared = Instructions + "\n" + GeminiJudgeClient.SessionData(request);

        // In order, not together: the second call is what finds the first one's prefix in the cache.
        var player1 = GeminiJudgeClient.Normalize(await AskAsync<PlayerAssessmentDto>(shared, GeminiJudgeClient.AssessmentTask(request, first: true), AnalysisSchema.PlayerAssessment(), GeminiJudgeClient.AssessOperation, ct));
        var player2 = GeminiJudgeClient.Normalize(await AskAsync<PlayerAssessmentDto>(shared, GeminiJudgeClient.AssessmentTask(request, first: false), AnalysisSchema.PlayerAssessment(), GeminiJudgeClient.AssessOperation, ct));
        var overall = GeminiJudgeClient.Normalize(await AskAsync<JudgeOverallDto>(shared, GeminiJudgeClient.RulingTask(request, player1, player2), AnalysisSchema.Overall(), GeminiJudgeClient.RuleOperation, ct));
        return new JudgeOutputDto(player1, player2, overall);
    }

    private async Task<T> AskAsync<T>(string system, string task, JsonObject schema, string operation, CancellationToken ct)
        where T : class
    {
        var answer = await text.GenerateAsync(
            new GeminiTextRequest(
                new GeminiPrompt(system, task),
                models.Judge,
                operation,
                schema,
                Temperature: 0.3,
                MaxOutputTokens: GeminiJudgeClient.MaxOutputTokens,
                ThinkingLevel: models.JudgeThinkingLevel,
                ServiceTier: string.IsNullOrWhiteSpace(models.JudgeServiceTier) ? null : models.JudgeServiceTier),
            ct);
        return GeminiJudgeClient.ParseText<T>(answer);
    }
}

/// <summary>
/// The fact check on the Responses API with its <c>web_search</c> tool. The answer's citations carry the same
/// <c>url_citation</c> spans Gemini's do, so the parsing is <see cref="GeminiClaimChecker.Apply"/>'s, fed the
/// message parts of the answer.
/// </summary>
public sealed class AzureClaimChecker(IHttpClientFactory factory, GeminiModelOptions models) : IClaimChecker
{
    public async Task<IReadOnlyList<ClaimDto>> CheckAsync(string topic, IReadOnlyList<ClaimDto> claims, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(claims);
        if (claims.Count == 0)
        {
            return claims;
        }

        var http = factory.CreateClient(GeminiHttpClients.Analysis);
        using var content = new StringContent(BuildRequest(models.Judge, topic, claims), System.Text.Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(new Uri("responses", UriKind.Relative), content, ct);
        await GeminiHttp.EnsureSuccessAsync(response, GeminiClaimChecker.Operation, ct);
        return GeminiClaimChecker.Apply(claims, MessageParts(GeminiHttp.TryParse(await response.Content.ReadAsStringAsync(ct))));
    }

    public static string BuildRequest(string model, string topic, IReadOnlyList<ClaimDto> claims)
    {
        var request = (JsonNode.Parse(GeminiClaimChecker.BuildRequest(model, topic, claims)) as JsonObject)!;
        request["tools"] = new JsonArray(new JsonObject { ["type"] = "web_search" });
        if (AzureOpenAi.IsReasoning(model))
        {
            request["reasoning"] = new JsonObject { ["effort"] = "low" };
        }

        return request.ToJsonString();
    }

    /// <summary>The text parts of every message in the answer, in the <c>outputs</c> shape the shared parser reads.</summary>
    public static JsonObject MessageParts(JsonObject? response) => new()
    {
        ["outputs"] = new JsonArray([.. ((response?["output"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .Where(o => o["type"]?.GetValue<string>() is "message")
            .SelectMany(o => (o["content"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .Select(c => c.DeepClone())]),
    };
}
