using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using PoFightJudge.Api.Features.Ai;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Api.Features.Analysis;

/// <summary>Checks the judge's claims against a web search, and says where it looked.</summary>
public interface IClaimChecker
{
    /// <summary>The same claims, in the same order, each re-read against the web; a claim it could not place comes back as it went in.</summary>
    Task<IReadOnlyList<ClaimDto>> CheckAsync(string topic, IReadOnlyList<ClaimDto> claims, CancellationToken ct);
}

/// <summary>
/// One grounded Interactions call per fight (<c>google_search</c>, verified against the published docs 2026-10-26).
/// The model answers one numbered line per claim, and each <c>url_citation</c> it attaches is hung on the line its
/// span falls in. Plain lines rather than a response schema: the citations index into the text, and that text is
/// what they are matched against.
/// </summary>
/// <remarks>Each search the model runs is billed (5,000 a month free across Gemini 3, then per thousand), so this runs once per ruled fight and never in WATCH.</remarks>
public sealed class GeminiClaimChecker(IHttpClientFactory factory, GeminiModelOptions models, TimeProvider clock) : IClaimChecker
{
    public const string Operation = "judge.factcheck";

    /// <summary>A claim with more links than this is a list, not a source.</summary>
    public const int MaxSources = 3;

    private static readonly string[] Verdicts = ["True", "False", "Unverifiable"];

    public async Task<IReadOnlyList<ClaimDto>> CheckAsync(string topic, IReadOnlyList<ClaimDto> claims, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(claims);
        if (claims.Count == 0)
        {
            return claims;
        }

        using var http = factory.CreateClient(GeminiHttpClients.Analysis);
        var backoff = new PollBackoff(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(2), clock);
        var node = await GeminiHttp.RunInteractionAsync(http, BuildRequest(models.Judge, topic, claims), Operation, backoff, ct);
        return Apply(claims, node);
    }

    public static string BuildRequest(string model, string topic, IReadOnlyList<ClaimDto> claims)
    {
        ArgumentNullException.ThrowIfNull(claims);
        var prompt = new StringBuilder()
            .Append("Two people argued about: ").AppendLine(topic)
            .AppendLine("Fact-check each numbered claim below with Google Search. Opinions and claims about the speakers themselves are Unverifiable.")
            .AppendLine("Answer with exactly one line per claim and nothing else, in this form:")
            .AppendLine("<number> | True|False|Unverifiable | <one short sentence why>")
            .AppendLine();
        for (var i = 0; i < claims.Count; i++)
        {
            prompt.Append(CultureInfo.InvariantCulture, $"{i + 1}. ").AppendLine(claims[i].Claim);
        }

        return new JsonObject
        {
            ["model"] = model,
            ["input"] = prompt.ToString(),
            ["tools"] = new JsonArray(new JsonObject { ["type"] = "google_search" }),
        }.ToJsonString();
    }

    /// <summary>Lays the answer over the claims. Anything unparseable leaves that claim exactly as the judge had it.</summary>
    public static IReadOnlyList<ClaimDto> Apply(IReadOnlyList<ClaimDto> claims, JsonNode? response)
    {
        ArgumentNullException.ThrowIfNull(claims);
        var result = claims.ToArray();
        foreach (var content in GeminiHttp.OutputContents(response))
        {
            var text = content["text"]?.GetValue<string>() ?? string.Empty;
            var citations = (content["annotations"] as JsonArray)?.OfType<JsonObject>()
                .Where(a => a["type"]?.GetValue<string>() is "url_citation")
                .ToList() ?? [];

            // ponytail: offsets are read as characters; the docs do not say bytes or chars, and the two only differ on non-ASCII text.
            var at = 0;
            foreach (var line in text.Split('\n'))
            {
                var (start, end) = (at, at + line.Length);
                at = end + 1;

                var parts = line.Split('|', 3, StringSplitOptions.TrimEntries);
                if (parts.Length < 3
                    || !int.TryParse(parts[0].TrimEnd('.'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                    || number < 1 || number > result.Length
                    || Array.Find(Verdicts, v => v.Equals(parts[1], StringComparison.OrdinalIgnoreCase)) is not { } verdict)
                {
                    continue;
                }

                var sources = citations
                    .Where(c => Index(c, "start_index") < end && Index(c, "end_index") > start)
                    .Select(c => (Url: c["url"]?.GetValue<string>(), Title: c["title"]?.GetValue<string>()))
                    .Where(c => Uri.TryCreate(c.Url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
                    .DistinctBy(c => c.Url, StringComparer.Ordinal)
                    .Take(MaxSources)
                    .Select(c => new SourceDto(string.IsNullOrWhiteSpace(c.Title) ? new Uri(c.Url!).Host : c.Title, c.Url!))
                    .ToList();

                result[number - 1] = result[number - 1] with { Verdict = verdict, Note = parts[2], Sources = sources };
            }
        }

        return result;
    }

    private static int Index(JsonObject citation, string key) =>
        citation[key] is JsonValue v && v.TryGetValue<int>(out var n) ? n : -1;
}
