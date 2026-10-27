using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using PoFightJudge.Api.Features.Diagnostics;

namespace PoFightJudge.Api.Features.Ai;

/// <summary>
/// Azure OpenAI (the AI Foundry resource in PoShared) as the AI provider: the OpenAI v1 surface under
/// <c>{endpoint}/openai/v1/</c>, signed in with the app's identity rather than a key. Model ids are deployment names.
/// </summary>
public static class AzureOpenAi
{
    public const string Scope = "https://cognitiveservices.azure.com/.default";

    public static Uri RestBase(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return new Uri(endpoint, "openai/v1/");
    }

    /// <summary>
    /// The gpt-5 and o-series deployments reason before they answer: they refuse a temperature, and their token
    /// ceiling has to leave room for the thinking as well as the answer.
    /// </summary>
    /// <remarks>ponytail: decided from the deployment name, which is only as honest as whoever named it.</remarks>
    public static bool IsReasoning(string deployment) =>
        deployment.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase)
        || (deployment.Length > 1 && deployment[0] is 'o' or 'O' && char.IsDigit(deployment[1]));

    /// <summary>
    /// A Gemini <c>responseSchema</c> as JSON Schema: the same shape, with the upper-case type names lowered. The
    /// schemas here were written for Gemini and still serve it, so they are translated on the way out.
    /// </summary>
    public static JsonNode? ToJsonSchema(JsonNode? schema)
    {
        var copy = schema?.DeepClone();
        Lower(copy);
        return copy;

        static void Lower(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    if (obj["type"] is JsonValue type && type.TryGetValue<string>(out var name))
                    {
                        obj["type"] = name.ToLowerInvariant();
                    }

                    foreach (var (_, child) in obj.ToList())
                    {
                        Lower(child);
                    }

                    break;
                case JsonArray array:
                    foreach (var child in array)
                    {
                        Lower(child);
                    }

                    break;
                default:
                    break;
            }
        }
    }
}

/// <summary>Puts the app identity's bearer token on every request to the Azure OpenAI resource.</summary>
public sealed class AzureTokenHandler(TokenCredential credential) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // The credential caches the token and refreshes it ahead of expiry, so asking every time costs nothing.
        var token = await credential.GetTokenAsync(new TokenRequestContext([AzureOpenAi.Scope]), cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return await base.SendAsync(request, cancellationToken);
    }
}

/// <summary>
/// The Azure <see cref="IGeminiText"/>: chat completions, streamed or not, recording the same latency and usage
/// numbers the Gemini client does. The system half of the prompt goes first, so Azure's automatic prefix cache
/// gets the same stable prefix Gemini's did.
/// </summary>
public sealed partial class AzureOpenAiText(IHttpClientFactory httpClients, AiLatencyTracker latency, ILogger<AzureOpenAiText> logger) : IGeminiText
{
    private static readonly Uri Completions = new("chat/completions", UriKind.Relative);

    public bool IsFake => false;

    public async Task<string> GenerateAsync(GeminiTextRequest request, CancellationToken ct = default)
    {
        // There is no discount tier here; a request that asked for one is simply work nobody waits on, so it gets
        // the patient client.
        var client = httpClients.CreateClient(request.ServiceTier is null ? GeminiHttpClients.Fast : GeminiHttpClients.Analysis);
        using var content = Content(request, stream: false);

        var started = Stopwatch.GetTimestamp();
        using var response = await client.PostAsync(Completions, content, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        var elapsed = Stopwatch.GetElapsedTime(started);

        LogCompleted(logger, request.Model, request.Operation, elapsed.TotalMilliseconds, (int)response.StatusCode);
        latency.Record(request.Operation, elapsed.TotalMilliseconds);

        var body = GeminiHttp.TryParse(raw);
        if (!response.IsSuccessStatusCode)
        {
            var detail = body?["error"]?["message"]?.GetValue<string>() ?? GeminiHttp.Truncate(raw);
            LogFailed(logger, request.Model, request.Operation, (int)response.StatusCode, detail);
            throw new HttpRequestException($"Azure OpenAI {request.Operation} failed: {(int)response.StatusCode} {detail}", null, response.StatusCode);
        }

        RecordUsage(request.Operation, body?["usage"] as JsonObject);
        var choice = (body?["choices"] as JsonArray)?.FirstOrDefault();
        if (choice?["finish_reason"]?.GetValue<string>() is { } reason and not "stop")
        {
            LogCutShort(logger, request.Model, request.Operation, reason);
        }

        return choice?["message"]?["content"]?.GetValue<string>() is { Length: > 0 } text
            ? text
            : throw new HttpRequestException($"Azure OpenAI {request.Operation} returned no text.", null, response.StatusCode);
    }

    public async IAsyncEnumerable<string> StreamAsync(GeminiTextRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, Completions) { Content = Content(request, stream: true) };
        var client = httpClients.CreateClient(GeminiHttpClients.Stream);

        var started = Stopwatch.GetTimestamp();
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = GeminiHttp.Truncate(await response.Content.ReadAsStringAsync(ct));
            LogFailed(logger, request.Model, request.Operation, (int)response.StatusCode, error);
            throw new HttpRequestException($"Azure OpenAI {request.Operation} stream failed: {(int)response.StatusCode} {error}", null, response.StatusCode);
        }

        var first = true;
        JsonObject? usage = null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await foreach (var data in SseParser.ReadDataAsync(stream, ct))
        {
            var chunk = GeminiHttp.TryParse(data);
            if (chunk?["usage"] is JsonObject reported)
            {
                usage = reported;
            }

            if (Delta(chunk) is not { Length: > 0 } fragment)
            {
                continue;
            }

            if (first)
            {
                first = false;
                latency.RecordTimeToFirstToken(request.Operation, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            yield return fragment;
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        LogCompleted(logger, request.Model, request.Operation, elapsed.TotalMilliseconds, (int)response.StatusCode);
        latency.Record(request.Operation, elapsed.TotalMilliseconds);
        RecordUsage(request.Operation, usage);
    }

    public static JsonObject BuildPayload(GeminiTextRequest request, bool stream)
    {
        ArgumentNullException.ThrowIfNull(request);
        var messages = new JsonArray();
        if (!string.IsNullOrEmpty(request.Prompt.System))
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = request.Prompt.System });
        }

        messages.Add(new JsonObject { ["role"] = "user", ["content"] = request.Prompt.User });

        var payload = new JsonObject { ["model"] = request.Model, ["messages"] = messages };
        if (AzureOpenAi.IsReasoning(request.Model))
        {
            // Gemini's thinking levels and these effort names line up; no level asked for means as little as there is.
            payload["reasoning_effort"] = request.ThinkingLevel is { Length: > 0 } level ? level : "minimal";
            payload["max_completion_tokens"] = request.MaxOutputTokens + ReasoningHeadroom;
        }
        else
        {
            payload["temperature"] = request.Temperature;
            payload["max_completion_tokens"] = request.MaxOutputTokens;
        }

        if (request.ResponseSchema is not null)
        {
            payload["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = request.Operation.Replace('.', '_'),

                    // Strict mode would demand additionalProperties:false everywhere and refuses minItems/maxItems.
                    ["strict"] = false,
                    ["schema"] = AzureOpenAi.ToJsonSchema(request.ResponseSchema),
                },
            };
        }

        if (stream)
        {
            payload["stream"] = true;
            payload["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

        return payload;
    }

    /// <summary>
    /// The text one streamed chunk adds, or null. The last chunk carries only the usage, with an empty
    /// <c>choices</c> — and indexing an empty <see cref="JsonArray"/> throws rather than answering null.
    /// </summary>
    public static string? Delta(JsonObject? chunk) =>
        (chunk?["choices"] as JsonArray)?.FirstOrDefault()?["delta"]?["content"] is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;

    /// <summary>The tokens a reasoning model may think with on top of the answer it was asked for.</summary>
    private const int ReasoningHeadroom = 4096;

    private static StringContent Content(GeminiTextRequest request, bool stream) =>
        new(BuildPayload(request, stream).ToJsonString(JsonSerializerOptions.Web), Encoding.UTF8, "application/json");

    private void RecordUsage(string operation, JsonObject? usage)
    {
        if (usage is null)
        {
            return;
        }

        latency.RecordUsage(
            operation,
            Count(usage["prompt_tokens"]),
            Count(usage["completion_tokens"]),
            Count(usage["prompt_tokens_details"]?["cached_tokens"]));

        static int Count(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;
    }

    [LoggerMessage(EventId = 1011, Level = LogLevel.Information, Message = "Azure OpenAI {Model} {Operation} completed in {ElapsedMs:F0}ms with {Status}")]
    private static partial void LogCompleted(ILogger logger, string model, string operation, double elapsedMs, int status);

    [LoggerMessage(EventId = 1013, Level = LogLevel.Warning, Message = "Azure OpenAI {Model} {Operation} stopped early: {Reason}")]
    private static partial void LogCutShort(ILogger logger, string model, string operation, string reason);

    [LoggerMessage(EventId = 1012, Level = LogLevel.Error, Message = "Azure OpenAI {Model} {Operation} failed with {Status}: {Detail}")]
    private static partial void LogFailed(ILogger logger, string model, string operation, int status, string detail);
}
