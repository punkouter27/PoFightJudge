using System.Reflection;

namespace PoFightJudge.Client.Services;

/// <summary>
/// Which build this is, as the UTC date of its HEAD commit (YYMMDD). The PoBuildStamp target in Directory.Build.props
/// stamps it into every assembly at build time (<c>AssemblyMetadata("BuildVersion", …)</c>), so the server and the
/// client carry the same value and it is never typed by hand.
/// </summary>
public static class BuildStamp
{
    public static string Version { get; } =
        typeof(BuildStamp).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, "BuildVersion", StringComparison.Ordinal))?.Value ?? "dev";
}
