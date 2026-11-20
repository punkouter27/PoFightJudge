using System.Text.RegularExpressions;
using PoFightJudge.Shared.Identifiers;

namespace PoFightJudge.Shared.Models;

/// <summary>
/// One heckle the spectator can throw into a live argument. The client renders buttons and the server builds its
/// prompt directive from this same list, so a new interjection is one entry here rather than an edit on both sides.
/// </summary>
/// <param name="Key">Wire value sent on the generate-round request.</param>
/// <param name="Icon">Emoji shown on the button.</param>
/// <param name="Label">Short button caption.</param>
/// <param name="Tooltip">Plain-English description of the effect.</param>
/// <param name="PromptDirective">Line injected into the prompt's current-state block.</param>
public sealed record Interjection(string Key, string Icon, string Label, string Tooltip, string PromptDirective);

/// <summary>The interjection catalogue, shared by the play page and the prompt builder so the two can never drift.</summary>
public static class Interjections
{
    /// <summary>Key of the interrupting slap — the one interjection with special turn handling.</summary>
    public const string SlapKey = "slap";

    public static IReadOnlyList<Interjection> All { get; } =
    [
        new(
            Key: SlapKey,
            Icon: "👋",
            Label: "SLAP",
            Tooltip: "Interrupt the speaker mid-sentence — they react furiously and the opponent's turn is skipped. Once per match.",
            PromptDirective: "⚠ They were just SLAPPED across the face and are FURIOUS — open with outrage at being struck, then escalate hard."),
    ];

    /// <summary>Resolves a wire key to its definition; null for an unknown or absent key, so an old client simply gets no directive.</summary>
    public static Interjection? Find(string? key) =>
        string.IsNullOrWhiteSpace(key) ? null : All.FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Argument analytics for one speaker, computed from what they actually said. Every value is 0–100 except the raw fallacy count.</summary>
public sealed record AdvancedStatsDto(
    double PassiveAggressionIndex,
    double HistoricalGrievanceRate,
    double BlameMetric,
    int LogicalFallacyCount,
    double VolumeScore,
    double WordCountDominance,
    double EmotionalVolatility,
    double DeflectionCoefficient,
    double LexicalComplexity,
    double ApologyToInsultRatio)
{
    public static AdvancedStatsDto Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}

/// <summary>One line of a WATCH match as the client holds it. Audio travels separately so a round can render before it can speak.</summary>
public sealed record WatchRoundDto(string Speaker, string Text, string Mood)
{
    /// <summary>Set once the round's audio has been fetched; the format is whatever the provider chain actually returned.</summary>
    public TtsAudioDto? Audio { get; init; }
}

/// <summary>
/// Asks for the next line. The client drives the loop, so it sends the whole state rather than the server holding a
/// session. Each side is a persona or a live person; the server only ever generates a line for a persona, because a
/// person speaks for themselves.
/// </summary>
public sealed record GenerateRoundRequest(
    MatchSide Husband,
    MatchSide Wife,
    IReadOnlyList<WatchRoundDto> History,
    string Speaker,
    string? Topic = null,
    string? Interjection = null,
    bool WasSlapped = false,
    Stance? HusbandStance = null);

/// <summary>One generated line plus the attitude it was asked for, so the UI can show why it landed the way it did.</summary>
public sealed record GenerateRoundResponse(string Text, string Mood, string Attitude, bool IsFake);

/// <summary>Asks for the audio of a line that has already been generated.</summary>
/// <param name="Speaker">The persona whose voice speaks it; null is the judge reading the ruling back.</param>
public sealed record RoundAudioRequest(ProfileId? Speaker, string Text);

/// <summary>
/// One fragment of a line as it is being written, off the round stream. The server emits these while the model is
/// still generating, so the viewer reads the line arriving rather than watching a spinner for the whole round trip.
/// </summary>
public sealed record RoundDeltaDto(string Delta);

/// <summary>
/// One clause of a line's audio, off the audio stream. Each chunk carries its own format because the provider chain
/// can fall back mid-line, and <paramref name="Index"/> is the clause's position so playback can never reorder them.
/// </summary>
public sealed record RoundAudioChunkDto(int Index, string Base64, string Format, bool IsLast)
{
    public TtsAudioDto Audio => new(Base64, Format);
}

/// <summary>
/// One item off the round stream: a fragment while the line is being written, then exactly one final response
/// carrying the whole line. Never both — a reader switches on whichever half is set.
/// </summary>
public sealed record RoundStreamPart(string? Delta, GenerateRoundResponse? Final);

/// <summary>
/// Ends a match: the judge rules on the transcript and the whole thing is persisted. A persona side gets a watch
/// result; a person's side gets a fighter result, so their profile grows from this debate too.
/// <paramref name="MatchId"/> is only for re-judging a match the caller already owns.
/// </summary>
public sealed record VerdictRequest(MatchSide Husband, MatchSide Wife, IReadOnlyList<WatchRoundDto> Rounds, string? Topic = null, MatchId? MatchId = null, Stance? HusbandStance = null);

/// <summary>The ruling. <paramref name="Winner"/> is the winning side's id, or empty when the judge called neither.</summary>
public sealed record VerdictResponse(
    MatchId MatchId,
    string Winner,
    string Verdict,
    int HusbandScore,
    int WifeScore,
    AdvancedStatsDto HusbandStats,
    AdvancedStatsDto WifeStats,
    bool Persisted);

/// <summary>A recorded SELF turn on its way to the transcriber. The WAV is base64 because it rides in a JSON body.</summary>
public sealed record TranscribeRequest(string WavBase64);

/// <summary>What was heard. An empty <paramref name="Text"/> means nothing intelligible was said — a legitimate outcome, not an error.</summary>
public sealed record TranscribeResponse(string Text, bool IsFake);

/// <summary>
/// The turn rules of a WATCH match. They are shared because both ends need them: the server enforces them, and the
/// play screen has to know whose turn it is in order to ask for the right line. A second copy in the client is how
/// the two would drift.
/// </summary>
public static partial class WatchTurns
{
    /// <summary>Wire values for a speaker. Storage maps the pair onto Player1/Player2.</summary>
    public const string Husband = "husband";

    public const string Wife = "wife";

    /// <summary>Per-speaker round cap: the two alternate three each, six lines in all.</summary>
    public const int RoundsPerSide = 3;

    /// <summary>
    /// A slap interrupts one line and inserts an extra reaction, and it is available once per match — so a
    /// legitimate match can carry seven lines, not six.
    /// </summary>
    public const int MaxLines = (RoundsPerSide * 2) + 1;

    /// <summary>The registers a line may carry. The judge, the tension meter and the schema all read this set, so it is closed.</summary>
    public static IReadOnlyList<string> Moods { get; } =
        ["calm", "humble", "apologetic", "smug", "passive-aggressive", "frustrated", "angry", "sarcastic", "hostile", "hateful", "furious", "sad"];

    /// <summary>Maps whatever a model said onto the closed set, defaulting to the middle of the range rather than an extreme.</summary>
    public static string NormalizeMood(string? mood)
    {
        if (string.IsNullOrWhiteSpace(mood))
        {
            return "angry";
        }

        var cleaned = mood.Trim().ToLowerInvariant();
        return Moods.FirstOrDefault(m => string.Equals(m, cleaned, StringComparison.Ordinal))
            ?? Moods.FirstOrDefault(m => cleaned.Contains(m, StringComparison.Ordinal))
            ?? "angry";
    }

    /// <summary>
    /// A line as the transcript shows it: without the stage cues — "(smug)", "(seething)" — that the voice acts out.
    /// The stored line and the one sent to the voice keep them; the mood chip already says it on screen. A line that
    /// is nothing but a cue is left as it is rather than printed blank.
    /// </summary>
    public static string Shown(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return string.Empty;
        }

        var shown = StageCue().Replace(line, string.Empty).Trim();
        return shown.Length == 0 ? line.Trim() : shown;
    }

    /// <summary>A short parenthesised aside, and the space in front of it.</summary>
    [GeneratedRegex(@"\s*\([^()]{1,40}\)", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex StageCue();

    public static bool IsHusband(string? speaker) => string.Equals(speaker, Husband, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this is a side of a watch at all. There are only two, and everything downstream treats "not the
    /// husband" as the wife — so a speaker that is neither has to be refused rather than quietly become one of them.
    /// </summary>
    public static bool IsSpeaker(string? speaker) =>
        IsHusband(speaker) || string.Equals(speaker, Wife, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whose turn it is next. The husband opens and the two alternate; a slap is the one exception, because the
    /// slapped speaker reacts instead of the turn passing over.
    /// </summary>
    public static string NextSpeaker(int linesSoFar, bool afterSlap)
    {
        var evenTurn = linesSoFar % 2 == 0;
        var husbandsTurn = afterSlap ? !evenTurn : evenTurn;
        return husbandsTurn ? Husband : Wife;
    }

    /// <summary>True when both sides have had all their rounds. Counted per speaker, because a slap adds a line without advancing a round.</summary>
    public static bool IsComplete(IEnumerable<string> speakers)
    {
        ArgumentNullException.ThrowIfNull(speakers);
        var spoken = speakers as IReadOnlyList<string> ?? [.. speakers];
        return spoken.Count(IsHusband) >= RoundsPerSide && spoken.Count(s => !IsHusband(s)) >= RoundsPerSide;
    }
}
