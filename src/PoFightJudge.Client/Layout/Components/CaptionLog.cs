using PoFightJudge.Shared.Models;

namespace PoFightJudge.Client.Layout.Components;

/// <summary>
/// The running caption of a fight. A speaker's line arrives in pieces and is rewritten as it grows, so the log
/// replaces the line still being spoken rather than appending each fragment; a final caption closes that line and
/// the next one starts fresh.
/// </summary>
public sealed class CaptionLog
{
    /// <summary>Older lines fall off the top: a long fight would otherwise grow the page without limit.</summary>
    public const int MaxLines = 120;

    private readonly List<CaptionDto> _lines = [];

    public IReadOnlyList<CaptionDto> Lines => _lines;

    /// <summary>
    /// Bumped on every change. The live API rewrites the line being spoken several times a second, and the page
    /// that owns this log has a phase banner, a clock, a host indicator and a scoreboard on it that none of those
    /// fragments touch — the feed watches this instead, and redraws alone.
    /// </summary>
    public int Version { get; private set; }

    /// <summary>Raised after <see cref="Version"/> moves. Handlers run on whatever thread added the line.</summary>
    public event EventHandler? Changed;

    public void Add(CaptionDto caption)
    {
        ArgumentNullException.ThrowIfNull(caption);
        if (string.IsNullOrWhiteSpace(caption.Text))
        {
            return;
        }

        // The line to extend is this speaker's own last line, if they have not finished it — not simply the last
        // line in the log. A live session interleaves: the host's transcript grows while a player is still being
        // transcribed, and looking only at the end of the log printed each of them again every time it grew.
        var open = LastLineOf(caption.Speaker);
        if (open >= 0 && !_lines[open].Final)
        {
            _lines[open] = caption;
        }
        else
        {
            _lines.Add(caption);
        }

        if (_lines.Count > MaxLines)
        {
            _lines.RemoveRange(0, _lines.Count - MaxLines);
        }

        Bump();
    }

    public void Clear()
    {
        _lines.Clear();
        Bump();
    }

    private void Bump()
    {
        Version++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Where this speaker last appears, or -1 if they have not spoken yet.</summary>
    private int LastLineOf(Speaker speaker)
    {
        for (var i = _lines.Count - 1; i >= 0; i--)
        {
            if (_lines[i].Speaker == speaker)
            {
                return i;
            }
        }

        return -1;
    }
}
