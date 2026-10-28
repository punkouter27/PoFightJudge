using System.Threading.Channels;
using PoFightJudge.Api.Features.Ai;

namespace PoFightJudge.Api.Features.Live;

/// <summary>One live audio conversation with the host model.</summary>
public interface IGeminiLiveClient : IAsyncDisposable
{
    /// <summary>Server events, in order. Completes when the connection closes.</summary>
    ChannelReader<LiveServerEvent> Events { get; }

    /// <summary>The handle to reconnect with, once the session has offered one.</summary>
    string? ResumptionHandle { get; }

    Task ConnectAsync(LiveSessionConfig config, CancellationToken ct);

    Task SendAudioAsync(ReadOnlyMemory<byte> pcm16k, CancellationToken ct);

    /// <summary>Sends a text turn. This is how the host is nudged: "Alex has had 45 seconds — cut in".</summary>
    Task SendTextAsync(string text, CancellationToken ct);

    Task SendToolResponseAsync(string id, string name, object result, CancellationToken ct);

    /// <summary>Best-effort, bounded, and never throws.</summary>
    Task CloseAsync(CancellationToken ct);
}

/// <summary>Makes one client per connection; reconnecting means asking for another.</summary>
public delegate IGeminiLiveClient LiveClientFactory();

/// <summary>
/// Raw WebSocket client for the Gemini Live API (BidiGenerateContent), one instance per connection. It owns the
/// receive pump and nothing else: what the events mean is the orchestrator's business.
/// </summary>
public sealed partial class GeminiLiveClient(
    LiveSocketConnector connect,
    LiveOptions options,
    GeminiModelOptions models,
    ILogger<GeminiLiveClient> logger) : IGeminiLiveClient
{
    private readonly Channel<LiveServerEvent> _events =
        Channel.CreateUnbounded<LiveServerEvent>(new UnboundedChannelOptions { SingleReader = true });

    private readonly TaskCompletionSource _setupComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private ILiveSocket? _socket;
    private Task? _pump;
    private CancellationTokenSource? _pumping;

    public ChannelReader<LiveServerEvent> Events => _events.Reader;

    public string? ResumptionHandle { get; private set; }

    /// <summary>
    /// Connects, sends the setup frame and waits to be acknowledged, so nothing is sent before the session exists.
    /// A session that never acknowledges fails here rather than swallowing everything sent into it.
    /// </summary>
    public async Task ConnectAsync(LiveSessionConfig config, CancellationToken ct)
    {
        if (_socket is not null)
        {
            throw new InvalidOperationException("Already connected.");
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new InvalidOperationException("The Gemini API key is not configured, so there is no live session to open.");
        }

        _socket = await connect(LiveSockets.AddressFor(options.Endpoint, options.ApiKey), options.ApiKey, ct);
        await _socket.SendAsync(LiveSetupBuilder.Build(models, config), ct);

        _pumping = new CancellationTokenSource();
        _pump = PumpAsync(_socket, _pumping.Token);

        try
        {
            await _setupComplete.Task.WaitAsync(options.SetupTimeout, ct);
        }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException(
                $"Gemini Live did not acknowledge setup within {options.SetupTimeout.TotalSeconds:0} seconds.", ex);
        }

        LogConnected(logger, models.Live);
    }

    public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16k, CancellationToken ct) =>
        Socket.SendAsync(LiveClientMessages.RealtimeAudio(pcm16k.Span), ct);

    public Task SendTextAsync(string text, CancellationToken ct) =>
        Socket.SendAsync(LiveClientMessages.UserText(text), ct);

    public Task SendToolResponseAsync(string id, string name, object result, CancellationToken ct) =>
        Socket.SendAsync(LiveClientMessages.ToolResponse(id, name, result), ct);

    /// <summary>Bounded and silent: a debate that is already over must not fail on the way out.</summary>
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

    private ILiveSocket Socket => _socket ?? throw new InvalidOperationException("Not connected.");

    /// <summary>
    /// The single reader. Every frame is decoded and published in order; the two events the client itself acts on
    /// are the setup acknowledgement and the resumption handle, and both are also passed on to the orchestrator.
    /// </summary>
    private async Task PumpAsync(ILiveSocket socket, CancellationToken ct)
    {
        var reason = "closed";
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var message = await socket.ReceiveAsync(ct);
                if (message is null)
                {
                    break;
                }

                foreach (var received in LiveMessageParser.Parse(message))
                {
                    switch (received)
                    {
                        case LiveServerEvent.ResumptionHandle handle:
                            ResumptionHandle = handle.Handle;
                            break;
                        case LiveServerEvent.SetupComplete:
                            _setupComplete.TrySetResult();
                            break;
                        default:
                            break;
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
            // Whoever is waiting on the connect has to hear about a socket that died first.
            _setupComplete.TrySetException(new InvalidOperationException($"Gemini Live closed before setup completed: {reason}"));
            _events.Writer.TryWrite(new LiveServerEvent.Closed(reason));
            _events.Writer.TryComplete();
        }
    }

    [LoggerMessage(EventId = 3001, Level = LogLevel.Information, Message = "Gemini Live connected ({Model})")]
    private static partial void LogConnected(ILogger logger, string model);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Warning, Message = "Gemini Live receive loop ended with an error")]
    private static partial void LogPumpFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 3003, Level = LogLevel.Debug, Message = "Gemini Live close was not clean")]
    private static partial void LogCloseFailed(ILogger logger, Exception ex);
}
