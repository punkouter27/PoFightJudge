using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using PoFightJudge.Api.Features.Ai;
using PoFightJudge.Api.Features.Voice;

namespace PoFightJudge.Api.Features.Live;

/// <summary>
/// The live host on Azure OpenAI realtime (<c>gpt-realtime-mini</c>), speaking the same <see cref="LiveServerEvent"/>s
/// the Gemini client does, so the orchestrator cannot tell them apart. The differences live here: audio goes in at
/// 24 kHz (the room is recorded at 16), a text nudge or a tool result has to ask for a response of its own, and
/// only one response may be running at a time, so a request made while one is running waits for it to finish.
/// </summary>
/// <remarks>
/// There is no session resumption: a dropped line reconnects as a new conversation, and the orchestrator's
/// "pick up where you were" nudge is all the new one hears of the old.
/// </remarks>
public sealed partial class AzureRealtimeClient(
    Func<CancellationToken, Task<ILiveSocket>> connect,
    LiveOptions options,
    GeminiModelOptions models,
    ILogger<AzureRealtimeClient> logger) : IGeminiLiveClient
{
    private readonly Channel<LiveServerEvent> _events =
        Channel.CreateUnbounded<LiveServerEvent>(new UnboundedChannelOptions { SingleReader = true });

    private readonly TaskCompletionSource _setupComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _gate = new();

    private ILiveSocket? _socket;
    private Task? _pump;
    private CancellationTokenSource? _pumping;
    private bool _responding;
    private bool _responseWanted;

    public ChannelReader<LiveServerEvent> Events => _events.Reader;

    public string? ResumptionHandle => null;

    /// <summary>The realtime endpoint for one deployment, from the resource's endpoint.</summary>
    public static Uri AddressFor(Uri endpoint, string deployment)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var wss = new UriBuilder(new Uri(endpoint, "openai/v1/realtime")) { Scheme = "wss", Port = -1, Query = $"model={Uri.EscapeDataString(deployment)}" };
        return wss.Uri;
    }

    /// <summary>The real connector: a WebSocket carrying the app identity's bearer token.</summary>
    public static async Task<ILiveSocket> ConnectAsync(Uri address, AzureOpenAiToken token, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(token);
        var bearer = await token.GetAsync(ct);
        ClientWebSocket? socket = null;
        try
        {
            socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", $"Bearer {bearer}");
            await socket.ConnectAsync(address, ct);
            var live = new ClientWebSocketLiveSocket(socket);
            socket = null;
            return live;
        }
        finally
        {
            socket?.Dispose();
        }
    }

    public async Task ConnectAsync(LiveSessionConfig config, CancellationToken ct)
    {
        if (_socket is not null)
        {
            throw new InvalidOperationException("Already connected.");
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        _socket = await connect(ct);
        _pumping = new CancellationTokenSource();
        _pump = PumpAsync(_socket, _pumping.Token);
        await _socket.SendAsync(SessionUpdate(models, config), ct);

        try
        {
            await _setupComplete.Task.WaitAsync(options.SetupTimeout, ct);
        }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException($"Azure realtime did not accept the session within {options.SetupTimeout.TotalSeconds:0} seconds.", ex);
        }

        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        LogConnected(logger, models.Live, elapsed.TotalMilliseconds);
    }

    public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16k, CancellationToken ct) =>
        Socket.SendAsync(new JsonObject
        {
            ["type"] = "input_audio_buffer.append",
            ["audio"] = Convert.ToBase64String(Upsample16To24(pcm16k.Span)),
        }.ToJsonString(), ct);

    public async Task SendTextAsync(string text, CancellationToken ct)
    {
        await Socket.SendAsync(Item(new JsonObject
        {
            ["type"] = "message",
            ["role"] = "user",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = text }),
        }), ct);
        await RequestResponseAsync(ct);
    }

    public async Task SendToolResponseAsync(string id, string name, object result, CancellationToken ct)
    {
        await Socket.SendAsync(Item(new JsonObject
        {
            ["type"] = "function_call_output",
            ["call_id"] = id,
            ["output"] = JsonSerializer.SerializeToNode(result)?.ToJsonString() ?? "null",
        }), ct);
        await RequestResponseAsync(ct);
    }

    public async Task CloseAsync(CancellationToken ct)
    {
        if (_socket is null)
        {
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.CloseTimeout);
        try
        {
            await _socket.CloseAsync(timeout.Token);
            if (_pumping is not null)
            {
                await _pumping.CancelAsync();
            }

            if (_pump is not null)
            {
                await _pump.WaitAsync(timeout.Token);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogCloseFailed(logger, ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync(CancellationToken.None);
        if (_socket is not null)
        {
            await _socket.DisposeAsync();
        }

        _pumping?.Dispose();
    }

    /// <summary>
    /// The session: the host's instructions and voice, its tools, audio both ways and both sides transcribed —
    /// the players' words are what the analysis reads, the host's are the caption feed.
    /// </summary>
    public static string SessionUpdate(GeminiModelOptions models, LiveSessionConfig config)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(config);
        var tools = new JsonArray();
        foreach (var declaration in config.FunctionDeclarations?.OfType<JsonObject>() ?? [])
        {
            tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["name"] = declaration["name"]?.DeepClone(),
                ["description"] = declaration["description"]?.DeepClone(),
                ["parameters"] = AzureOpenAi.ToJsonSchema(declaration["parameters"]) ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            });
        }

        var pcm = new JsonObject { ["type"] = "audio/pcm", ["rate"] = TtsAudioFormats.PcmSampleRate };
        return new JsonObject
        {
            ["type"] = "session.update",
            ["session"] = new JsonObject
            {
                ["type"] = "realtime",
                ["instructions"] = config.SystemInstruction,
                ["output_modalities"] = new JsonArray("audio"),
                ["audio"] = new JsonObject
                {
                    ["input"] = new JsonObject
                    {
                        ["format"] = pcm.DeepClone(),
                        ["transcription"] = new JsonObject { ["model"] = models.TurnTranscribe },
                        // The host finishes its own (ten-second) turn. Talked over, realtime cancels the answer mid
                        // tool call, and a set_players cut off there arrives with no arguments and fails; two people
                        // arguing over the host would keep the show in its intro for good (measured 2026-10-27).
                        ["turn_detection"] = new JsonObject { ["type"] = "server_vad", ["interrupt_response"] = false },
                    },
                    ["output"] = new JsonObject
                    {
                        ["format"] = pcm,
                        ["voice"] = AzureVoices.For(string.IsNullOrWhiteSpace(config.Voice) ? models.Voice : config.Voice),
                    },
                },
                ["tools"] = tools,
                ["tool_choice"] = "auto",
            },
        }.ToJsonString();
    }

    /// <summary>
    /// 16 kHz to 24 kHz by straight-line interpolation: three samples out for every two in.
    /// </summary>
    /// <remarks>ponytail: each chunk is resampled alone, so its last sample is held rather than blended into the next chunk; inaudible for speech at the 20-100 ms chunk sizes the mic sends.</remarks>
    public static byte[] Upsample16To24(ReadOnlySpan<byte> pcm16k)
    {
        var inSamples = pcm16k.Length / 2;
        var outSamples = inSamples * 3 / 2;
        var output = new byte[outSamples * 2];
        for (var j = 0; j < outSamples; j++)
        {
            var position = j * 2.0 / 3.0;
            var i = (int)position;
            var a = Sample(pcm16k, i, inSamples);
            var b = Sample(pcm16k, i + 1, inSamples);
            var value = a + ((b - a) * (position - i));
            BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(j * 2), (short)Math.Round(value));
        }

        return output;

        static short Sample(ReadOnlySpan<byte> pcm, int index, int count) =>
            BinaryPrimitives.ReadInt16LittleEndian(pcm[(Math.Min(index, count - 1) * 2)..]);
    }

    /// <summary>
    /// Decodes one server event. Both the GA names and the older preview ones are read, so a deployment on either
    /// version speaks to the same orchestrator. Anything unknown yields nothing.
    /// </summary>
    public static IReadOnlyList<LiveServerEvent> Parse(JsonObject message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var type = message["type"]?.GetValue<string>();
        string? Text(string field) => message[field] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

        return type switch
        {
            "session.updated" => [new LiveServerEvent.SetupComplete()],
            "response.output_audio.delta" or "response.audio.delta" when Text("delta") is { } audio && TryDecode(audio, out var pcm) => [new LiveServerEvent.AudioOut(pcm)],
            "response.output_audio_transcript.delta" or "response.audio_transcript.delta" when Text("delta") is { Length: > 0 } said => [new LiveServerEvent.OutputTranscript(said)],
            "conversation.item.input_audio_transcription.completed" when Text("transcript") is { Length: > 0 } heard => [new LiveServerEvent.InputTranscript(heard.TrimEnd() + " ")],
            "response.function_call_arguments.done" => [new LiveServerEvent.ToolCall(
                Text("call_id") ?? Text("name") ?? string.Empty,
                Text("name") ?? string.Empty,
                Arguments(Text("arguments")))],
            "response.done" => [new LiveServerEvent.GenerationComplete(), new LiveServerEvent.TurnComplete()],
            _ => [],
        };
    }

    private static JsonElement Arguments(string? json)
    {
        try
        {
            using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return parsed.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }
    }

    private static string Item(JsonObject item) =>
        new JsonObject { ["type"] = "conversation.item.create", ["item"] = item }.ToJsonString();

    private static readonly string CreateResponse = new JsonObject { ["type"] = "response.create" }.ToJsonString();

    private ILiveSocket Socket => _socket ?? throw new InvalidOperationException("Not connected.");

    /// <summary>Asks for a response now, or, while one is running, as soon as it is done.</summary>
    private async Task RequestResponseAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_responding)
            {
                _responseWanted = true;
                return;
            }

            _responding = true;
        }

        await Socket.SendAsync(CreateResponse, ct);
    }

    private async Task PumpAsync(ILiveSocket socket, CancellationToken ct)
    {
        var reason = "closed";
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var raw = await socket.ReceiveAsync(ct);
                if (raw is null)
                {
                    break;
                }

                if (GeminiHttp.TryParse(raw) is not { } message)
                {
                    continue;
                }

                await TrackAsync(socket, message, ct);
                foreach (var received in Parse(message))
                {
                    if (received is LiveServerEvent.SetupComplete)
                    {
                        _setupComplete.TrySetResult();
                    }

                    _events.Writer.TryWrite(received);
                }
            }
        }
        catch (OperationCanceledException)
        {
            reason = "cancelled";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            reason = ex.Message;
            LogPumpFailed(logger, ex);
        }
        finally
        {
            _setupComplete.TrySetException(new InvalidOperationException($"Azure realtime closed before the session was accepted: {reason}"));
            _events.Writer.TryWrite(new LiveServerEvent.Closed(reason));
            _events.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Keeps count of the one response that may run. The host is never cut off (see <see cref="SessionUpdate"/>), so
    /// unlike Gemini this client never reports <see cref="LiveServerEvent.Interrupted"/>.
    /// </summary>
    private async Task TrackAsync(ILiveSocket socket, JsonObject message, CancellationToken ct)
    {
        var send = false;
        switch (message["type"]?.GetValue<string>())
        {
            case "response.created":
                lock (_gate)
                {
                    _responding = true;
                }

                break;
            case "response.done":
                lock (_gate)
                {
                    send = _responseWanted;
                    _responseWanted = false;
                    _responding = send;
                }

                break;
            case "error":
                var detail = message["error"]?["message"]?.GetValue<string>() ?? message.ToJsonString();
                LogServerError(logger, detail);
                if (message["error"]?["code"]?.GetValue<string>() is "conversation_already_has_active_response")
                {
                    lock (_gate)
                    {
                        _responseWanted = true;
                    }
                }

                break;
            default:
                break;
        }

        if (send)
        {
            await socket.SendAsync(CreateResponse, ct);
        }
    }

    private static bool TryDecode(string base64, out byte[] pcm)
    {
        var buffer = new byte[((base64.Length * 3) + 3) / 4];
        if (Convert.TryFromBase64String(base64, buffer, out var written))
        {
            pcm = buffer[..written];
            return true;
        }

        pcm = [];
        return false;
    }

    [LoggerMessage(EventId = 3011, Level = LogLevel.Information, Message = "Azure realtime connected ({Model}) in {ElapsedMs:F0}ms")]
    private static partial void LogConnected(ILogger logger, string model, double elapsedMs);

    [LoggerMessage(EventId = 3012, Level = LogLevel.Warning, Message = "Azure realtime receive loop ended with an error")]
    private static partial void LogPumpFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 3013, Level = LogLevel.Debug, Message = "Azure realtime close was not clean")]
    private static partial void LogCloseFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 3014, Level = LogLevel.Warning, Message = "Azure realtime reported an error: {Detail}")]
    private static partial void LogServerError(ILogger logger, string detail);
}
