using Blazored.LocalStorage;
using Bunit;
using Microsoft.JSInterop;
using NSubstitute;
using PoFightJudge.Client.Components;
using PoFightJudge.Client.Services;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Unit.Client;

/// <summary>
/// The Settings panel's optional choices: a first visit gets the simplest app (no stage effects, a medium spin, the
/// Referee hosting, no on-device transcriber), and whatever is changed is what the next visit opens on. The
/// transcriber, once on, is a shortcut and never a dependency: anything short of words sends the clip to the server.
/// </summary>
public class PreferencesTests
{
    [Fact]
    public async Task A_first_visit_gets_the_defaults_and_a_change_survives_a_reload()
    {
        var storage = Substitute.For<ILocalStorageService>();

        var first = new Preferences(storage);
        await first.InitializeAsync();
        first.Effects.Should().BeFalse();
        first.Heat.Should().Be(TopicField.Heat.Medium);
        first.Host.Should().Be(HostPersonaId.Referee);
        first.OnDeviceAsr.Should().BeFalse("it downloads a forty-megabyte model, which nobody gets without asking");

        await first.SetEffectsAsync(on: true);
        await first.SetHeatAsync(TopicField.Heat.Spicy);
        await first.SetHostAsync(HostPersonaId.Roastmaster);
        await first.SetOnDeviceAsrAsync(on: true);

        await storage.Received().SetItemAsStringAsync(Preferences.EffectsKey, "on", Arg.Any<CancellationToken>());
        await storage.Received().SetItemAsStringAsync(Preferences.HeatKey, "Spicy", Arg.Any<CancellationToken>());
        await storage.Received().SetItemAsStringAsync(Preferences.HostKey, "Roastmaster", Arg.Any<CancellationToken>());
        await storage.Received().SetItemAsStringAsync(Preferences.AsrKey, "on", Arg.Any<CancellationToken>());

        // What was written is what the next visit reads back.
        storage.GetItemAsStringAsync(Preferences.EffectsKey, Arg.Any<CancellationToken>()).Returns("on");
        storage.GetItemAsStringAsync(Preferences.HeatKey, Arg.Any<CancellationToken>()).Returns("Spicy");
        storage.GetItemAsStringAsync(Preferences.HostKey, Arg.Any<CancellationToken>()).Returns("Roastmaster");
        storage.GetItemAsStringAsync(Preferences.AsrKey, Arg.Any<CancellationToken>()).Returns("on");
        var next = new Preferences(storage);
        await next.InitializeAsync();
        next.Effects.Should().BeTrue();
        next.Heat.Should().Be(TopicField.Heat.Spicy);
        next.Host.Should().Be(HostPersonaId.Roastmaster);
        next.OnDeviceAsr.Should().BeTrue();

        foreach (var (heard, used) in new (string?, string?)[] { (" the bins are yours ", "the bins are yours"), (null, null), ("   ", null) })
        {
            var js = new BunitJSInterop();
            js.Setup<string?>(AsrInterop.Transcribe).SetResult(heard);
            (await new AsrInterop(js.JSRuntime).TranscribeAsync()).Should().Be(used, "null means the model was not ready, and the server is asked");
        }

        var missing = new BunitJSInterop();
        missing.Setup<string?>(AsrInterop.Transcribe).SetException(new JSException("PoAsr is not defined"));
        (await new AsrInterop(missing.JSRuntime).TranscribeAsync()).Should().BeNull();
    }
}
