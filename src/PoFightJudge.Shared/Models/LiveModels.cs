using PoFightJudge.Shared.Identifiers;

namespace PoFightJudge.Shared.Models;

/// <summary>
/// Method names on the live hub, shared so the browser and the server cannot drift apart on a spelling. The first
/// three are what the room calls; the rest are what it is told.
/// </summary>
public static class LiveHubContract
{
    public const string JoinFight = "JoinFight";
    public const string AudioIn = "AudioIn";
    public const string EndFight = "EndFight";

    public const string AudioOut = "AudioOut";
    public const string AudioClear = "AudioClear";
    public const string Caption = "Caption";
    public const string Snapshot = "Snapshot";
    public const string Ended = "Ended";
    public const string Error = "Error";

    /// <summary>Client to server: tell me when the reading of this fight moves on. Only its owner may ask.</summary>
    public const string WatchAnalysis = "WatchAnalysis";

    /// <summary>Server to client: the reading has reached a new stage. Carries the stage's name; the page asks for the rest.</summary>
    public const string AnalysisStatus = "AnalysisStatus";
}

/// <summary>
/// Body of a request to start a fight: who is hosting, both fighter tags, and what they have agreed to argue about.
/// Both tags are required, because a fight goes on both their records and a record needs a name.
/// </summary>
public sealed record CreateFightRequest(
    HostPersonaId Persona = HostPersonaId.Referee,
    string Player1Tag = "",
    string Player2Tag = "",
    string? Topic = null,
    ProfileRole Player1Role = ProfileRole.Husband,
    ProfileRole Player2Role = ProfileRole.Wife,
    Stance? Player1Stance = null);

/// <summary>The fight that was started. Its id is what the browser joins the hub with.</summary>
public sealed record CreateFightResponse(MatchId MatchId);

/// <summary>
/// One line of live caption. <paramref name="Final"/> marks the end of a turn: everything before it is the same
/// sentence still being spoken, so the room replaces rather than appends.
/// </summary>
public sealed record CaptionDto(Speaker Speaker, string Text, bool Final);

/// <summary>
/// One word of a transcript, with the speaker label it was attributed to and where it sits in the recording.
/// Labels keep the <c>label_n</c> shape a diarizer produces, so the same mapper reads both sources.
/// </summary>
public sealed record TranscriptWord(string Text, string Label, double Start, double End);

/// <summary>A whole transcript: the words in order, and the plain text they spell out.</summary>
public sealed record TranscriptDto(string Text, IReadOnlyList<TranscriptWord> Words);

/// <summary>Who held the floor when, in a fight recorded without a host: seconds from the start of the recording.</summary>
public sealed record RecordedTurnDto(Speaker Speaker, double StartSeconds, double EndSeconds);

/// <summary>
/// The form field that travels with a recorded fight's audio (multipart <c>meta</c>, JSON). The phone picks
/// <see cref="MatchId"/> itself so an upload retried after a timeout lands on the same fight instead of a second one.
/// </summary>
public sealed record RecordedFightRequest(
    MatchId MatchId,
    string Player1Tag,
    string Player2Tag,
    DateTimeOffset RecordedAt,
    IReadOnlyList<RecordedTurnDto> Turns,
    string? Player1Name = null,
    string? Player2Name = null,
    string? Topic = null,
    Stance? Player1Stance = null,
    ProfileRole Player1Role = ProfileRole.Husband,
    ProfileRole Player2Role = ProfileRole.Wife);
