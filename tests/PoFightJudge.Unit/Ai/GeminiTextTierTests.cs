using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PoFightJudge.Api.Features.Ai;
using PoFightJudge.Api.Features.Diagnostics;
using PoFightJudge.TestSupport;

namespace PoFightJudge.Unit.Ai;

/// <summary>
/// Work nobody is waiting on (a persona rewritten after a fight) asks for the discount tier. That tier is slow and
/// refuses when busy, so it goes over the patient client and, refused, is sent again at the standard price.
/// </summary>
public sealed class GeminiTextTierTests
{
    private const string Answer = """{"candidates":[{"content":{"parts":[{"text":"ok"}]},"finishReason":"STOP"}]}""";

    [Fact]
    public async Task A_tiered_call_rides_the_patient_client_and_a_busy_discount_tier_is_retried_at_the_standard_one()
    {
        using var fast = new FakeHttpHandler();
        fast.Enqueue(HttpStatusCode.OK, Answer);
        using var patient = new FakeHttpHandler();
        patient.Enqueue(HttpStatusCode.ServiceUnavailable, """{"error":{"message":"high demand"}}""").Enqueue(HttpStatusCode.OK, Answer);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(GeminiHttpClients.Fast).Returns(_ => fast.CreateClient());
        factory.CreateClient(GeminiHttpClients.Analysis).Returns(_ => patient.CreateClient());
        var sut = new GeminiTextClient(factory, new AiLatencyTracker(), NullLogger<GeminiTextClient>.Instance);

        var text = await sut.GenerateAsync(new GeminiTextRequest(new("sys", "user"), "m", "profile", ServiceTier: "flex"));

        text.Should().Be("ok");
        fast.Requests.Should().BeEmpty("the fast client gives up after eight seconds, which the discount tier routinely takes");
        patient.Requests.Should().HaveCount(2);
        patient.Requests[0].Body.Should().Contain("\"service_tier\":\"flex\"");
        patient.Requests[1].Body.Should().NotContain("service_tier", "busy is the tier's answer for as long as it is busy");

        await sut.GenerateAsync(new GeminiTextRequest(new("sys", "user"), "m", "round"));
        fast.Requests.Should().ContainSingle("an untiered call is a live one and stays on the fast client")
            .Which.Body.Should().NotContain("service_tier");
    }
}
