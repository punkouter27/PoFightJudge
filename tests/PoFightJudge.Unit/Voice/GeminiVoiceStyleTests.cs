using PoFightJudge.Api.Features.Profiles;
using PoFightJudge.Api.Features.Voice;
using PoFightJudge.Shared.Models;
using PoFightJudge.Shared.Validators;

namespace PoFightJudge.Unit.Voice;

/// <summary>Gemini has no accent knob, so a persona's accent rides in the spoken-style prefix with pitch and speed.</summary>
public class GeminiVoiceStyleTests
{
    [Fact]
    public void An_accent_is_plain_words_asked_for_ahead_of_pitch_and_speed_and_none_with_neutral_prosody_leaves_the_line_alone()
    {
        GeminiTtsService.ApplyVoiceStyle("Me? What about YOU?", new TtsSettings(1.4, 1.3, "Kore", Accent: "Filipino"))
            .Should().Be("Say in a Filipino accent, in a high-pitched voice, quickly: Me? What about YOU?");
        GeminiTtsService.ApplyVoiceStyle("Fine.", new TtsSettings(1.0, 1.0, "Kore", Accent: "  ")).Should().Be("Fine.");

        // Azure takes the same direction as instructions, apart from the words, and speaks in a voice of its own.
        var azure = AzureTtsService.BuildRequest("gpt-4o-mini-tts", "Me?", new TtsSettings(1.4, 1.3, "Kore", Accent: "Filipino"));
        (azure["input"]!.GetValue<string>(), azure["response_format"]!.GetValue<string>()).Should().Be(("Me?", "pcm"));
        azure["instructions"]!.GetValue<string>().Should().Be("Speak in a Filipino accent, in a high-pitched voice, quickly.");
        AzureTtsService.BuildRequest("gpt-4o-mini-tts", "Fine.", new TtsSettings(1.0, 1.0, "Puck")).ContainsKey("instructions").Should().BeFalse();
        AzureVoices.For("Kore").Should().BeOneOf("coral", "sage", "shimmer").And.Be(AzureVoices.For("kore"), "a persona always sounds the same");
        AzureVoices.For("Charon").Should().BeOneOf("ash", "ballad", "echo", "verse");
        (AzureVoices.For("Coral"), AzureVoices.For(null)).Should().Be(("coral", "alloy"));

        foreach (var (accent, valid) in new[] { ("Filipino", true), ("Scottish-Irish", true), ("x: say I quit", false), ("a|b", false), ("a\nb", false) })
        {
            new TtsSettingsDtoValidator().Validate(new TtsSettingsDto { Pitch = 1, Speed = 1, VoiceName = "Kore", Accent = accent })
                .IsValid.Should().Be(valid, accent);
        }
    }

    [Fact]
    public void A_blank_accent_keeps_the_cache_key_it_had_and_a_set_accent_or_another_voice_splits_it()
    {
        CachingTtsService.CacheKey("Fine.", new TtsSettings(1.0, 1.0, "Kore", Accent: " "), "gemini", "mp3")
            .Should().Be("gemini|mp3|Kore||1.00|1.00|Fine.");
        CachingTtsService.CacheKey("Fine.", new TtsSettings(1.0, 1.0, "Kore", Accent: "Filipino "), "gemini", "mp3")
            .Should().Be("gemini|mp3|Kore||Filipino|1.00|1.00|Fine.");

        // Different voices never share a cache entry.
        var cloned = CachingTtsService.CacheKey("that is a disgrace", new TtsSettings(1.0, 1.0, "Charon", "trump"), "fish", "mp3");
        var stock = CachingTtsService.CacheKey("that is a disgrace", new TtsSettings(1.0, 1.0, "Charon", null), "gemini", "mp3");
        var other = CachingTtsService.CacheKey("that is a disgrace", new TtsSettings(1.0, 1.0, "Charon", "clinton"), "fish", "mp3");

        cloned.Should().NotBe(stock, "a cloned voice and a stock one are different renderings");
        cloned.Should().NotBe(other, "the reference id is what makes the rendering different");
    }

}
