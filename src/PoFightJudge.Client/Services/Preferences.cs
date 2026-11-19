using PoFightJudge.Client.Layout.Components;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Client.Services;

/// <summary>
/// How the app plays, fixed: stage effects and the detailed analysis on, a medium spin, the Referee hosting, and
/// spoken turns transcribed by the server (no 41 MB model in the browser). These used to be Settings switches; they
/// are no longer choices, so nothing is read from or written to storage. <c>js/theme.js</c>'s
/// <c>PoMotion.still()</c> holds the same effects default for the drawn effects.
/// </summary>
public sealed class Preferences
{
    public bool Effects { get; } = true;

    public TopicField.Heat Heat { get; } = TopicField.Heat.Medium;

    public HostPersonaId Host { get; } = HostPersonaId.Referee;

    public bool OnDeviceAsr { get; }

    public bool Detailed { get; } = true;
}
