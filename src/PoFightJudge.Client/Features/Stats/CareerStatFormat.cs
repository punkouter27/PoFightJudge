using System.Globalization;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Client.Features.Stats;

/// <summary>
/// The career numbers, described once. <see cref="StatFormat"/> does the same job for a single debate; this one
/// reads a whole history, so every rate is per hundred words and nothing here is a total that rewards turning up.
/// </summary>
public static class CareerStatFormat
{
    public static IReadOnlyList<Stat> Describe(CareerWordsDto words)
    {
        ArgumentNullException.ThrowIfNull(words);

        return
        [
            new("Reading grade", Number(words.ReadingGrade), "Roughly the school year needed to follow them.", StatGroup.Words),
            new("Reading grade trend", Trend(words.GradeTrend), "Their latest fight against their first.", StatGroup.Words),
            new("Vocabulary", Count(words.VocabularySize), "Different words they have ever used.", StatGroup.Words),
            new("New words last time", Count(words.NewWordsLatest), "Words in their latest fight they had never used before.", StatGroup.Words),
            new("Catchphrases", List(words.Catchphrases), "Phrases they have brought to more than one argument.", StatGroup.Words),
            new("Words on the record", Count(words.Words), $"Across {Fights(words.Debates)}.", StatGroup.Words),

            new("Blame", Per100(words.BlamePer100), "\"You always\", \"you never\", \"your fault\".", StatGroup.Manner),
            new("Apologies", Per100(words.ApologyPer100), "\"Sorry\", \"my fault\", \"you're right\".", StatGroup.Manner),
            new("Name-calling", Per100(words.NameCallingPer100), "Aimed at the person, not at what they did.", StatGroup.Manner),
            new("Swearing", Percent(words.OffensivePercent), "Of every word said. Counted, not judged.", StatGroup.Manner),
            new("Receipts", Per100(words.ReceiptsPer100), "Numbers, days and times — arguing with specifics.", StatGroup.Manner),
            new("Intensity", Per100(words.IntensifierPer100), "\"Very\", \"absolutely\", \"totally\".", StatGroup.Manner),
            new("Questions with an edge", Split(words.Challenges, words.GenuineQuestions), "Challenges against questions that wanted an answer.", StatGroup.Manner),
            new("Where they argue from", Tense(words), "How the past, present and future language splits.", StatGroup.Manner),
        ];
    }

    /// <summary>
    /// Where their reading grade has got to, and which way it moved. A sparkline was the obvious thing, but the one
    /// this app has is scaled for 0-100 scores and a grade of nine would sit flat on the floor of it.
    /// </summary>
    private static string Trend(IReadOnlyList<double> grades)
    {
        if (grades.Count == 0)
        {
            return "—";
        }

        var latest = grades[^1];
        if (grades.Count == 1)
        {
            return Number(latest);
        }

        var change = Math.Round(latest - grades[0], 1);
        return change switch
        {
            > 0.2 => $"{Number(latest)}, up {Number(change)}",
            < -0.2 => $"{Number(latest)}, down {Number(Math.Abs(change))}",
            _ => $"{Number(latest)}, steady",
        };
    }

    /// <summary>Which way they face, in words. Three percentages in a row is a table nobody reads.</summary>
    private static string Tense(CareerWordsDto w)
    {
        if (w.PastShare + w.PresentShare + w.FutureShare <= 0)
        {
            return "—";
        }

        var leading = new[] { ("the past", w.PastShare), ("right now", w.PresentShare), ("what happens next", w.FutureShare) }
            .OrderByDescending(p => p.Item2)
            .First();

        return $"{leading.Item1} ({Percent(leading.Item2 * 100)})";
    }

    private static string Split(int challenges, int genuine) =>
        challenges + genuine == 0 ? "—" : $"{challenges} vs {genuine}";

    private static string Fights(int debates) =>
        debates == 1 ? "one fight" : $"{debates.ToString(CultureInfo.CurrentCulture)} fights";

    private static string Per100(double value) =>
        value <= 0 ? "none" : $"{Number(value)} per 100";

    private static string Percent(double value) =>
        value <= 0 ? "none" : $"{value.ToString("0.#", CultureInfo.CurrentCulture)}%";

    private static string Number(double value) => value.ToString("0.#", CultureInfo.CurrentCulture);

    private static string Count(int value) => value.ToString("N0", CultureInfo.CurrentCulture);

    private static string List(IReadOnlyList<string> values) =>
        values.Count == 0 ? "—" : string.Join(", ", values);
}
