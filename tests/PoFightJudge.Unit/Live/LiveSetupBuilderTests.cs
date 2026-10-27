using System.Text.Json;
using System.Text.Json.Nodes;
using PoFightJudge.Api.Features.Ai;
using PoFightJudge.Api.Features.Live;

namespace PoFightJudge.Unit.Live;

/// <summary>
/// The setup frame. Its shape was checked against the Live API reference on 2026-09-06: response modality and voice
/// live inside <c>generationConfig</c>, everything else sits directly on <c>setup</c>.
/// </summary>
/// <summary>
/// The setup frame. Its shape was checked against the Live API reference on 2026-09-06: response modality and voice
/// live inside <c>generationConfig</c>, everything else sits directly on <c>setup</c>.
/// </summary>
public class LiveSetupBuilderTests
{
    private static readonly GeminiModelOptions Models = GeminiModelOptions.Defaults;

    private static JsonElement Build(LiveSessionConfig config) =>
        JsonDocument.Parse(LiveSetupBuilder.Build(Models, config)).RootElement.GetProperty("setup");

    [Fact]
    public void Search_is_always_offered_and_our_own_tools_only_when_there_are_some()
    {
        var without = Build(new LiveSessionConfig("host the debate")).GetProperty("tools");
        without.GetArrayLength().Should().Be(1);
        without[0].TryGetProperty("googleSearch", out _).Should().BeTrue("the host checks claims out loud");

        JsonArray declarations = [new JsonObject { ["name"] = "call_verdict" }];
        var with = Build(new LiveSessionConfig("host the debate", FunctionDeclarations: declarations)).GetProperty("tools");
        with.GetArrayLength().Should().Be(2);
        with[1].GetProperty("functionDeclarations")[0].GetProperty("name").GetString().Should().Be("call_verdict");

        // Azure realtime: the same host as one session.update, tools in JSON Schema and the voice in OpenAI's set.
        var session = JsonNode.Parse(AzureRealtimeClient.SessionUpdate(
            Models with { Live = "gpt-realtime-mini", TurnTranscribe = "gpt-4o-transcribe-diarize" },
            new LiveSessionConfig("host the debate", Voice: "Kore", FunctionDeclarations: ToolDeclarations.FunctionDeclarations())))!["session"]!;
        session["instructions"]!.GetValue<string>().Should().Be("host the debate");
        session["audio"]!["input"]!["format"]!["rate"]!.GetValue<int>().Should().Be(24_000);
        session["audio"]!["input"]!["transcription"]!["model"]!.GetValue<string>().Should().Be("gpt-4o-transcribe-diarize");
        session["audio"]!["input"]!["turn_detection"]!["interrupt_response"]!.GetValue<bool>().Should().BeFalse("talked over, the host's tool call is cut off with no arguments");
        session["audio"]!["output"]!["voice"]!.GetValue<string>().Should().BeOneOf("coral", "sage", "shimmer");
        JsonNode Tool(string name) => session["tools"]!.AsArray().Single(t => string.Equals(t!["name"]!.GetValue<string>(), name, StringComparison.Ordinal))!;
        Tool(ToolDeclarations.StartTurn)["type"]!.GetValue<string>().Should().Be("function");
        Tool(ToolDeclarations.StartTurn)["parameters"]!["properties"]!["player"]!["type"]!.GetValue<string>().Should().Be("string");
        Tool(ToolDeclarations.EndDebate)["parameters"]!["type"]!.GetValue<string>().Should().Be("object", "a tool without arguments still declares an empty object");

        // Its events are the ones the Gemini parser yields, so the orchestrator cannot tell the two apart.
        static LiveServerEvent[] Events(string json) => [.. AzureRealtimeClient.Parse((JsonNode.Parse(json) as JsonObject)!)];
        Events("""{"type":"session.updated"}""").Should().ContainSingle().Which.Should().BeOfType<LiveServerEvent.SetupComplete>();
        Events("""{"type":"response.output_audio.delta","delta":"AAEC"}""").Should().ContainSingle()
            .Which.Should().BeOfType<LiveServerEvent.AudioOut>().Which.Pcm24k.ToArray().Should().Equal(0, 1, 2);
        Events("""{"type":"conversation.item.input_audio_transcription.completed","transcript":"you never do"}""").Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new LiveServerEvent.InputTranscript("you never do "));
        var call = Events("""{"type":"response.function_call_arguments.done","call_id":"c1","name":"start_turn","arguments":"{\"player\":\"player2\"}"}""")
            .Should().ContainSingle().Which.Should().BeOfType<LiveServerEvent.ToolCall>().Which;
        (call.Id, call.Name, call.Args.GetProperty("player").GetString()).Should().Be(("c1", "start_turn", "player2"));
        Events("""{"type":"response.done"}""").OfType<LiveServerEvent.TurnComplete>().Should().ContainSingle();
        Events("""{"type":"rate_limits.updated"}""").Should().BeEmpty();

        // The room is recorded at 16 kHz and realtime hears 24: three samples out for every two in, on the line between them.
        var up = AzureRealtimeClient.Upsample16To24([0, 0, 30, 0]);
        Enumerable.Range(0, 3).Select(i => BitConverter.ToInt16(up, i * 2)).Should().Equal(0, 20, 30);
        AzureRealtimeClient.AddressFor(new Uri("https://res.cognitiveservices.azure.com/"), "gpt-realtime-mini")
            .Should().Be(new Uri("wss://res.cognitiveservices.azure.com/openai/v1/realtime?model=gpt-realtime-mini"));
    }
}
