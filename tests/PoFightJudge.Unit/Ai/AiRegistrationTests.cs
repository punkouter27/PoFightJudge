using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using PoFightJudge.Api.Features.Ai;
using PoFightJudge.Api.Features.Voice;
using PoFightJudge.Shared.Configuration;

namespace PoFightJudge.Unit.Ai;

public class AiRegistrationTests
{
    private static (AiMode Mode, ServiceProvider Services) Register(string environment, string? apiKey, bool forceFakes = false, string? azure = null)
    {
        var values = new Dictionary<string, string?> { [ConfigKeys.Ai.AzureOpenAiEndpoint] = azure };
        if (apiKey is not null)
        {
            values[ConfigKeys.Ai.GeminiApiKey] = apiKey;
        }

        if (forceFakes)
        {
            values[Toggles.UseFakes] = "true";
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(environment);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Substitute.For<Azure.Core.TokenCredential>());
        var mode = services.AddPoAi(configuration, env);
        return (mode, services.BuildServiceProvider());
    }

    [Fact]
    public async Task Fakes_run_only_outside_production_when_there_is_no_key_or_the_flag_forces_them()
    {
        Register("Development", apiKey: null).Mode.UseFakes.Should().BeTrue("no key in Development means the fakes");
        Register("Development", apiKey: "k").Mode.UseFakes.Should().BeFalse();
        Register("Development", apiKey: "k", forceFakes: true).Mode.UseFakes.Should().BeTrue("the flag forces them");
        Register("Test", apiKey: null).Mode.UseFakes.Should().BeTrue();

        var production = Register("Production", apiKey: null, forceFakes: true).Mode;
        production.UseFakes.Should().BeFalse("Production never runs fakes, whatever the flag says");
        production.HasGeminiKey.Should().BeFalse();
        production.Reason.Should().Contain("degraded");

        // An Azure endpoint is a real provider with no key at all, and it wins over a Gemini key.
        var (mode, services) = Register("Development", apiKey: "k", azure: "https://res.cognitiveservices.azure.com/");
        using (services)
        {
            (mode.UseFakes, mode.UsesAzure).Should().Be((false, true));
            mode.Reason.Should().Contain("res.cognitiveservices.azure.com");
            services.GetRequiredService<IGeminiText>().Should().BeOfType<AzureOpenAiText>();
            services.GetServices<ITtsProvider>().Should().ContainSingle().Which.Should().BeOfType<AzureTtsService>();
            using var fast = services.GetRequiredService<IHttpClientFactory>().CreateClient(GeminiHttpClients.Fast);
            fast.BaseAddress.Should().Be(new Uri("https://res.cognitiveservices.azure.com/openai/v1/"));
            fast.DefaultRequestHeaders.Contains(GeminiHttp.ApiKeyHeader).Should().BeFalse("Azure is reached with the app's identity, never a key");
        }

        // The token is fetched once and kept until five minutes before it expires: locally each fetch is an az CLI call.
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
        var credential = Substitute.For<Azure.Core.TokenCredential>();
        credential.GetTokenAsync(default, default).ReturnsForAnyArgs(_ => new Azure.Core.AccessToken("t", clock.GetUtcNow().AddHours(1)));
        var cache = new AzureOpenAiToken(credential, clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<AzureOpenAiToken>.Instance);
        (await cache.GetAsync(CancellationToken.None), await cache.GetAsync(CancellationToken.None)).Should().Be(("t", "t"));
        await credential.ReceivedWithAnyArgs(1).GetTokenAsync(default, default);
        clock.Advance(TimeSpan.FromMinutes(56));
        await cache.GetAsync(CancellationToken.None);
        await credential.ReceivedWithAnyArgs(2).GetTokenAsync(default, default);

        // Startup does not call the Gemini key missing when Azure is the provider: in Production that would be a 503 site.
        static IConfiguration Config(string key, string value) => new ConfigurationBuilder().AddInMemoryCollection([new(key, value)]).Build();
        PoFightJudge.Api.Features.Diagnostics.StartupSecretValidator.FindMissing(Config(ConfigKeys.Ai.AzureOpenAiEndpoint, "https://res.cognitiveservices.azure.com/"), isProduction: false).Should().BeEmpty();
        PoFightJudge.Api.Features.Diagnostics.StartupSecretValidator.FindMissing(Config("unrelated", "x"), isProduction: false).Should().Equal(ConfigKeys.Ai.GeminiApiKey);

        // Its payload: system first for the prefix cache, a reasoning deployment gets an effort and no temperature,
        // and a Gemini schema goes out as JSON Schema.
        var schema = new JsonObject { ["type"] = "OBJECT", ["properties"] = new JsonObject { ["line"] = new JsonObject { ["type"] = "STRING" } } };
        var reasoning = AzureOpenAiText.BuildPayload(new GeminiTextRequest(new GeminiPrompt("sys", "user"), "gpt-5-mini", "judge.assess", schema, ThinkingLevel: "low"), stream: false);
        reasoning["messages"]![0]!["role"]!.GetValue<string>().Should().Be("system");
        (reasoning["reasoning_effort"]!.GetValue<string>(), reasoning.ContainsKey("temperature")).Should().Be(("low", false));
        reasoning["response_format"]!["json_schema"]!["name"]!.GetValue<string>().Should().Be("judge_assess");
        reasoning["response_format"]!["json_schema"]!["schema"]!["properties"]!["line"]!["type"]!.GetValue<string>().Should().Be("string");
        var plain = AzureOpenAiText.BuildPayload(new GeminiTextRequest(new GeminiPrompt(string.Empty, "user"), "gpt-4.1-mini", "round", Temperature: 0.9, MaxOutputTokens: 300, ThinkingLevel: "low"), stream: true);
        (plain["temperature"]!.GetValue<double>(), plain["max_completion_tokens"]!.GetValue<int>(), plain.ContainsKey("reasoning_effort")).Should().Be((0.9, 300, false));
        plain["messages"]!.AsArray().Should().ContainSingle("an empty system half is left out");
        plain["stream_options"]!["include_usage"]!.GetValue<bool>().Should().BeTrue();
        AzureOpenAiText.Delta(JsonNode.Parse("""{"choices":[{"delta":{"content":"You"}}]}""") as JsonObject).Should().Be("You");
        AzureOpenAiText.Delta(JsonNode.Parse("""{"choices":[],"usage":{"prompt_tokens":11}}""") as JsonObject).Should().BeNull("the usage chunk has no choices, and must not throw");
    }

    [Fact]
    public void Named_clients_carry_the_key_the_base_address_and_their_own_budgets()
    {
        var (_, services) = Register("Development", apiKey: "secret-key");
        var factory = services.GetRequiredService<IHttpClientFactory>();

        using var fast = factory.CreateClient(GeminiHttpClients.Fast);
        using var tts = factory.CreateClient(GeminiHttpClients.Tts);
        using var stream = factory.CreateClient(GeminiHttpClients.Stream);
        using var analysis = factory.CreateClient(GeminiHttpClients.Analysis);

        fast.BaseAddress.Should().Be(GeminiHttp.RestBase);
        fast.DefaultRequestHeaders.GetValues(GeminiHttp.ApiKeyHeader).Should().ContainSingle().Which.Should().Be("secret-key");
        fast.Timeout.Should().Be(Timeout.InfiniteTimeSpan, "the standard resilience handler owns the whole budget and disables the client timeout");
        tts.Timeout.Should().Be(Timeout.InfiniteTimeSpan);
        stream.Timeout.Should().Be(GeminiResilience.StreamTimeout);
        analysis.Timeout.Should().Be(GeminiResilience.AnalysisTimeout, "flex-tier calls are queued by design");
        analysis.DefaultRequestVersion.Should().Be(HttpVersion.Version20);
        services.GetRequiredService<GeminiModelOptions>().Should().Be(GeminiModelOptions.Defaults);
    }
}
