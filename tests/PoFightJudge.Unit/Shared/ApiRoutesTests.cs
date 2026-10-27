using System.Reflection;
using PoFightJudge.Shared;
using PoFightJudge.Shared.Identifiers;

namespace PoFightJudge.Unit.Shared;

/// <summary>
/// ApiRoutes is the single source of truth for every path. These tests keep it honest: every constant is a rooted
/// path, absolute URLs never collide, and the helpers escape what they interpolate.
/// </summary>
/// <summary>
/// ApiRoutes is the single source of truth for every path. These tests keep it honest: every constant is a rooted
/// path, absolute URLs never collide, and the helpers escape what they interpolate.
/// </summary>
public class ApiRoutesTests
{
    private static IEnumerable<(string Name, string Value)> AllConstants(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => ($"{type.Name}.{f.Name}", (string)f.GetRawConstantValue()!))
            .Concat(type.GetNestedTypes(BindingFlags.Public).SelectMany(AllConstants));

    [Fact]
    public void Absolute_urls_are_unique_and_the_helpers_build_the_paths_the_server_maps()
    {
        var absolute = AllConstants(typeof(ApiRoutes))
            .Where(c => c.Value.StartsWith("/api", StringComparison.Ordinal) || c.Value.StartsWith("/auth", StringComparison.Ordinal) || c.Value.StartsWith("/hubs", StringComparison.Ordinal) || c.Value.StartsWith("/health", StringComparison.Ordinal))
            .Where(c => !c.Name.EndsWith("Segment", StringComparison.Ordinal))
            .ToList();

        absolute.Select(c => c.Value).Should().OnlyHaveUniqueItems();

        var profile = ProfileId.From("KDH");
        var fighter = FighterId.From("MLT");
        var match = MatchId.From("0123456789abcdef0123456789abcdef");

        ApiRoutes.Profiles.ById(profile).Should().Be("/api/profiles/KDH");
        ApiRoutes.Profiles.Face(profile).Should().Be("/api/profiles/KDH/face");
        ApiRoutes.Fighters.ByTag(fighter).Should().Be("/api/fighters/MLT");
        ApiRoutes.Fights.ById(match).Should().Be($"/api/fights/{match.Value}");
        ApiRoutes.Fights.Analysis(match).Should().Be($"/api/fights/{match.Value}/analysis");
        ApiRoutes.Fights.Clip(match, 2).Should().Be($"/api/fights/{match.Value}/clips/2");
        ApiRoutes.Matches.ById(match).Should().Be($"/api/matches/{match.Value}");
        ApiRoutes.Audio.Round(match, 3).Should().Be($"/api/audio/{match.Value}/3");
        // The two sides travel in the body, not the path: a side is a persona or a live person, and a route segment
        // cannot say which without inventing an encoding for it.
        ApiRoutes.Watch.GenerateRoundUrl.Should().Be("/api/watch/generate-round");
        ApiRoutes.Watch.VerdictUrl.Should().Be("/api/watch/verdict");
        ApiRoutes.Hubs.Live.Should().Be("/hubs/live");
    }
}
