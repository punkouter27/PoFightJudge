using System.Text.Json.Nodes;
using PoFightJudge.Api.Features.Analysis;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Unit.Analysis;

/// <summary>
/// The fact check: the judge's claims go to a model that can search, one numbered line comes back per claim, and the
/// citations the answer carries are hung on the line they fall inside. A claim the answer skipped keeps the judge's
/// own reading, and a link that is not plain http(s) never reaches a page.
/// </summary>
public class GeminiClaimCheckerTests
{
    private static readonly ClaimDto[] Judged =
    [
        new("The Eiffel Tower is in Berlin", "Unverifiable", "judge was unsure"),
        new("Water boils at 100C at sea level", "True", "judge agrees"),
        new("My mum loves me more", "Unverifiable", "personal"),
    ];

    [Fact]
    public void Each_claim_is_searched_and_the_citations_inside_a_line_become_that_claims_sources()
    {
        var request = JsonNode.Parse(GeminiClaimChecker.BuildRequest("gemini-3.7-flash", "the thermostat", Judged))!;

        request["model"]!.GetValue<string>().Should().Be("gemini-3.7-flash");
        request["tools"]!.AsArray().Should().ContainSingle().Which!["type"]!.GetValue<string>().Should().Be("google_search");
        request["input"]!.GetValue<string>().Should().Contain("1. The Eiffel Tower is in Berlin").And.Contain("3. My mum loves me more");

        const string Line1 = "1 | False | It is in Paris.";
        const string Text = Line1 + "\n2 | True | Standard boiling point.";
        var response = new JsonObject
        {
            ["status"] = "completed",
            ["steps"] = new JsonArray(new JsonObject
            {
                ["type"] = "model_output",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = Text,
                    ["annotations"] = new JsonArray(
                        Citation("https://example.org/eiffel", "Eiffel", 0, Line1.Length),
                        Citation("javascript:alert(1)", "bad", 0, 5),
                        Citation("https://example.org/boil", "Boiling", Line1.Length + 1, Text.Length),
                        Citation("https://example.org/boil", "Boiling again", Line1.Length + 3, Text.Length)),
                }),
            }),
        };

        var checkedClaims = GeminiClaimChecker.Apply(Judged, response);

        checkedClaims[0].Should().BeEquivalentTo(Judged[0] with { Verdict = "False", Note = "It is in Paris.", Sources = [new SourceDto("Eiffel", "https://example.org/eiffel")] },
            "the searched verdict replaces the judge's, and only an http(s) link is kept");
        checkedClaims[1].Sources.Should().ContainSingle("the same page cited twice is one source").Which.Url.Should().Be("https://example.org/boil");
        checkedClaims[2].Should().BeEquivalentTo(Judged[2], "a claim the answer skipped keeps the judge's reading");

        // Azure: the same prompt with its own search tool, and the Responses API's message parts read the same way.
        var azure = JsonNode.Parse(AzureClaimChecker.BuildRequest("gpt-5-mini", "the thermostat", Judged))!;
        azure["tools"]!.AsArray().Should().ContainSingle().Which!["type"]!.GetValue<string>().Should().Be("web_search");
        azure["reasoning"]!["effort"]!.GetValue<string>().Should().Be("low");
        var answered = new JsonObject
        {
            ["output"] = new JsonArray(
                new JsonObject { ["type"] = "web_search_call" },
                new JsonObject
                {
                    ["type"] = "message",
                    ["content"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "output_text",
                        ["text"] = Line1,
                        ["annotations"] = new JsonArray(Citation("https://example.org/eiffel", "Eiffel", 0, Line1.Length)),
                    }),
                }),
        };
        GeminiClaimChecker.Apply(Judged, AzureClaimChecker.MessageParts(answered))[0].Sources.Should().ContainSingle()
            .Which.Url.Should().Be("https://example.org/eiffel");
    }

    private static JsonObject Citation(string url, string title, int start, int end) => new()
    {
        ["type"] = "url_citation",
        ["url"] = url,
        ["title"] = title,
        ["start_index"] = start,
        ["end_index"] = end,
    };
}
