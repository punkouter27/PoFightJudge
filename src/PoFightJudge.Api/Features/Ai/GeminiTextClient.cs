using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PoFightJudge.Api.Features.Diagnostics;

namespace PoFightJudge.Api.Features.Ai;

/// <summary>
/// The real <see cref="IGeminiText"/>: <c>generateContent</c> over the fast client, <c>streamGenerateContent?alt=sse</c>
/// over the stream client. Records total latency, time-to-first-token for streams and the token usage the API already
/// returns — without those numbers a prompt change cannot be shown to have helped.
/// </summary>
public sealed partial class GeminiTextClient(IHttpClientFactory httpClients, AiLatencyTracker latency, ILogger<GeminiTextClient> logger) : IGeminiText
{
    public bool IsFake => false;

    public async Task<string> GenerateAsync(GeminiTextRequest request, CancellationToken ct = default)
    {
        // A tiered call is one nobody is waiting on, and the discount tier routinely outlasts the fast client's
        // eight-second attempt; the analysis client is the patient one, for the standard-price retry too.
        var client = request.ServiceTier is null ? GeminiHttpClients.Fast : GeminiHttpClients.Analysis;
        try
        {
            return await SendAsync(request, client, ct);
        }
        catch (HttpRequestException ex) when (request.ServiceTier is not null && ex.StatusCode is HttpStatusCode.ServiceUnavailable)
        {
            // The discount tier is a price, not capacity: busy is its answer for as long as it is busy (the judge
            // learned this on 2026-09-07). Work that was worth doing is worth paying the standard price for.
            return await SendAsync(request with { ServiceTier = null }, client, ct);
        }
    }

    private async Task<string> SendAsync(GeminiTextRequest request, string clientName, CancellationToken ct)
    {
        using var message = Message(request, stream: false);
        var client = httpClients.CreateClient(clientName);

        var started = Stopwatch.GetTimestamp();
        using var response = await client.SendAsync(message, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        var elapsed = Stopwatch.GetElapsedTime(started);

        LogCompleted(logger, request.Model, request.Operation, elapsed.TotalMilliseconds, (int)response.StatusCode);
        latency.Record(request.Operation, elapsed.TotalMilliseconds);

        var body = GeminiHttp.TryParse(raw);

        if (!response.IsSuccessStatusCode)
        {
            var detail = body?["error"]?["message"]?.GetValue<string>() ?? GeminiHttp.Truncate(raw);
            LogFailed(logger, request.Model, request.Operation, (int)response.StatusCode, detail);
            throw new HttpRequestException($"Gemini {request.Operation} failed: {(int)response.StatusCode} {detail}", null, response.StatusCode);
        }

        RecordUsage(request.Operation, body?["usageMetadata"] as JsonObject);
        if (GeminiHttp.FinishReason(body) is { } reason and not "STOP")
        {
            // The text below is whatever arrived before it stopped. The callers cope with a partial answer; this is
            // what says, afterwards, that one happened and why.
            LogCutShort(logger, request.Model, request.Operation, reason);
        }

        return ExtractText(body) ?? throw new HttpRequestException($"Gemini {request.Operation} returned no text part.", null, response.StatusCode);
    }

    public async IAsyncEnumerable<string> StreamAsync(GeminiTextRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var message = Message(request, stream: true);
        // No resilience handler on this client on purpose — see GeminiHttpClients.Stream.
        var client = httpClients.CreateClient(GeminiHttpClients.Stream);

        var started = Stopwatch.GetTimestamp();
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            LogFailed(logger, request.Model, request.Operation, (int)response.StatusCode, GeminiHttp.Truncate(error));
            throw new HttpRequestException($"Gemini {request.Operation} stream failed: {(int)response.StatusCode} {GeminiHttp.Truncate(error)}", null, response.StatusCode);
        }

        var first = true;
        JsonObject? lastUsage = null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await foreach (var data in SseParser.ReadDataAsync(stream, ct))
        {
            JsonObject? chunk = null;
            try
            {
                chunk = JsonNode.Parse(data) as JsonObject;
            }
            catch (JsonException)
            {
                continue; // a malformed keep-alive chunk
            }

            // Usage arrives on the trailing chunks; keep the newest one seen.
            if (chunk?["usageMetadata"] is JsonObject usage)
            {
                lastUsage = usage;
            }

            var fragment = ExtractText(chunk);
            if (string.IsNullOrEmpty(fragment))
            {
                continue;
            }

            if (first)
            {
                first = false;
                // Time-to-first-token is what the viewer experiences on a streamed round; total wall time is not.
                latency.RecordTimeToFirstToken(request.Operation, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            yield return fragment;
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        LogCompleted(logger, request.Model, request.Operation, elapsed.TotalMilliseconds, (int)response.StatusCode);
        latency.Record(request.Operation, elapsed.TotalMilliseconds);
        RecordUsage(request.Operation, lastUsage);
    }

    /// <summary>The request body: invariant half in <c>systemInstruction</c>, per-turn half in <c>contents</c>, structured output when a schema is given.</summary>
    public static JsonObject BuildPayload(GeminiTextRequest request)
    {
        var generationConfig = new JsonObject
        {
            ["temperature"] = request.Temperature,
            ["maxOutputTokens"] = request.MaxOutputTokens,
        };
        if (request.ThinkingLevel is { Length: > 0 } level)
        {
            generationConfig["thinkingConfig"] = new JsonObject { ["thinkingLevel"] = level };
        }

        if (request.ResponseSchema is not null)
        {
            generationConfig["responseMimeType"] = "application/json";
            generationConfig["responseSchema"] = request.ResponseSchema.DeepClone();
        }

        var payload = new JsonObject
        {
            ["contents"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["parts"] = new JsonArray { new JsonObject { ["text"] = request.Prompt.User } },
                },
            },
            ["generationConfig"] = generationConfig,
        };
        if (request.ServiceTier is { Length: > 0 } tier)
        {
            payload["service_tier"] = tier;
        }

        if (!string.IsNullOrEmpty(request.Prompt.System))
        {
            payload["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray { new JsonObject { ["text"] = request.Prompt.System } },
            };
        }

        return payload;
    }

    /// <summary>Relative to the client base address; the key rides in the header the named client sets, never the query string.</summary>
    public static Uri Endpoint(string model, bool stream) =>
        new($"v1beta/models/{Uri.EscapeDataString(model)}:{(stream ? "streamGenerateContent?alt=sse" : "generateContent")}", UriKind.Relative);

    /// <summary>The first non-thought text part: thinking models may emit <c>thought:true</c> parts before the answer.</summary>
    public static string? ExtractText(JsonObject? response)
    {
        if (response?["candidates"]?[0]?["content"]?["parts"] is not JsonArray parts)
        {
            return null;
        }

        foreach (var part in parts.OfType<JsonObject>())
        {
            var isThought = part["thought"]?.GetValue<bool>() ?? false;
            if (!isThought && part["text"]?.GetValue<string>() is { } text)
            {
                return text;
            }
        }

        return null;
    }

    private static HttpRequestMessage Message(GeminiTextRequest request, bool stream) =>
        new(HttpMethod.Post, Endpoint(request.Model, stream))
        {
            Content = new StringContent(BuildPayload(request).ToJsonString(JsonSerializerOptions.Web), Encoding.UTF8, "application/json"),
        };

    private void RecordUsage(string operation, JsonObject? usage)
    {
        if (usage is null)
        {
            return;
        }

        var read = GeminiUsage.Read(usage);
        latency.RecordUsage(operation, read.PromptTokens, read.OutputTokens, read.CachedTokens);
    }

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "Gemini {Model} {Operation} completed in {ElapsedMs:F0}ms with {Status}")]
    private static partial void LogCompleted(ILogger logger, string model, string operation, double elapsedMs, int status);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Warning, Message = "Gemini {Model} {Operation} stopped early: {Reason}")]
    private static partial void LogCutShort(ILogger logger, string model, string operation, string reason);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Error, Message = "Gemini {Model} {Operation} failed with {Status}: {Detail}")]
    private static partial void LogFailed(ILogger logger, string model, string operation, int status, string detail);
}

/// <summary>
/// Minimal server-sent-events reader: yields each event's joined <c>data</c> payload. Comments and other fields are
/// skipped, CRLF is tolerated, and <c>[DONE]</c> ends the stream. Gemini sends one JSON object per event.
/// </summary>
public static class SseParser
{
    public const string Done = "[DONE]";

    public static async IAsyncEnumerable<string> ReadDataAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        var hasData = false;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
            {
                if (hasData)
                {
                    var payload = data.ToString();
                    data.Clear();
                    hasData = false;
                    if (string.Equals(payload, Done, StringComparison.Ordinal))
                    {
                        yield break;
                    }

                    yield return payload;
                }

                continue;
            }

            if (line[0] == ':' || !line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var value = line.AsSpan(5);
            if (value.Length > 0 && value[0] == ' ')
            {
                value = value[1..];
            }

            if (hasData)
            {
                data.Append('\n');
            }

            data.Append(value);
            hasData = true;
        }

        if (hasData)
        {
            var trailing = data.ToString();
            if (!string.Equals(trailing, Done, StringComparison.Ordinal))
            {
                yield return trailing;
            }
        }
    }
}
