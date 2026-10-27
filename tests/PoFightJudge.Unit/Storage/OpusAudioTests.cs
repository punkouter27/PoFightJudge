using System.Buffers.Binary;
using PoFightJudge.Api.Features.Fight;
using PoFightJudge.Api.Features.Storage;

namespace PoFightJudge.Unit.Storage;

/// <summary>
/// Recordings are stored in Opus and read back as PCM. What matters is that the length survives — the analysis
/// reads its timings off it and every highlight is cut by the second — and that the file is actually smaller,
/// which is the only reason to do this at all.
/// </summary>
/// <summary>
/// Recordings are stored in Opus and read back as PCM. What matters is that the length survives — the analysis
/// reads its timings off it and every highlight is cut by the second — and that the file is actually smaller,
/// which is the only reason to do this at all.
/// </summary>
public class OpusAudioTests
{
    private const int Rate = 16_000;

    /// <summary>Something speech-like rather than silence: a quiet stretch would compress no matter what.</summary>
    private static byte[] Speech(double seconds, int sampleRate = Rate)
    {
        var samples = (int)(seconds * sampleRate);
        var pcm = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var t = (double)i / sampleRate;

            // Two tones and an envelope, roughly where a voice sits.
            var value = (Math.Sin(2 * Math.PI * 220 * t) * 0.4) + (Math.Sin(2 * Math.PI * 440 * t) * 0.2);
            value *= 0.5 + (0.5 * Math.Sin(2 * Math.PI * 3 * t));
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)(value * short.MaxValue * 0.8));
        }

        return pcm;
    }

    private static TimeSpan Length(byte[] pcm, int sampleRate = Rate) => TimeSpan.FromSeconds(pcm.Length / 2.0 / sampleRate);

    [Fact]
    public void A_rate_Opus_does_not_record_at_is_refused_rather_than_quietly_resampled()
    {
        OpusAudio.IsSupportedRate(16_000).Should().BeTrue();
        OpusAudio.IsSupportedRate(44_100).Should().BeFalse();

        var act = () => OpusAudio.Encode(Speech(0.1), 44_100);

        act.Should().Throw<ArgumentOutOfRangeException>();

        // A real fight reads back too. Its size estimate once multiplied the compressed length by the byte rate in
        // an int, which went negative past ~67 KB of Opus — about twenty seconds — and failed every real analysis.
        var fight = OpusAudio.Encode(Speech(40), Rate);
        fight.Length.Should().BeGreaterThan(70_000, "long enough to have overflowed");
        var wav = OpusAudio.DecodeToWav(fight, Rate);
        TimeSpan.FromSeconds((wav.Length - WavWriter.HeaderBytes) / 2.0 / Rate).Should().BeCloseTo(TimeSpan.FromSeconds(40), TimeSpan.FromMilliseconds(200));
    }
}
