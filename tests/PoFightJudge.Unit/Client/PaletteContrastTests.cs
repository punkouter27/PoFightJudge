using System.Globalization;
using System.Text.RegularExpressions;

namespace PoFightJudge.Unit.Client;

/// <summary>
/// Reads the design tokens straight out of <c>app.css</c> and checks the colour pairs the UI actually renders, in
/// BOTH themes. Contrast is a property of the palette, not of a screenshot, so it belongs in the suite: change a token
/// to something unreadable and this fails before anyone sees it.
/// </summary>
public partial class PaletteContrastTests
{
    private const double TextMinimum = 4.5;      // WCAG AA, body text
    private const double NonTextMinimum = 3.0;   // WCAG AA, focus rings and UI boundaries
    private const double OutlineMinimum = 1.9;   // Decorative card outlines: visible separation, not a WCAG rule

    private static readonly Lazy<(IReadOnlyDictionary<string, string> Light, IReadOnlyDictionary<string, string> Dark)> Themes = new(Load);

    /// <summary>Every colour pair the UI renders, with the floor it has to clear and what it is used for.</summary>
    private static readonly (string Foreground, string Background, double Minimum, string Use)[] Pairs =
    [
        ("--po-fg", "--po-bg", TextMinimum, "body text on the page"),
        ("--po-fg", "--po-surface", TextMinimum, "body text on a card"),
        ("--po-fg", "--po-surface-2", TextMinimum, "body text on a raised panel"),
        ("--po-muted", "--po-bg", TextMinimum, "secondary text"),
        ("--po-muted", "--po-surface", TextMinimum, "secondary text on a card"),
        ("--po-muted", "--po-surface-2", TextMinimum, "secondary text on a raised panel"),
        ("--po-muted", "--po-surface-3", TextMinimum, "a stat that did not win"),
        ("--po-accent-text", "--po-surface", TextMinimum, "links, eyebrows and the wordmark"),
        ("--po-accent-text", "--po-bg", TextMinimum, "links on the page"),
        ("--po-accent-fg", "--po-accent", TextMinimum, "text on an accent button or chip"),
        ("--po-p1", "--po-surface", TextMinimum, "fighter one's tag"),
        ("--po-p2", "--po-surface", TextMinimum, "fighter two's tag"),
        ("--po-p1", "--po-bg", TextMinimum, "fighter one's tag on the stage"),
        ("--po-p2", "--po-bg", TextMinimum, "fighter two's tag on the stage"),
        ("--po-fg-invert", "--po-p1", TextMinimum, "text on a fighter-one chip"),
        ("--po-fg-invert", "--po-p2", TextMinimum, "text on a fighter-two chip"),
        ("--po-danger", "--po-surface", TextMinimum, "error text"),
        ("--po-danger-fg", "--po-danger", TextMinimum, "text on the End fight button and the LIVE chip"),
        ("--po-success", "--po-surface", TextMinimum, "verified claims"),
        ("--po-warning", "--po-surface", TextMinimum, "unverifiable claims"),
        ("--po-focus", "--po-bg", NonTextMinimum, "the focus ring"),
        ("--po-border", "--po-surface", OutlineMinimum, "card outlines"),
        ("--po-tier-ink", "--po-tier-bronze", TextMinimum, "a Bronze badge"),
        ("--po-tier-ink", "--po-tier-silver", TextMinimum, "a Silver badge"),
        ("--po-tier-ink", "--po-tier-gold", TextMinimum, "a Gold badge"),
        ("--po-tier-ink", "--po-tier-platinum", TextMinimum, "a Platinum badge"),
        ("--po-tier-ink", "--po-tier-diamond", TextMinimum, "a Diamond badge"),
    ];

    [Fact]
    public void Palette_tokens_are_defined_and_meet_contrast_ratios()
    {
        var (light, dark) = Themes.Value;
        var required = Pairs.SelectMany(p => new[] { p.Foreground, p.Background }).Distinct();

        dark.Keys.Should().Contain(required);
        light.Keys.Should().Contain(required, "every colour is one light-dark() declaration, so both halves exist");

        var failures = new List<string>();
        foreach (var (name, tokens) in new[] { ("light", light), ("dark", dark) })
        {
            foreach (var (foreground, background, minimum, use) in Pairs)
            {
                var ratio = Contrast(tokens[foreground], tokens[background]);
                if (ratio < minimum)
                {
                    failures.Add($"{name}: {foreground} on {background} ({use}) is {ratio:F2}:1, needs {minimum:F1}:1");
                }
            }
        }

        failures.Should().BeEmpty();
    }

    private static (IReadOnlyDictionary<string, string> Light, IReadOnlyDictionary<string, string> Dark) Load()
    {
        var css = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "PoFightJudge.Client", "wwwroot", "css", "po-tokens.css"));
        var light = new Dictionary<string, string>(StringComparer.Ordinal);
        var dark = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in LightDarkToken().Matches(css))
        {
            light[m.Groups["name"].Value] = m.Groups["light"].Value;
            dark[m.Groups["name"].Value] = m.Groups["dark"].Value;
        }

        return (light, dark);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PoFightJudge.slnx")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the tests run from inside the repository");
        return directory!.FullName;
    }

    private static double Contrast(string first, string second)
    {
        var (a, b) = (Luminance(first), Luminance(second));
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double Luminance(string hex)
    {
        var value = hex.TrimStart('#');
        if (value.Length == 3)
        {
            value = string.Concat(value.Select(c => new string(c, 2)));
        }

        double Channel(int offset)
        {
            var raw = int.Parse(value.AsSpan(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return raw <= 0.03928 ? raw / 12.92 : Math.Pow((raw + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(0)) + (0.7152 * Channel(2)) + (0.0722 * Channel(4));
    }

    /// <summary>Only hex pairs are checked; rgba() soft tints are fills behind other tokens, never text.</summary>
    [GeneratedRegex(@"(?<name>--po-[\w-]+):\s*light-dark\(\s*(?<light>#[0-9a-fA-F]{3,6})\s*,\s*(?<dark>#[0-9a-fA-F]{3,6})\s*\)", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 2000)]
    private static partial Regex LightDarkToken();
}
