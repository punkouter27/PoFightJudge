using PoFightJudge.Api.Features.Fight;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Api.Features.Analysis;

/// <summary>
/// Maps diarizer labels (spk_1, spk_2, …) onto Player1/Player2 by overlap with the orchestrator's turn timeline.
/// Labels that mostly fall outside any player turn are treated as the host's voice bleeding into the mic and dropped —
/// only when there was a host. A fight recorded with no host (a phone's offline recording) has nobody to bleed in, so
/// every voice is kept and mapped by overlap alone.
/// </summary>
public static class SpeakerMapper
{
    private const double HostBleedThreshold = 0.25;

    public static MappedTranscript Map(TranscriptDto transcript, IReadOnlyList<TurnDto> turns, bool hostOnMic = true)
    {
        if (transcript.Words.Count == 0)
        {
            return new MappedTranscript([], true, "Diarizer returned no words.");
        }

        var playerTurns = turns.Where(t => t.Kind is TurnKind.Talk or TurnKind.Probe && t.Speaker is Speaker.Player1 or Speaker.Player2).ToList();
        var labels = transcript.Words.GroupBy(w => w.Label, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var assignment = new Dictionary<string, Speaker?>(StringComparer.Ordinal);
        var notes = new List<string>();

        foreach (var (label, words) in labels)
        {
            var total = words.Sum(w => Math.Max(0, w.End - w.Start));
            var p1 = Overlap(words, playerTurns, Speaker.Player1);
            var p2 = Overlap(words, playerTurns, Speaker.Player2);
            var inTurns = p1 + p2;

            if (hostOnMic && playerTurns.Count > 0 && total > 0 && inTurns / total < HostBleedThreshold)
            {
                assignment[label] = null;
                notes.Add($"{label} ignored ({words.Count} words mostly outside player turns — likely the host).");
                continue;
            }

            assignment[label] = p1 >= p2 ? Speaker.Player1 : Speaker.Player2;
        }

        var kept = assignment.Where(a => a.Value is not null).ToList();
        var flagged = kept.Count != 2 || kept.Select(k => k.Value).Distinct().Count() != 2;

        if (playerTurns.Count == 0 && kept.Count >= 2)
        {
            // No timeline at all: give the two loudest labels distinct players in order of first appearance.
            var ordered = kept.OrderBy(k => labels[k.Key].Min(w => w.Start)).Select(k => k.Key).ToList();
            assignment[ordered[0]] = Speaker.Player1;
            assignment[ordered[1]] = Speaker.Player2;
            for (var i = 2; i < ordered.Count; i++)
            {
                assignment[ordered[i]] = Speaker.Player2;
            }

            notes.Add("No turn timeline; players assigned by order of first speech.");
            flagged = true;
        }
        else if (kept.Count >= 2 && kept.Select(k => k.Value).Distinct().Count() == 1)
        {
            // Everyone mapped to the same player: split by who overlaps that player less.
            var target = kept[0].Value!.Value;
            var other = target.Other();
            var weakest = kept.OrderBy(k => Overlap(labels[k.Key], playerTurns, target)).First().Key;
            assignment[weakest] = other;
            notes.Add($"{weakest} reassigned to {other} to keep two distinct voices.");
        }

        if (kept.Count > 2)
        {
            notes.Add($"{kept.Count} voices detected; extras merged into the nearest player.");
        }

        var mapped = transcript.Words
            .Where(w => assignment[w.Label] is not null)
            .Select(w => new MappedWord(w.Text, assignment[w.Label]!.Value, w.Start, w.End))
            .ToList();

        return new MappedTranscript(mapped, flagged, notes.Count == 0 ? "Speakers matched the turn timeline." : string.Join(" ", notes));
    }

    private static double Overlap(IEnumerable<TranscriptWord> words, IEnumerable<TurnDto> turns, Speaker speaker)
    {
        var relevant = turns.Where(t => t.Speaker == speaker).ToList();
        double total = 0;
        foreach (var w in words)
        {
            foreach (var t in relevant)
            {
                var end = t.EndSeconds ?? double.MaxValue;
                total += Math.Max(0, Math.Min(w.End, end) - Math.Max(w.Start, t.StartSeconds));
            }
        }

        return total;
    }
}
