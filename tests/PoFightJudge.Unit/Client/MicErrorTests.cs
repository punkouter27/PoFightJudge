using PoFightJudge.Client.Services;

namespace PoFightJudge.Unit.Client;

/// <summary>
/// A microphone that would not open is told in words somebody can act on, the same on every page that opens one.
/// "AbortError" used to reach the page as itself; it is two different problems, and each now says which.
/// </summary>
public class MicErrorTests
{
    [Fact]
    public void Each_failure_says_what_to_do_about_it()
    {
        foreach (var (error, advice) in new[]
        {
            ("NotAllowedError", "Allow it for this site"), ("NotFoundError", "No microphone was found"),
            ("NotReadableError", "Something else is holding the microphone"), ("AbortError", "would not start"),
            ("AudioLoadError", "Reload the page"), ("SomethingNew", "(SomethingNew)"),
        })
        {
            MicError.Describe(error).Should().Contain(advice);
        }

        MicError.Describe("AbortError").Should().NotContain("(AbortError)", "nobody can act on the browser's own name for it");
    }
}
