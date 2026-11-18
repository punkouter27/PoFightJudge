using System.Globalization;
using PoFightJudge.Client.Services;

namespace PoFightJudge.Unit.Client;

/// <summary>
/// The build version in the corner (vYYMMDD) is the UTC date of the commit it was built from, stamped by the build
/// itself (PoBuildStamp, Directory.Build.props) — so a screenshot or a bug report says which build it came from without
/// anybody having typed a number.
/// </summary>
public class BuildStampTests
{
    [Fact]
    public void The_build_is_stamped_with_its_commit_day_as_yymmdd()
    {
        BuildStamp.Version.Should().MatchRegex(@"^\d{6}$");
        DateTime.TryParseExact(BuildStamp.Version, "yyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            .Should().BeTrue("it is a date, not just six digits");
    }
}
