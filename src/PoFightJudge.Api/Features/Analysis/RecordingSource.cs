using PoFightJudge.Api.Features.Fight;
using PoFightJudge.Api.Features.Storage;

namespace PoFightJudge.Api.Features.Analysis;

/// <summary>
/// The recording on its way to the Files API: a readable stream of 16-bit PCM in a WAV, and the length that has to
/// travel with it because a resumable upload declares its size before it sends any bytes.
/// </summary>
public sealed class Recording(Stream content, long length) : IAsyncDisposable
{
    public Stream Content { get; } = content;

    public long Length { get; } = length;

    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

/// <summary>
/// Opens a stored recording as the WAV the model reads.
/// </summary>
/// <remarks>
/// A fifteen-minute fight is about 28 MB as 16 kHz mono PCM, two analyses may run at once, and the F1 plan has a
/// gigabyte. This used to copy the blob into a MemoryStream that doubles as it grows, call <c>ToArray</c> on it and
/// hand the copy on — two full copies alive at once, and alive for the length of the whole analysis rather than
/// the upload, because the local outlived the call that used it.
///
/// Opus is stored on disk and PCM is what everything reads, so the two cases are genuinely different. A WAV needs
/// no decoding and therefore need not be in memory at all: the blob's own stream goes straight to the upload. Opus
/// does — a decoder needs the whole file — but it is about a tenth the size going in, and the decoded side is now
/// built once, into a buffer sized for it, rather than three times.
/// </remarks>
public static class RecordingSource
{
    public static async Task<Recording> OpenAsync(IAudioBlobStore blobs, string blobName, int sampleRate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(blobs);

        var stored = await blobs.OpenReadAsync(blobName, ct)
            ?? throw new InvalidOperationException("The recording is missing from storage.");

        if (!OpusAudio.IsOpus(blobName))
        {
            // Already exactly what the model reads. A blob stream knows its own length, so there is nothing here
            // worth copying; only a stream that cannot say how long it is has to be measured first.
            if (stored.CanSeek)
            {
                stored.Position = 0;
                return new Recording(stored, stored.Length);
            }

            await using (stored)
            {
                var measured = new MemoryStream();
                await stored.CopyToAsync(measured, ct);
                measured.Position = 0;
                return new Recording(measured, measured.Length);
            }
        }

        await using (stored)
        {
            // The compressed side is the tenth, so buffering this is the cheap half of the job.
            var compressed = new MemoryStream(stored.CanSeek ? (int)stored.Length : 0);
            await stored.CopyToAsync(compressed, ct);
            var wav = OpusAudio.DecodeToWav(compressed.ToArray(), sampleRate);
            return new Recording(new MemoryStream(wav, writable: false), wav.Length);
        }
    }
}
