using Microsoft.JSInterop;

namespace PoFightJudge.Client.Services;

/// <summary>
/// The .NET side of <c>PoFx</c>: one-shot stage effects that are pure decoration. Every call is best-effort, because
/// a missing animation must never stop an argument — the page keeps running if the browser refuses.
/// </summary>
public sealed class FxInterop(IJSRuntime js)
{
    public const string Burst = "PoFx.burst";

    public const string Stamps = "PoFx.stamps";

    /// <summary>A round ringing in: a dissolve wipes across the stage (<c>.stage.fx-round</c>).</summary>
    public const string Round = "round";

    /// <summary>
    /// Arms every element matching <paramref name="selector"/> to stamp down onto the page the first time it is seen.
    /// Best-effort like the rest: a browser that will not simply shows them where they are.
    /// </summary>
    public Task StampsAsync(string selector, CancellationToken ct = default) =>
        BestEffort.JsAsync(() => js.InvokeVoidAsync(Stamps, ct, selector));

    /// <summary>Plays the effect registered for an interjection key, if the browser has one.</summary>
    public Task BurstAsync(string kind, CancellationToken ct = default) => BurstAsync(kind, null, ct);

    /// <summary>The same, on the first element matching <paramref name="selector"/> rather than the stage.</summary>
    public Task BurstAsync(string kind, string? selector, CancellationToken ct = default) =>
        BestEffort.JsAsync(() => js.InvokeVoidAsync(Burst, ct, kind, selector));
}
