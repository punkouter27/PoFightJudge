using Microsoft.JSInterop;

namespace PoFightJudge.Client.Services;

/// <summary>
/// The .NET side of <c>PoAsr</c>: Whisper in the browser (WebGPU where there is one, WASM where not), reading the
/// turn <c>PoMic</c> just recorded. A shortcut, never a dependency — null means the clip goes to the server.
/// </summary>
public sealed class AsrInterop(IJSRuntime js)
{
    public const string Warm = "PoAsr.warm";
    public const string Transcribe = "PoAsr.transcribe";

    /// <summary>Starts the one-off model download in the background, so a later turn finds it ready.</summary>
    public async Task WarmAsync(CancellationToken ct = default)
    {
        try
        {
            await js.InvokeVoidAsync(Warm, ct);
        }
        catch (JSException)
        {
            // No worker, no model: every turn goes to the server, as it did before.
        }
    }

    /// <summary>
    /// What the browser heard in the last recorded turn, or null when it cannot say: the model is not loaded yet,
    /// it failed, or it heard nothing (which the server is better placed to confirm).
    /// </summary>
    public async Task<string?> TranscribeAsync(CancellationToken ct = default)
    {
        try
        {
            return await js.InvokeAsync<string?>(Transcribe, ct) is { } text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
        }
        catch (JSException)
        {
            return null;
        }
    }
}
