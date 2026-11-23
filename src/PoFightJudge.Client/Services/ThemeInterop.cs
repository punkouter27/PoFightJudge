using Microsoft.JSInterop;
using Radzen;

namespace PoFightJudge.Client.Services;

/// <summary>
/// The theme follows the device (Po web standard): light or dark as <c>prefers-color-scheme</c> says, with no switch
/// and nothing stored. Our tokens follow it in CSS on their own (po-tokens.css, light-dark()), and <c>js/theme.js</c>
/// owns Radzen's one stylesheet link (ahead of the app's CSS) and swaps it when the device flips. This keeps
/// <see cref="ThemeService"/> and <see cref="IsDark"/> (shown on Settings) on the same scheme.
/// </summary>
public sealed class ThemeInterop(IJSRuntime js, ThemeService radzen) : IDisposable
{
    public const string RadzenDark = "material-dark";
    public const string RadzenLight = "material";

    private DotNetObjectReference<ThemeInterop>? _self;

    public bool IsDark { get; private set; } = true;

    /// <summary>Reads the device's scheme, applies it to Radzen, and listens for it to change. Safe without JS.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            Apply(await js.InvokeAsync<bool>("PoTheme.prefersDark"));
            if (_self is null)
            {
                _self = DotNetObjectReference.Create(this);
                await js.InvokeVoidAsync("PoTheme.watch", _self);
            }
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException)
        {
            // No interop (prerender, a test renderer): Radzen keeps the sheet it was given.
        }
    }

    /// <summary>Called by <c>PoTheme.watch</c> when the device switches between light and dark.</summary>
    [JSInvokable]
    public void OnDeviceThemeChanged(bool dark) => Apply(dark);

    private void Apply(bool dark)
    {
        IsDark = dark;
        radzen.SetTheme(dark ? RadzenDark : RadzenLight);
    }

    public void Dispose() => _self?.Dispose();
}
