using Bunit;
using Microsoft.JSInterop;
using PoFightJudge.Client.Layout.Components;
using PoFightJudge.Client.Services;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Unit.Client;

/// <summary>
/// How the app plays is fixed, not chosen: stage effects and the detailed analysis on, a medium spin, the Referee
/// hosting, spoken turns read by the server. The transcriber is kept as a shortcut that is never a dependency:
/// anything short of words sends the clip to the server.
/// </summary>
public class PreferencesTests
{
    [Fact]
    public async Task How_the_app_plays_is_fixed_and_the_transcriber_never_blocks()
    {
        var prefs = new Preferences();
        prefs.Effects.Should().BeTrue();
        prefs.Detailed.Should().BeTrue();
        prefs.Heat.Should().Be(TopicField.Heat.Medium);
        prefs.Host.Should().Be(HostPersonaId.Referee);
        prefs.OnDeviceAsr.Should().BeFalse("it downloads a forty-megabyte model");

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
