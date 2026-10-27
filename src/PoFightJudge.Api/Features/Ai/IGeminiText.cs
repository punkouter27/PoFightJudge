using System.Text.Json.Nodes;

namespace PoFightJudge.Api.Features.Ai;

/// <summary>
/// A prompt split into the half that is constant for a whole match and the half that changes per turn.
/// <see cref="System"/> becomes <c>systemInstruction</c>; <see cref="User"/> the single user content part. The split
/// is what gives the provider a stable prefix to cache against — merging them back into one blob silently undoes it.
/// </summary>
public readonly record struct GeminiPrompt(string System, string User);

/// <summary>
/// One text-generation call. <paramref name="Operation"/> names the latency/usage bucket ("round", "judge",
/// "profile"…); a <paramref name="ResponseSchema"/> switches the model to structured JSON output. A
/// <paramref name="ServiceTier"/> ("flex") is for work nobody is waiting on: it is slower and cheaper.
/// </summary>
public sealed record GeminiTextRequest(
    GeminiPrompt Prompt,
    string Model,
    string Operation,
    JsonNode? ResponseSchema = null,
    double Temperature = 0.9,
    int MaxOutputTokens = 1024,
    string? ThinkingLevel = null,
    string? ServiceTier = null);

/// <summary>
/// The Gemini text transport: one call in, text out — JSON text when the request carries a schema. Prompt building
/// and parsing live with the feature that owns them (WATCH, profiles); the fake sits at this seam.
/// </summary>
public interface IGeminiText
{
    /// <summary>True for the deterministic fake, so callers and the banner can say so.</summary>
    bool IsFake { get; }

    Task<string> GenerateAsync(GeminiTextRequest request, CancellationToken ct = default);

    /// <summary>Raw text fragments in arrival order; concatenated they are exactly what <see cref="GenerateAsync"/> would return.</summary>
    IAsyncEnumerable<string> StreamAsync(GeminiTextRequest request, CancellationToken ct = default);
}
