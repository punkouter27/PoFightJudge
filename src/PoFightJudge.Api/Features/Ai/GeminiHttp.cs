using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PoFightJudge.Api.Features.Analysis;

namespace PoFightJudge.Api.Features.Ai;

/// <summary>
/// The named <see cref="HttpClient"/>s the Gemini REST clients talk through — one per latency class, because one
/// shared client means one shared timeout, and a dialogue line that should answer in a second or two must not share
/// its budget with a voice call that legitimately runs for most of a minute.
/// </summary>
public static class GeminiHttpClients
{
    /// <summary>Short interactive text calls: a round line, the WATCH judge, a generated profile.</summary>
    public const string Fast = "gemini-fast";

    /// <summary>Speech synthesis — legitimately slow, so it gets its own budget.</summary>
    public const string Tts = "gemini-tts";

    /// <summary>
    /// Streaming rounds. Deliberately no resilience handler: a per-attempt timeout disposes its cancellation source
    /// once <c>SendAsync</c> returns at the response headers, which cuts the body stream out from under the reader,
    /// and a retry cannot un-send tokens the client has already rendered. The client timeout is the whole guard.
    /// </summary>
    public const string Stream = "gemini-stream";

    /// <summary>The FIGHT analysis surface (Files API, transcription, the flex-tier judge): long, retried by <see cref="GeminiRetryHandler"/>.</summary>
    public const string Analysis = "gemini-analysis";

    public static IReadOnlyList<string> All { get; } = [Fast, Tts, Stream, Analysis];
}

/// <summary>Gemini REST plumbing shared by every client: endpoint, key header, error handling.</summary>
public static class GeminiHttp
{
    public const string ApiKeyHeader = "x-goog-api-key";

    public static Uri RestBase { get; } = new("https://generativelanguage.googleapis.com/");

    /// <summary>Turns a non-success answer into an <see cref="HttpRequestException"/> that names the operation and quotes the (truncated) body.</summary>
    public static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException($"Gemini {operation} failed: {(int)response.StatusCode} {Truncate(body)}", null, response.StatusCode);
    }

    public static string Truncate(string s, int max = 400) => s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "…");

    /// <summary>
    /// Why the model stopped: <c>STOP</c> when it finished, <c>MAX_TOKENS</c> when it was cut off, <c>SAFETY</c> and
    /// the like when it declined. Null when the answer does not say.
    /// </summary>
    public static string? FinishReason(JsonObject? response) =>
        (response?["candidates"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault()?["finishReason"]?.GetValue<string>();

    /// <summary>A body that has already been read, as JSON. Null for anything else — an error page, say.</summary>
    public static JsonObject? TryParse(string raw)
    {
        try
        {
            return JsonNode.Parse(raw) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Creates an interaction and polls it by id until it is done, quickly at first. The backoff's budget is the
    /// deadline for the whole loop: each poll is a request of its own, so the client's timeout does not cover it.
    /// A failed interaction throws; one still running when the budget is spent comes back as it stands.
    /// </summary>
    public static async Task<JsonObject?> RunInteractionAsync(HttpClient http, string body, string operation, PollBackoff backoff, CancellationToken ct)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(new Uri("v1beta/interactions", UriKind.Relative), content, ct);
        await EnsureSuccessAsync(response, $"{operation} interactions.create", ct);
        var node = TryParse(await response.Content.ReadAsStringAsync(ct));

        while (node?["status"]?.GetValue<string>() is not (null or "completed" or "failed")
            && node["id"]?.GetValue<string>() is { } id
            && await backoff.WaitAsync(ct))
        {
            using var poll = await http.GetAsync(new Uri($"v1beta/{id}", UriKind.Relative), ct);
            await EnsureSuccessAsync(poll, $"{operation} interactions.get", ct);
            node = TryParse(await poll.Content.ReadAsStringAsync(ct));
        }

        return string.Equals(node?["status"]?.GetValue<string>(), "failed", StringComparison.Ordinal)
            ? throw new HttpRequestException($"Gemini {operation} failed: " + Truncate(node?["error"]?.ToJsonString() ?? "unknown"))
            : node;
    }

    /// <summary>The content blocks a finished interaction answered with, from whichever shape it came back in (<c>steps</c> or the older <c>outputs</c>).</summary>
    public static IEnumerable<JsonObject> OutputContents(JsonNode? root) =>
        (root?["steps"] as JsonArray)?.OfType<JsonObject>()
            .Where(s => s["type"]?.GetValue<string>() is null or "model_output")
            .SelectMany(s => (s["content"] as JsonArray)?.OfType<JsonObject>() ?? [])
        ?? (root?["outputs"] as JsonArray)?.OfType<JsonObject>()
        ?? [];

    /// <summary>Strips code fences and isolates the outermost object, so a fenced or chatty answer still parses.</summary>
    public static string IsolateJsonObject(string raw)
    {
        var cleaned = raw.Trim();
        if (cleaned.StartsWith("```", StringComparison.Ordinal))
        {
            var newline = cleaned.IndexOf('\n', StringComparison.Ordinal);
            if (newline >= 0)
            {
                cleaned = cleaned[(newline + 1)..];
            }

            var fence = cleaned.LastIndexOf("```", StringComparison.Ordinal);
            if (fence >= 0)
            {
                cleaned = cleaned[..fence];
            }
        }

        var start = cleaned.IndexOf('{', StringComparison.Ordinal);
        var end = cleaned.LastIndexOf('}');
        return start >= 0 && end > start ? cleaned[start..(end + 1)] : cleaned;
    }
}

/// <summary>
/// The token counts a <c>generateContent</c> answer reports, as the ledger wants them. Shared rather than private
/// to one client: the judge sends by far the largest prompt in the app and used to report none of this, because it
/// builds its own payloads and never went past <see cref="GeminiTextClient"/>.
/// </summary>
/// <param name="PromptTokens">Everything sent, cached or not.</param>
/// <param name="OutputTokens">
/// What came back, with thinking folded in. Thinking tokens bill as output and are invisible in the text, so
/// leaving them out is what would hide the cost of a raised <c>thinkingLevel</c>.
/// </param>
/// <param name="CachedTokens">
/// The slice of the prompt served from the provider's context cache — the number that says whether cache-friendly
/// prompt ordering, and the judge's explicit cache, are actually paying.
/// </param>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Auto)]
public readonly record struct GeminiUsage(int PromptTokens, int OutputTokens, int CachedTokens)
{
    /// <summary>Reads what is there. Anything missing, or not a number, counts as nothing rather than as a failure.</summary>
    public static GeminiUsage Read(JsonObject? usage)
    {
        if (usage is null)
        {
            return default;
        }

        return new GeminiUsage(
            Count(usage, "promptTokenCount"),
            Count(usage, "candidatesTokenCount") + Count(usage, "thoughtsTokenCount"),
            Count(usage, "cachedContentTokenCount"));

        static int Count(JsonObject usage, string key) =>
            usage[key] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;
    }
}
