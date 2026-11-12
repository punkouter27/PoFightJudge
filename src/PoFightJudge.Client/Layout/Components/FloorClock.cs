using PoFightJudge.Shared.Models;

namespace PoFightJudge.Client.Layout.Components;

/// <summary>
/// The live share of the room. The browser keeps a running total of seconds the microphone heard a voice, frame by
/// frame (<c>PoLive.voiced</c>); each snapshot hands what was voiced since the last one to whoever held the floor
/// through it — unless the host was talking. Floor time on its own would be useless: the host hands the floor out
/// in even slices, and a snapshot comes once a second, far too seldom to sample a voice by.
/// </summary>
public sealed class FloorClock
{
    /// <summary>The most one snapshot's span can credit. They come once a second; a longer gap is a stall, and whose turn it was through one is anybody's guess.</summary>
    public static readonly TimeSpan LongestGap = TimeSpan.FromSeconds(2);

    private Speaker? _holder;
    private bool _counting;
    private double? _voiced;

    /// <summary>Seconds fighter one has spent talking with the floor.</summary>
    public double One { get; private set; }

    public double Two { get; private set; }

    /// <summary>Fighter one's part of everything said so far, 0 to 1. Level until anybody has said anything.</summary>
    public double Share => One + Two <= 0 ? 0.5 : One / (One + Two);

    /// <summary>
    /// One snapshot's worth: who has the floor, whether the host is talking, and the browser's running total of voiced
    /// seconds. What was voiced since the last tick is credited to the state the last tick described.
    /// </summary>
    public void Tick(Speaker? speaking, bool hostSpeaking, double voicedSeconds)
    {
        if (_voiced is { } before && _counting)
        {
            var spoken = Math.Clamp(voicedSeconds - before, 0, LongestGap.TotalSeconds);
            if (_holder == Speaker.Player1)
            {
                One += spoken;
            }
            else
            {
                Two += spoken;
            }
        }

        _holder = speaking;
        _counting = !hostSpeaking && speaking is Speaker.Player1 or Speaker.Player2;
        _voiced = voicedSeconds;
    }
}
