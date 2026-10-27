using System.Text.Json.Serialization;
using PoFightJudge.Shared.Identifiers;

namespace PoFightJudge.Shared.Models;

/// <summary>
/// Which host runs a fight. The system instruction lives server-side; only what the picker shows is shared. The
/// referee is first, and therefore the default: a couple who picks nothing gets the host who calls it straight.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<HostPersonaId>))]
public enum HostPersonaId
{
    Referee,
    Puck,
    JudgeStern,
    Coach,
    Roastmaster,
}

/// <summary>What the host picker shows for one persona. <paramref name="ThemeClass"/> themes the live and verdict pages.</summary>
public sealed record HostPersonaDto(HostPersonaId Id, string Name, string Emoji, string Tagline, string ThemeClass);

/// <summary>The hosts on offer. Shared so the picker needs no round trip, and validated against the same list server-side.</summary>
public static class HostPersonaCatalogue
{
    public static IReadOnlyList<HostPersonaDto> All { get; } =
    [
        new(HostPersonaId.Referee, "The Referee", "\U0001F9D1‍⚖️", "Even-handed and unhurried. Has heard this argument before.", "persona-referee"),
        new(HostPersonaId.Puck, "Puck", "\U0001F3A4", "Game-show energy. Fast, funny, keeps the room warm.", "persona-puck"),
        new(HostPersonaId.JudgeStern, "Judge Stern", "⚖️", "Dry and severe. Treats your opinions as sworn testimony.", "persona-stern"),
        new(HostPersonaId.Coach, "Coach", "\U0001F4E3", "Relentlessly encouraging. Finds the good in every bad argument.", "persona-coach"),
        new(HostPersonaId.Roastmaster, "Roastmaster", "\U0001F525", "Merciless. Will roast you both and enjoy it.", "persona-roast"),
    ];

    public static HostPersonaDto For(HostPersonaId id) => All.FirstOrDefault(p => p.Id == id) ?? All[0];

    public static bool IsKnown(HostPersonaId id) => All.Any(p => p.Id == id);
}

/// <summary>The ruling: who argued better, who was righter, and who took it overall.</summary>
public sealed record VerdictDto(Speaker WinnerLogic, Speaker WinnerCorrect, Speaker Overall, IReadOnlyList<string> Reasons);

/// <summary>What the room sees while a fight is happening.</summary>
public sealed record DebateSnapshotDto(
    MatchId MatchId,
    SessionPhase Phase,
    string Topic,
    string Player1Name,
    string Player2Name,
    Speaker? Speaking,
    int DebateElapsedSeconds,
    int DebateRemainingSeconds,
    VerdictDto? Verdict,
    HostPersonaId Persona = HostPersonaId.Referee,

    /// <summary>
    /// Seconds since the match was created. The recorded track is padded to this clock, so anything timestamped in
    /// the browser has to be shifted by it before it lines up with the turn timeline.
    /// </summary>
    int SessionElapsedSeconds = 0,

    /// <summary>Whether fighter one is for the topic or against it (fighter two is the opposite); null without sides.</summary>
    Stance? Player1Stance = null,

    /// <summary>The fallacies the host has called so far, oldest first; null before the first.</summary>
    IReadOnlyList<LiveFlagDto>? Flags = null);

/// <summary>A fallacy the host called out loud during the fight: who, which one, and the words it was in.</summary>
public sealed record LiveFlagDto(Speaker Who, string Fallacy, string Quote);
