using System.Globalization;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Client.Features.Stats;

/// <summary>One measured thing, ready to put on screen.</summary>
/// <param name="Label">What it is called, in words rather than a field name.</param>
/// <param name="Value">The number, already formatted.</param>
/// <param name="Hint">What it means, for anyone who wonders why it is here.</param>
/// <param name="Group">Which section of the page it belongs in.</param>
public sealed record Stat(string Label, string Value, string Hint, StatGroup Group);

/// <summary>The three things measurements are about: the shape of the talking, the words, and the manner.</summary>
public enum StatGroup
{
    Floor,
    Words,
    Manner,
}

/// <summary>
/// Turns the measured half of a report into something readable. Every field is described here, in one place: a
/// number that was measured and then never shown is a number nobody can act on, and one that quietly disappears
/// when the contract changes is worse.
/// </summary>
public static class StatFormat
{
    public static IReadOnlyList<Stat> Describe(PlayerMetricsDto metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);

        return
        [
            new("Time talking", Seconds(metrics.TalkSeconds), "How long they held the floor in total.", StatGroup.Floor),
            new("Share of the room", Percent(metrics.TalkShare), "Of everything said, how much was theirs.", StatGroup.Floor),
            new("Turns", Count(metrics.Turns), "How many times they were given the floor.", StatGroup.Floor),
            new("Average turn", Seconds(metrics.AverageTurnSeconds), "How long they went on for, typically.", StatGroup.Floor),
            new("Longest turn", Seconds(metrics.LongestTurnSeconds), "Their longest single stretch without stopping.", StatGroup.Floor),
            new("Pauses", Count(metrics.Pauses), "Gaps long enough to notice mid-sentence.", StatGroup.Floor),
            new("Average pause", Seconds(metrics.MeanPauseSeconds), "How long they left it, typically.", StatGroup.Floor),
            new("Longest pause", Seconds(metrics.LongestPauseSeconds), "The longest they left it.", StatGroup.Floor),
            new("Interruptions made", Count(metrics.InterruptionsMade), "Times they started while the other one was still going.", StatGroup.Floor),
            new("Interruptions taken", Count(metrics.InterruptionsReceived), "Times the other one did it to them.", StatGroup.Floor),
            new("Talking over each other", Seconds(metrics.OverlapSeconds), "How long both of them were speaking at once.", StatGroup.Floor),
            new("Cut off by the host", Count(metrics.HostInterrupts), "Times the host stepped in on them.", StatGroup.Floor),

            new("Words", Count(metrics.Words), "How many words they actually said.", StatGroup.Words),
            new("Pace", $"{Number(metrics.WordsPerMinute)} wpm", "Words per minute while they were talking.", StatGroup.Words),
            new("Vocabulary range", Ratio(metrics.TypeTokenRatio), "How much of what they said was a word they had not already used.", StatGroup.Words),
            new("Word length", Number(metrics.MeanWordLength), "Letters per word, on average.", StatGroup.Words),
            new("Syllables per word", Number(metrics.SyllablesPerWord), "A rough measure of how heavy the words were.", StatGroup.Words),
            new("Sentence length", Number(metrics.MeanSentenceLength), "Words per sentence, on average.", StatGroup.Words),
            new("Reading ease", Number(metrics.FleschReadingEase), "Higher is plainer speech.", StatGroup.Words),
            new("Reading grade", Number(metrics.FleschKincaidGrade), "Roughly the school year needed to follow it.", StatGroup.Words),
            new("Longest word", Text(metrics.LongestWord), "The longest thing they said in one go.", StatGroup.Words),
            new("Words they lean on", List(metrics.TopWords), "The ones they came back to most.", StatGroup.Words),
            new("Phrases they repeat", List(metrics.RepeatedPhrases), "Whole phrases said more than once.", StatGroup.Words),

            new("Fillers", $"{Number(metrics.FillersPer100)} per 100", "Ums, likes and you-knows, per hundred words.", StatGroup.Manner),
            new("Hedges", Count(metrics.Hedges), "\"I think\", \"maybe\", \"sort of\".", StatGroup.Manner),
            new("Absolutes", Count(metrics.Absolutes), "\"always\", \"never\", \"everyone\".", StatGroup.Manner),
            new("Questions", Count(metrics.Questions), "Times they asked rather than told.", StatGroup.Manner),
            new("Swearing", Count(metrics.Profanity), "Counted, not judged.", StatGroup.Manner),
            new("Talking about themselves", Percent(metrics.FirstPersonRatio), "How much of it was I, me and my.", StatGroup.Manner),
            new("Talking about the other one", Percent(metrics.SecondPersonRatio), "How much of it was you and your.", StatGroup.Manner),
        ];
    }

    /// <summary>The one-to-ten judgements, which read as bars rather than numbers.</summary>
    /// <summary>
    /// The WATCH measurements, which are a different set from the fight's: they are the judge's read of a persona
    /// rather than anything measured off a waveform. Described here so both kinds of number live in one place.
    /// </summary>
    public static IReadOnlyList<Stat> Describe(AdvancedStatsDto stats)
    {
        ArgumentNullException.ThrowIfNull(stats);

        return
        [
            new("Passive aggression", Number(stats.PassiveAggressionIndex), "How much was meant rather than said.", StatGroup.Manner),
            new("Old grievances", Number(stats.HistoricalGrievanceRate), "How often something from years ago came up.", StatGroup.Manner),
            new("Blame", Number(stats.BlameMetric), "How much of it was the other one's fault.", StatGroup.Manner),
            new("Fallacies", Count(stats.LogicalFallacyCount), "Arguments that did not follow.", StatGroup.Words),
            new("Volume", Number(stats.VolumeScore), "How loudly it was put.", StatGroup.Floor),
            new("Share of words", Number(stats.WordCountDominance), "How much of the talking they did.", StatGroup.Floor),
            new("Volatility", Number(stats.EmotionalVolatility), "How far the mood swung.", StatGroup.Manner),
            new("Deflection", Number(stats.DeflectionCoefficient), "How often the question was answered with another one.", StatGroup.Manner),
            new("Vocabulary", Number(stats.LexicalComplexity), "How elaborate the words were.", StatGroup.Words),
            new("Sorry to insults", Number(stats.ApologyToInsultRatio), "Apologies against the other thing.", StatGroup.Words),
        ];
    }

    public static IReadOnlyList<(string Label, int Score, string Hint)> Traits(PlayerAssessmentDto assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);

        return
        [
            ("Logic", assessment.Logic, "Did the argument hold together?"),
            ("Clarity", assessment.Clarity, "Could you follow it?"),
            ("Evidence", assessment.EvidenceUse, "Did they back anything up?"),
            ("Rebuttal", assessment.RebuttalQuality, "Did they answer what was actually said?"),
            ("Persuasiveness", assessment.Persuasiveness, "Would it move anybody?"),
            ("Listening", assessment.Listening, "Did they take the other one in at all?"),
            ("Confidence", assessment.Confidence, "How sure they sounded."),
            ("Politeness", assessment.Politeness, "How they said it."),
            ("Aggression", assessment.Aggression, "How hard they came at it."),
            ("Vocabulary", assessment.VocabularySophistication, "The range of words they reached for."),
        ];
    }

    public static string Seconds(double value) =>
        value >= 60
            ? string.Create(CultureInfo.CurrentCulture, $"{(int)(value / 60)}m {(int)(value % 60)}s")
            : string.Create(CultureInfo.CurrentCulture, $"{value:0.#}s");

    public static string Percent(double share) => string.Create(CultureInfo.CurrentCulture, $"{Math.Round(share * 100)}%");

    public static string Number(double value) => string.Create(CultureInfo.CurrentCulture, $"{value:0.#}");

    public static string Count(int value) => value.ToString(CultureInfo.CurrentCulture);

    /// <summary>What an argument was about, or that nobody set it: a blank cell reads as a fault.</summary>
    public static string Topic(string? topic) => string.IsNullOrWhiteSpace(topic) ? "an open topic" : topic;

    private static string Ratio(double value) => string.Create(CultureInfo.CurrentCulture, $"{value:0.00}");

    private static string Text(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    private static string List(IReadOnlyList<string> values) => values.Count == 0 ? "—" : string.Join(", ", values);
}
