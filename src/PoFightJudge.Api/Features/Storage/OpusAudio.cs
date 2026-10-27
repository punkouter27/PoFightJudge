using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;
using PoFightJudge.Api.Features.Fight;

namespace PoFightJudge.Api.Features.Storage;

/// <summary>
/// Recordings, in Opus. A fight is two people talking for a few minutes, and speech at 16 kHz costs about
/// 2 MB a minute as PCM in a WAV and about a tenth of that as Opus — which is what a hobby storage account and a
/// browser fetching a highlight both care about.
/// </summary>
/// <remarks>
/// Everything the app itself works with stays 16-bit mono PCM: the analysis measures it, the slicer cuts it, and
/// the browser plays clips of it. Opus is the storage format alone, decoded on the way back out.
/// </remarks>
public static class OpusAudio
{
    /// <summary>The extension and content type a stored recording carries.</summary>
    public const string Extension = "opus";

    public const string ContentType = "audio/ogg";

    /// <summary>
    /// Opus works in whole frames of 2.5, 5, 10, 20, 40 or 60 ms. Twenty is what the codec is tuned around for
    /// speech, and it divides every sample rate this app records at.
    /// </summary>
    public const int FrameMilliseconds = 20;

    /// <summary>
    /// Enough for speech that has to stay intelligible for a transcriber, and far below what the WAV costs. Opus
    /// is variable-rate, so quiet stretches cost less than this rather than the same.
    /// </summary>
    public const int BitsPerSecond = 24_000;

    /// <summary>Sample rates Opus encodes natively. Anything else would have to be resampled first.</summary>
    public static bool IsSupportedRate(int sampleRate) => sampleRate is 8_000 or 12_000 or 16_000 or 24_000 or 48_000;

    /// <summary>Whether a blob is stored in Opus, judged by the name the orchestrator gave it.</summary>
    public static bool IsOpus(string? blobName) =>
        blobName?.EndsWith("." + Extension, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>16-bit mono PCM in, an Ogg/Opus file out.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> pcm16, int sampleRate)
    {
        if (!IsSupportedRate(sampleRate))
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Opus records at 8, 12, 16, 24 or 48 kHz.");
        }

        var samples = new short[pcm16.Length / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)(pcm16[i * 2] | (pcm16[(i * 2) + 1] << 8));
        }

        using var output = new MemoryStream();
        using var encoder = OpusCodecFactory.CreateEncoder(sampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        encoder.Bitrate = BitsPerSecond;

        // leaveOpen, because the stream is read back out of the buffer once the writer has finished with it.
        var ogg = new OpusOggWriteStream(encoder, output, inputSampleRate: sampleRate, leaveOpen: true);

        // Whole frames only. The tail is padded with silence rather than dropped: a recording that ends mid-frame
        // would come back a few milliseconds short, and the analysis reads its own timings off these lengths.
        var frame = sampleRate / 1000 * FrameMilliseconds;
        var written = 0;
        while (written < samples.Length)
        {
            var take = Math.Min(frame, samples.Length - written);
            if (take == frame)
            {
                ogg.WriteSamples(samples, written, frame);
            }
            else
            {
                var padded = new short[frame];
                Array.Copy(samples, written, padded, 0, take);
                ogg.WriteSamples(padded, 0, frame);
            }

            written += take;
        }

        ogg.Finish();
        return output.ToArray();
    }

    /// <summary>An Ogg/Opus file back to 16-bit mono PCM at the rate asked for.</summary>
    public static byte[] Decode(byte[] ogg, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(ogg);
        if (!IsSupportedRate(sampleRate))
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Opus decodes to 8, 12, 16, 24 or 48 kHz.");
        }

        using var input = new MemoryStream(ogg);
        using var decoder = OpusCodecFactory.CreateDecoder(sampleRate, 1);
        var reader = new OpusOggReadStream(decoder, input);

        using var pcm = DecodeTo(ogg, sampleRate, reserveHeader: 0);
        return pcm.ToArray();
    }

    /// <summary>
    /// The same recording as a WAV, for everything downstream that reads one.
    /// </summary>
    /// <remarks>
    /// Decoded straight into the buffer the WAV is returned in. Going through <see cref="Decode"/> built the audio
    /// three times over — the decoder's own growing stream, the array <c>ToArray</c> copied out of it, and the WAV
    /// built around that — which on a fifteen-minute fight is about 86 MB of transient allocation for 28 MB of
    /// sound, on a plan with a gigabyte and two analyses allowed at once.
    /// </remarks>
    public static byte[] DecodeToWav(byte[] ogg, int sampleRate)
    {
        using var buffer = DecodeTo(ogg, sampleRate, reserveHeader: WavWriter.HeaderBytes);
        var wav = buffer.GetBuffer();
        var length = (int)buffer.Length;

        WavWriter.WriteHeader(wav.AsSpan(0, WavWriter.HeaderBytes), sampleRate, length - WavWriter.HeaderBytes);

        // GetBuffer hands back the whole capacity, which is rarely the length; the WAV is only the used part.
        return wav.Length == length ? wav : wav.AsSpan(0, length).ToArray();
    }

    /// <summary>
    /// Decodes into one stream, leaving <paramref name="reserveHeader"/> bytes at the front for a caller that wants
    /// to write a header in front of the samples without copying them again.
    /// </summary>
    private static MemoryStream DecodeTo(byte[] ogg, int sampleRate, int reserveHeader)
    {
        ArgumentNullException.ThrowIfNull(ogg);
        if (!IsSupportedRate(sampleRate))
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Opus decodes to 8, 12, 16, 24 or 48 kHz.");
        }

        using var input = new MemoryStream(ogg);
        using var decoder = OpusCodecFactory.CreateDecoder(sampleRate, 1);
        var reader = new OpusOggReadStream(decoder, input);

        // A frame is at most 60 ms; the buffer is sized for the largest one the format allows.
        var buffer = new short[sampleRate / 1000 * 60];

        // Sized up front from what the compressed side weighs, so the stream is not reallocated and recopied a
        // dozen times on the way to its final size. Being a little wrong costs one resize; being absent costs all
        // of them.
        // In long: the product passes int.MaxValue at about 67 KB of Opus, twenty seconds of a fight.
        var expected = reserveHeader + ((long)ogg.Length * sampleRate * 2 / Math.Max(1, BitsPerSecond / 8));
        var pcm = new MemoryStream((int)Math.Min(expected, Array.MaxLength));
        pcm.SetLength(reserveHeader);
        pcm.Position = reserveHeader;

        var bytes = new byte[buffer.Length * 2];
        while (reader.HasNextPacket)
        {
            var packet = reader.ReadNextRawPacket();
            if (packet is null || packet.Length == 0)
            {
                continue;
            }

            var decoded = decoder.Decode(packet, buffer, buffer.Length, false);
            for (var i = 0; i < decoded; i++)
            {
                bytes[i * 2] = (byte)(buffer[i] & 0xFF);
                bytes[(i * 2) + 1] = (byte)((buffer[i] >> 8) & 0xFF);
            }

            pcm.Write(bytes, 0, decoded * 2);
        }

        return pcm;
    }
}
