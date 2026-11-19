using System.Buffers.Binary;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Carter;
using PoFightJudge.Api.Features.Auth;
using PoFightJudge.Api.Features.Fighters;
using PoFightJudge.Api.Features.Records;
using PoFightJudge.Api.Features.Storage;
using PoFightJudge.Api.Features.Watch;
using PoFightJudge.Shared;
using PoFightJudge.Shared.Identifiers;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Api.Features.Fight;

/// <summary>
/// A fight recorded on a phone with no host — no signal, or no wish for one — and uploaded afterwards. It is stored
/// exactly as a live fight stores its players' track and goes through the same analysis (transcribe, judge, fact
/// check, records), with the phone's own turn timeline standing in for the host's so each voice lands on the right
/// fighter. The phone chooses the match id, so an upload retried after a timeout answers with the fight it already made.
/// </summary>
public sealed class RecordedFightEndpoints : ICarterModule
{
    /// <summary>Thirty-odd minutes of 16 kHz mono 16-bit PCM.</summary>
    public const long MaxBytes = 64L * 1024 * 1024;

    public const int MaxTurns = 2_000;

    public void AddRoutes(IEndpointRouteBuilder app) =>
        app.MapGroup(ApiRoutes.Fights.Base).RequireAuthorization().WithTags("Fights")
            .MapPost(ApiRoutes.Fights.RecordedSegment, UploadAsync)
            .RequireRateLimiting(WatchServiceExtensions.AiPolicy)
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(MaxBytes + (1024 * 1024)))
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<CreateFightResponse>(StatusCodes.Status201Created)
            .Produces<CreateFightResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

    private static async Task<IResult> UploadAsync(
        HttpRequest http,
        ClaimsPrincipal user,
        IMatchRepository matches,
        IFighterRepository fighters,
        IAudioBlobStore blobs,
        IAnalysisIntake analysis,
        DebateOptions options,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!http.HasFormContentType)
        {
            return Problem("Send the recording as multipart/form-data with an 'audio' file and a 'meta' field.");
        }

        IFormCollection form = await http.ReadFormAsync(ct);
        RecordedFightRequest? meta;
        try
        {
            meta = form["meta"].FirstOrDefault() is { Length: > 0 } json ? JsonSerializer.Deserialize<RecordedFightRequest>(json, JsonSerializerOptions.Web) : null;
        }
        catch (JsonException)
        {
            meta = null;
        }

        if (meta is null || meta.MatchId.Value is null || meta.MatchId.Value.Length is < 8 or > 64)
        {
            return Problem("The 'meta' field is missing or unreadable.");
        }

        if (!Initials.AreNewPair(meta.Player1Tag, meta.Player2Tag))
        {
            return Problem("Both fighters need their own tag: exactly three letters, and not the same as each other.");
        }

        string userId = user.UserId();
        if (await matches.GetAsync(userId, meta.MatchId, ct) is { } existing)
        {
            return Results.Ok(new CreateFightResponse(existing.Id)); // a retried upload: already have it
        }

        if (form.Files.GetFile("audio") is not { Length: > 0 } file || file.Length > MaxBytes)
        {
            return Problem($"The 'audio' file is missing or larger than {MaxBytes / (1024 * 1024)} MB.");
        }

        byte[] wav = new byte[file.Length];
        await using (Stream body = file.OpenReadStream())
        {
            await body.ReadExactlyAsync(wav, ct);
        }

        if (Pcm16Mono(wav, DebateOrchestrator.PlayerSampleRate) is not { } pcm)
        {
            return Problem($"The recording must be a WAV of 16-bit mono PCM at {DebateOrchestrator.PlayerSampleRate} Hz.");
        }

        double seconds = pcm.Length / 2.0 / DebateOrchestrator.PlayerSampleRate;
        List<RecordedTurnDto> turns = [.. meta.Turns.Take(MaxTurns)
            .Where(t => t.Speaker is Speaker.Player1 or Speaker.Player2 && double.IsFinite(t.StartSeconds) && double.IsFinite(t.EndSeconds) && t.EndSeconds > t.StartSeconds)
            .Select(t => t with { StartSeconds = Math.Clamp(t.StartSeconds, 0, seconds), EndSeconds = Math.Clamp(t.EndSeconds, 0, seconds) })];

        string first = Initials.Normalize(meta.Player1Tag), second = Initials.Normalize(meta.Player2Tag);
        DateTimeOffset now = clock.GetUtcNow();
        await fighters.EnsureAsync(FighterId.From(first), now, meta.Player1Role, ct);
        await fighters.EnsureAsync(FighterId.From(second), now, meta.Player2Role, ct);

        // Same storage as a live fight's players' track: Opus unless the options keep WAV.
        string blobName;
        if (options.StoreOpus)
        {
            await using MemoryStream encoded = new(OpusAudio.Encode(pcm, DebateOrchestrator.PlayerSampleRate), writable: false);
            blobName = await blobs.UploadAsync(IAudioBlobStore.PlayersTrack(meta.MatchId, OpusAudio.Extension), encoded, OpusAudio.ContentType, ct);
        }
        else
        {
            await using MemoryStream raw = new(wav, writable: false);
            blobName = await blobs.UploadAsync(IAudioBlobStore.PlayersTrack(meta.MatchId), raw, "audio/wav", ct);
        }

        string? topic = string.IsNullOrWhiteSpace(meta.Topic) ? null : DebateSession.Clean(meta.Topic.Trim(), DebateSession.MaxShortText, string.Empty);
        DateTimeOffset started = meta.RecordedAt > now || meta.RecordedAt < now.AddDays(-30) ? now.AddSeconds(-seconds) : meta.RecordedAt;
        MatchDto match = new(
            meta.MatchId,
            userId,
            MatchMode.Fight,
            started,
            started.AddSeconds(seconds),
            topic ?? string.Empty,
            MatchSide.Human(first, Clean(meta.Player1Name)),
            MatchSide.Human(second, Clean(meta.Player2Name)),
            SessionPhase.Done,
            SessionStatus.Analyzing,
            string.Empty,
            null,
            false)
        {
            AudioBlobName = blobName,
            Side1Stance = topic is null ? null : meta.Player1Stance,
        };

        await matches.UpsertAsync(match, ct);
        if (turns.Count > 0)
        {
            await matches.SaveTurnsAsync(meta.MatchId, turns.Select((t, i) => new TurnDto(meta.MatchId, i, t.Speaker, TurnKind.Talk, string.Empty)
            {
                StartSeconds = t.StartSeconds,
                EndSeconds = t.EndSeconds,
            }), ct);
        }

        await matches.SaveAnalysisAsync(new AnalysisRecordDto(meta.MatchId, AnalysisStatus.Queued, null, null, now), ct);
        await analysis.SubmitAsync(userId, meta.MatchId, ct);
        return Results.Created(ApiRoutes.Fights.ById(meta.MatchId), new CreateFightResponse(meta.MatchId));
    }

    private static string? Clean(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : DebateSession.Clean(name.Trim(), 40, string.Empty);

    private static IResult Problem(string detail) => Results.Problem(detail, statusCode: StatusCodes.Status400BadRequest);

    /// <summary>The PCM samples of a canonical WAV (RIFF/WAVE, PCM format 1, one channel, 16-bit, at the given rate); null otherwise.</summary>
    internal static byte[]? Pcm16Mono(byte[] wav, int sampleRate)
    {
        ReadOnlySpan<byte> span = wav;
        if (span.Length < 44 || !span[..4].SequenceEqual("RIFF"u8) || !span.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            return null;
        }

        bool formatOk = false;
        int at = 12;
        while (at + 8 <= span.Length)
        {
            ReadOnlySpan<byte> id = span.Slice(at, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(at + 4, 4));
            int start = at + 8;
            if (size < 0 || start + size > span.Length)
            {
                size = span.Length - start; // a recorder that never patched its sizes: take the rest
            }

            if (id.SequenceEqual("fmt "u8) && size >= 16)
            {
                formatOk = BinaryPrimitives.ReadInt16LittleEndian(span.Slice(start, 2)) == 1
                    && BinaryPrimitives.ReadInt16LittleEndian(span.Slice(start + 2, 2)) == 1
                    && BinaryPrimitives.ReadInt32LittleEndian(span.Slice(start + 4, 4)) == sampleRate
                    && BinaryPrimitives.ReadInt16LittleEndian(span.Slice(start + 14, 2)) == 16;
            }
            else if (id.SequenceEqual("data"u8))
            {
                return formatOk && size >= 2 ? span.Slice(start, size & ~1).ToArray() : null;
            }

            at = start + size + (size & 1);
        }

        return null;
    }
}
