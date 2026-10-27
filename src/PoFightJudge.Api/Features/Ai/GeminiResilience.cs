using System.Net;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace PoFightJudge.Api.Features.Ai;

/// <summary>
/// Registers the four Gemini clients with the budget each latency class is legitimately allowed. The interactive
/// pair (fast, tts) run the standard resilience pipeline in its canonical order — total timeout outside, then
/// retry, circuit breaker and the per-attempt timeout — so the total really is the worst case a caller waits and a
/// single slow attempt cannot eat the whole budget; 5xx/408/429 retry and <c>Retry-After</c> is honoured while a 400
/// or 403 fails on the first attempt. The analysis client instead gets a ten-minute timeout (flex-tier calls are
/// queued by design) and <see cref="GeminiRetryHandler"/>; the stream client gets nothing but a timeout.
/// </summary>
public static class GeminiResilience
{
    // A round line streams in ~1-3 s; 8 s is already the long tail, and failing there leaves room for one retry
    // inside a 20 s ceiling the viewer will still sit through.
    public const int FastAttemptSeconds = 8;
    public const int FastTotalSeconds = 20;

    // Gemini TTS measures 12-17 s and spikes past 30 s. The client renders the line first and fetches audio
    // separately, so this ceiling delays the voice, never the text.
    public const int TtsAttemptSeconds = 45;
    public const int TtsTotalSeconds = 100;

    public static TimeSpan StreamTimeout { get; } = TimeSpan.FromSeconds(120);

    /// <summary>Google's guidance for the flex tier: allow ten minutes so a queued request is not torn down before it is served.</summary>
    public static TimeSpan AnalysisTimeout { get; } = TimeSpan.FromMinutes(10);

    /// <summary>The clients are always registered; without a key no header is sent and the provider answers 400/403 — which the degraded gate keeps from reaching users in Production.</summary>
    /// <remarks>
    /// With an Azure endpoint the same named clients point at the Azure OpenAI resource and carry the app identity's
    /// token instead of a key: one set of latency classes, whichever provider answers.
    /// </remarks>
    public static IServiceCollection AddGeminiClients(this IServiceCollection services, string? apiKey, Uri? azureEndpoint = null)
    {
        services.AddTransient<GeminiRetryHandler>();

        // The pipeline owns the whole budget: the standard handler sets HttpClient.Timeout to infinite so only its
        // total-request timeout applies (a client timeout here would be overwritten, not layered).
        Named(services, GeminiHttpClients.Fast, apiKey, azureEndpoint, timeout: null)
            .AddGeminiPipeline(FastAttemptSeconds, FastTotalSeconds, maxRetries: 1, baseDelayMs: 400);
        Named(services, GeminiHttpClients.Tts, apiKey, azureEndpoint, timeout: null)
            .AddGeminiPipeline(TtsAttemptSeconds, TtsTotalSeconds, maxRetries: 2, baseDelayMs: 2000);
        Named(services, GeminiHttpClients.Stream, apiKey, azureEndpoint, StreamTimeout);
        Named(services, GeminiHttpClients.Analysis, apiKey, azureEndpoint, AnalysisTimeout)
            .AddHttpMessageHandler<GeminiRetryHandler>();
        return services;
    }

    private static IHttpClientBuilder Named(IServiceCollection services, string name, string? apiKey, Uri? azureEndpoint, TimeSpan? timeout)
    {
        var builder = services.AddHttpClient(name, client =>
            {
                client.BaseAddress = azureEndpoint is null ? GeminiHttp.RestBase : AzureOpenAi.RestBase(azureEndpoint);
                if (timeout is { } clientTimeout)
                {
                    client.Timeout = clientTimeout;
                }

                // Several calls are in flight at once; one multiplexed HTTP/2 connection beats a handshake each.
                client.DefaultRequestVersion = HttpVersion.Version20;
                client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
                if (azureEndpoint is null && !string.IsNullOrWhiteSpace(apiKey))
                {
                    client.DefaultRequestHeaders.Add(GeminiHttp.ApiKeyHeader, apiKey);
                }
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // Long-lived pools go stale behind the load balancer; recycling beats a dead-connection retry.
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                EnableMultipleHttp2Connections = true,
            });

        // Outermost: the token is set once per call, and it outlives any retry the handlers inside it make.
        return azureEndpoint is null
            ? builder
            : builder.AddHttpMessageHandler(sp => new AzureTokenHandler(sp.GetRequiredService<AzureOpenAiToken>()));
    }

    private static IHttpClientBuilder AddGeminiPipeline(this IHttpClientBuilder builder, int attemptSeconds, int totalSeconds, int maxRetries, int baseDelayMs)
    {
        builder.AddStandardResilienceHandler(o =>
        {
            o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(totalSeconds);
            o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(attemptSeconds);

            o.Retry.MaxRetryAttempts = maxRetries;
            o.Retry.BackoffType = DelayBackoffType.Exponential;
            o.Retry.UseJitter = true;
            o.Retry.Delay = TimeSpan.FromMilliseconds(baseDelayMs);

            o.CircuitBreaker.FailureRatio = 0.5;
            // The handler validates SamplingDuration >= 2 x AttemptTimeout; a narrower window cannot observe a full attempt.
            o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(attemptSeconds * 4);
            o.CircuitBreaker.MinimumThroughput = 10;
            o.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);
        });
        return builder;
    }
}
