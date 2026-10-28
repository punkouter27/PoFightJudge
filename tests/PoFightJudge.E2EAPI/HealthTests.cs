using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using PoFightJudge.Api.Features.Auth;
using PoFightJudge.Shared;
using PoFightJudge.Shared.Configuration;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.E2EAPI;

[Collection(ApiCollection.Name)]
public partial class HealthTests(ApiFactory factory)
{
    [Fact]
    public async Task Health_probes_answer_200_without_touching_the_network()
    {
        using var client = factory.CreateClient();

        (await client.GetAsync(ApiRoutes.Health.Url)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Diag_requires_authentication_and_never_leaks_a_value()
    {
        // Anonymous → 401
        {
            using var client = factory.CreateClient();
            (await client.GetAsync(ApiRoutes.Diag.Url)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        // Signed in (Test environment, no admin requirement) → presence only
        {
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add(FakeAuthOptions.UserHeader, "diag-user");
            var response = await client.GetAsync(ApiRoutes.Diag.Url);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var body = await response.Content.ReadAsStringAsync();
            body.Should().NotContain("test-key").And.NotContain("AccountKey=");
            SecretLikePattern().IsMatch(body).Should().BeFalse("diag must never include key-like strings");

            var dto = await response.Content.ReadFromJsonAsync<DiagDto>();
            dto!.Status.Should().Be("ok");
            dto.Gemini.Should().Be(DiagDto.Configured);
            dto.FakeAi.Should().BeTrue("the test host keeps a placeholder key but forces the fakes with the flag");
            dto.Ready.Should().BeTrue();
            dto.Models.Live.Should().Be("gpt-realtime-mini");
            dto.Models.Round.Should().Be("gpt-4.1-mini");
            dto.Models.Tts.Should().Be("gpt-4o-mini-tts");
            dto.Flags.Keys.Should().BeEquivalentTo(Toggles.All);
        }
    }

    [Fact]
    public async Task Every_response_carries_the_security_envelope_including_a_404()
    {
        using var client = factory.CreateClient();

        var ok = await client.GetAsync(ApiRoutes.Health.Url);
        ok.Headers.GetValues("Content-Security-Policy").Single().Should().Contain("frame-ancestors 'none'");
        ok.Headers.GetValues("X-Content-Type-Options").Single().Should().Be("nosniff");
        ok.Headers.GetValues("Permissions-Policy").Single().Should().Contain("microphone=(self)");

        var missing = await client.GetAsync($"{ApiRoutes.ApiPrefix}/does-not-exist");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        missing.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        missing.Headers.Contains("Content-Security-Policy").Should().BeTrue();

        // A missing static file is a 404, not an auth challenge (the SPA fallback for client routes is covered by E2EUI,
        // where the static web assets exist).
        (await client.GetAsync("/js/does-not-exist.js")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [GeneratedRegex("AIza[0-9A-Za-z_-]{20,}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SecretLikePattern();
}
