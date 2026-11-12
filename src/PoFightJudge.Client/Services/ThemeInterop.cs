using Blazored.LocalStorage;
using Microsoft.JSInterop;
using Radzen;

namespace PoFightJudge.Client.Services;

/// <summary>
/// The one place the theme is read and written: the <c>data-theme</c> attribute (our tokens), Radzen's stylesheet
/// (<see cref="ThemeService"/>) and the persisted choice (LocalStorage, the same key <c>js/theme.js</c> reads before
/// first paint). Dark is the default; only an explicit choice is stored.
/// </summary>
public sealed class ThemeInterop(IJSRuntime js, ILocalStorageService storage, ThemeService radzen)
{
    public const string StorageKey = "pofightjudge:theme";
    public const string Dark = "dark";
    public const string Light = "light";
    public const string RadzenDark = "material-dark";
    public const string RadzenLight = "material";

    public string Current { get; private set; } = Dark;

    public bool IsDark => string.Equals(Current, Dark, StringComparison.Ordinal);

    /// <summary>Reads the stored choice (if any) and applies it. Safe when storage is blocked.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            var stored = await storage.GetItemAsStringAsync(StorageKey);
            if (stored is Light or Dark)
            {
                await ApplyAsync(stored, persist: false);
            }
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException)
        {
            // Storage blocked or not ready — keep the default.
        }
    }

    public Task ToggleAsync() => ApplyAsync(IsDark ? Light : Dark, persist: true);

    public async Task ApplyAsync(string theme, bool persist)
    {
        Current = theme is Light ? Light : Dark;
        radzen.SetTheme(IsDark ? RadzenDark : RadzenLight);
        try
        {
            await js.InvokeVoidAsync("document.documentElement.setAttribute", "data-theme", Current);
            await js.InvokeVoidAsync("PoTheme.chrome", Current);
            if (persist)
            {
                await storage.SetItemAsStringAsync(StorageKey, Current);
            }
        }
        catch (JSException)
        {
            // Applied via Radzen at least; the attribute/persist half failed (storage blocked).
        }
    }
}
