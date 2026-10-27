using Blazored.LocalStorage;
using Microsoft.JSInterop;
using PoFightJudge.Client.Components;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Client.Services;

/// <summary>
/// The optional choices the Settings panel holds, each defaulting to the simplest version of the app, so the setup
/// screens ask only what a match needs. Kept in LocalStorage like the theme; every read and write is guarded,
/// because a blocked store means the defaults, not a broken page.
/// </summary>
public sealed class Preferences(ILocalStorageService storage)
{
    /// <summary>The same key <c>js/theme.js</c>'s <c>PoMotion.still()</c> reads before any effect mounts.</summary>
    public const string EffectsKey = "po-effects";
    public const string HeatKey = "po-heat";
    public const string HostKey = "po-host";
    public const string AsrKey = "po-asr";
    public const string DetailKey = "po-detail";
    public const string On = "on";
    public const string Off = "off";

    private Task? _loaded;

    /// <summary>Shaders, particles, terrain, stamps and foil. Off unless somebody turns them on.</summary>
    public bool Effects { get; private set; }

    /// <summary>How hot a topic the Spin button lands on.</summary>
    public TopicField.Heat Heat { get; private set; } = TopicField.Heat.Medium;

    /// <summary>Who hosts a 2P fight set up on this device.</summary>
    public HostPersonaId Host { get; private set; } = HostPersonaId.Referee;

    /// <summary>Whether a spoken WATCH turn is transcribed in the browser. Off: it downloads a ~40 MB model once.</summary>
    public bool OnDeviceAsr { get; private set; }

    /// <summary>The verdict's traits, mood chart and thirty measurements. Off: the ruling, the claims and the advice are the verdict.</summary>
    public bool Detailed { get; private set; }

    /// <summary>Raised on every change, so a page already showing a choice can redraw without a reload.</summary>
    public event EventHandler? Changed;

    /// <summary>Loads once, however many components ask; a page awaits it before reading a choice.</summary>
    public Task InitializeAsync() => _loaded ??= LoadAsync();

    public Task SetEffectsAsync(bool on)
    {
        Effects = on;
        return WriteAsync(EffectsKey, on ? On : Off);
    }

    public Task SetHeatAsync(TopicField.Heat heat)
    {
        Heat = heat;
        return WriteAsync(HeatKey, heat.ToString());
    }

    public Task SetHostAsync(HostPersonaId host)
    {
        Host = host;
        return WriteAsync(HostKey, host.ToString());
    }

    public Task SetOnDeviceAsrAsync(bool on)
    {
        OnDeviceAsr = on;
        return WriteAsync(AsrKey, on ? On : Off);
    }

    public Task SetDetailedAsync(bool on)
    {
        Detailed = on;
        return WriteAsync(DetailKey, on ? On : Off);
    }

    private async Task LoadAsync()
    {
        Effects = string.Equals(await ReadAsync(EffectsKey), On, StringComparison.Ordinal);
        Heat = Enum.TryParse<TopicField.Heat>(await ReadAsync(HeatKey), out var heat) ? heat : Heat;
        Host = Enum.TryParse<HostPersonaId>(await ReadAsync(HostKey), out var host) ? host : Host;
        OnDeviceAsr = string.Equals(await ReadAsync(AsrKey), On, StringComparison.Ordinal);
        Detailed = string.Equals(await ReadAsync(DetailKey), On, StringComparison.Ordinal);
    }

    private async Task<string?> ReadAsync(string key)
    {
        try
        {
            return (await storage.GetItemAsStringAsync(key))?.Trim('"');
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException)
        {
            return null; // Storage blocked: the default stands.
        }
    }

    private async Task WriteAsync(string key, string value)
    {
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            await storage.SetItemAsStringAsync(key, value);
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException)
        {
            // Not kept between visits; it still applies for this one.
        }
    }
}
