using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using PoFightJudge.Api.Features.Auth;
using PoFightJudge.Shared;
using PoFightJudge.Shared.Configuration;
using PoFightJudge.Shared.Diagnostics;
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
    public async Task Admin_diag_requires_authentication_and_neither_diag_report_leaks_a_value()
    {
        // Anonymous → 401
        {
            using var client = factory.CreateClient();
            (await client.GetAsync(ApiRoutes.Diag.AdminUrl)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        // Signed in (Test environment, no admin requirement) → presence only
        {
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add(FakeAuthOptions.UserHeader, "diag-user");
            var response = await client.GetAsync(ApiRoutes.Diag.AdminUrl);
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

        // Anonymous /api/diag (the Po web standard's DiagReportDto), which the /diag page reads: plain words, never a value.
        {
            // Endpoints a leak would echo: the storage hosts, plus the Azure OpenAI host appsettings.json already carries.
            using var host = factory.WithWebHostBuilder(b => b
                .UseSetting(ConfigKeys.Storage.TableEndpoint, "https://leaksentinel.table.core.windows.net/")
                .UseSetting(ConfigKeys.Storage.BlobEndpoint, "https://leaksentinel.blob.core.windows.net/"));
            using var client = host.CreateClient();

            var response = await client.GetAsync(ApiRoutes.Diag.Url);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await response.Content.ReadAsStringAsync();

            foreach (var forbidden in new[] { "test-key", "leaksentinel", "core.windows.net", "vault.azure.net", "cognitiveservices", "AccountKey", "Exception", "   at ", ".ps1", ":\\" })
            {
                body.Should().NotContainEquivalentOf(forbidden, "the /diag page is anonymous");
            }

            SecretLikePattern().IsMatch(body).Should().BeFalse();
            GuidPattern().IsMatch(body).Should().BeFalse("no tenant, subscription or client id");
            EmailPattern().IsMatch(body).Should().BeFalse();

            var report = await response.Content.ReadFromJsonAsync<DiagReportDto>();
            report!.Environment.Should().Be(ApiFactory.Environment);
            report.Version.Should().MatchRegex(@"^\d{6}$", "the BuildVersion stamp");
            report.Status.Should().BeOneOf("Healthy", "Degraded", "Unhealthy");
            report.Checks[0].Should().Match<DiagCheckDto>(c => c.Name == "api" && c.Group == "api" && c.Status == "Healthy");
            report.Checks.Select(c => c.Group).Should().OnlyContain(g => g == "api" || g == "data" || g == "service");
            report.Checks.Select(c => c.Status).Should().OnlyContain(s => s == "Healthy" || s == "Degraded" || s == "Unhealthy" || s == "Off");
            // Services this host leaves unconfigured on purpose are "Off" (neutral), never a fake "Healthy".
            report.Checks.Should().Contain(c => c.Name == "key-vault" && c.Status == "Off");
            report.Checks.Should().Contain(c => c.Name == "ai-provider" && c.Status == "Off");
            report.Status.Should().NotBe("Unhealthy", "an Off row is not a problem");
            report.Checks.Should().Contain(c => c.Group == "data" && c.Name == "storage");
            report.Checks.Should().Contain(c => c.Group == "service" && c.Label == "AI provider" && c.Purpose.Length > 0);

            // Cached (DiagReportCache): a second read inside the lifetime is the same report, not a second set of probes.
            var again = await client.GetFromJsonAsync<DiagReportDto>(ApiRoutes.Diag.Url);
            again!.CheckedAt.Should().Be(report.CheckedAt, "the probes run at most once per DiagReportCache.Lifetime");
        }
    }

    [Fact]
    public async Task Every_response_carries_the_security_envelope_including_a_404()
    {
        using var client = factory.CreateClient();

        var ok = await client.GetAsync(ApiRoutes.Health.Url);
        ok.Headers.GetValues("Content-Security-Policy").Single().Should().Contain("frame-ancestors 'none'");
        ok.Headers.GetValues("X-Content-Type-Options").Single().Should().Be("nosniff");
        ok.Headers.GetValues("X-Commit").Single().Should().MatchRegex("^[0-9a-f]{40}$", "the deploy matches it against the commit it shipped");
        ok.Headers.GetValues("Permissions-Policy").Single().Should().Contain("microphone=(self)");

        var missing = await client.GetAsync($"{ApiRoutes.ApiPrefix}/does-not-exist");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        missing.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        missing.Headers.Contains("Content-Security-Policy").Should().BeTrue();

        // A missing static file is a 404, not an auth challenge (the SPA fallback for client routes is covered by E2EUI,
        // where the static web assets exist).
        (await client.GetAsync("/js/does-not-exist.js")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"[\w.+-]+@[\w-]+\.[\w.]+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EmailPattern();

    [GeneratedRegex("AIza[0-9A-Za-z_-]{20,}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SecretLikePattern();
}
