using System.Net;
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
    private static (AiMode Mode, ServiceProvider Services) Register(string environment, string? apiKey, bool forceFakes = false)
    {
        var values = new Dictionary<string, string?>();
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
        var mode = services.AddPoAi(configuration, env);
        return (mode, services.BuildServiceProvider());
    }

    [Fact]
    public void Fakes_run_only_outside_production_when_there_is_no_key_or_the_flag_forces_them()
    {
        Register("Development", apiKey: null).Mode.UseFakes.Should().BeTrue("no key in Development means the fakes");
        Register("Development", apiKey: "k").Mode.UseFakes.Should().BeFalse();
        Register("Development", apiKey: "k", forceFakes: true).Mode.UseFakes.Should().BeTrue("the flag forces them");
        Register("Test", apiKey: null).Mode.UseFakes.Should().BeTrue();

        var production = Register("Production", apiKey: null, forceFakes: true).Mode;
        production.UseFakes.Should().BeFalse("Production never runs fakes, whatever the flag says");
        production.HasGeminiKey.Should().BeFalse();
        production.Reason.Should().Contain("degraded");
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
