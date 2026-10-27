using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using PoFightJudge.Api.Features.Diagnostics;
using PoFightJudge.Api.Features.Watch;
using PoFightJudge.Shared.Configuration;

namespace PoFightJudge.Unit.Diagnostics;

/// <summary>A daily token allowance per caller, so one runaway guest cannot spend the month's Gemini bill.</summary>
public class AiBudgetTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 19, 22, 0, 0, TimeSpan.Zero));

    private static async Task SpendAsync(AiLatencyTracker tracker, string? caller, int tokens)
    {
        // A flow of its own, the way a request or an analysis job is: the caller set here goes no further.
        await Task.Run(() =>
        {
            AiLatencyTracker.Caller = caller;
            tracker.RecordUsage("round", promptTokens: tokens / 2, outputTokens: tokens - (tokens / 2), cachedTokens: 0);
        });
    }

    private static DefaultHttpContext Request(string? user, bool aiRoute)
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        if (user is not null)
        {
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user)], "test"));
        }

        var metadata = aiRoute ? new EndpointMetadataCollection(new EnableRateLimitingAttribute(WatchServiceExtensions.AiPolicy)) : EndpointMetadataCollection.Empty;
        ctx.SetEndpoint(new Endpoint(_ => Task.CompletedTask, metadata, "test"));
        return ctx;
    }

    private static AiBudgetGate Gate(AiLatencyTracker tracker, long limit, Action? next = null) => new(
        _ =>
        {
            next?.Invoke();
            return Task.CompletedTask;
        },
        tracker,
        new ConfigurationBuilder().AddInMemoryCollection([new(ConfigKeys.Ai.DailyTokenBudget, limit.ToString(System.Globalization.CultureInfo.InvariantCulture))]).Build());

    [Fact]
    public async Task Tokens_count_against_whoever_the_work_was_for_until_midnight_utc_and_over_the_allowance_an_ai_route_is_refused()
    {
        var tracker = new AiLatencyTracker(_clock);

        await SpendAsync(tracker, "alice", 300);
        await SpendAsync(tracker, "alice", 200);
        await SpendAsync(tracker, null, 1_000);

        tracker.TokensToday("alice").Should().Be(500);
        tracker.TokensToday("bob").Should().Be(0);
        tracker.UsageSnapshot().Single().Calls.Should().Be(3, "the per-operation ledger still counts every call");

        // The gate names the caller for the work the request does.
        await Gate(tracker, limit: 0, () => tracker.RecordUsage("round", 40, 2, 0)).InvokeAsync(Request("alice", aiRoute: true));
        tracker.TokensToday("alice").Should().Be(542);

        // Over the allowance an AI route is refused until midnight UTC.
        await SpendAsync(tracker, "alice", 458);
        var overRan = false;
        var ctx = Request("alice", aiRoute: true);

        await Gate(tracker, limit: 1_000, () => overRan = true).InvokeAsync(ctx);

        overRan.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        ctx.Response.Headers.RetryAfter.ToString().Should().Be("7200", "it is 22:00 UTC, two hours before the allowance resets");

        // Under the allowance, on another route or with no limit, the request goes through.
        foreach (var (user, aiRoute, limit) in new[] { ("bob", true, 1_000L), ("alice", false, 1_000L), ("alice", true, 0L) })
        {
            var ran = false;
            await Gate(tracker, limit, () => ran = true).InvokeAsync(Request(user, aiRoute));
            ran.Should().BeTrue($"{user} on {(aiRoute ? "an AI" : "a plain")} route with a limit of {limit}");
        }

        _clock.Advance(TimeSpan.FromHours(2));

        tracker.TokensToday("alice").Should().Be(0, "a new day starts from nothing");
    }
}
