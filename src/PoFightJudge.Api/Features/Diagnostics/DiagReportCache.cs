using PoFightJudge.Shared.Diagnostics;

namespace PoFightJudge.Api.Features.Diagnostics;

/// <summary>
/// The anonymous <c>/api/diag</c> report, cached for <see cref="Lifetime"/>. Every probe is a cheap, unauthenticated
/// reachability call (DiagnosticsServiceExtensions), but the page is public and the host is on the F1 plan, so a
/// refresh-happy visitor (or a crawler) must not turn into a probe storm: within the lifetime every caller, concurrent
/// ones included, shares the one report and its <see cref="DiagReportDto.CheckedAt"/>. A failed build is not kept.
/// </summary>
public sealed class DiagReportCache(TimeProvider clock)
{
    /// <summary>How long one set of probes answers for.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    private readonly Lock _gate = new();
    private Task<DiagReportDto>? _report;
    private DateTimeOffset _expires;

    /// <summary>
    /// The cached report, or a new one from <paramref name="build"/> once the last has expired. The build runs without
    /// the caller's token, so one visitor closing the tab cannot cancel the report every other visitor is waiting on.
    /// </summary>
    public Task<DiagReportDto> GetAsync(Func<CancellationToken, Task<DiagReportDto>> build, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(build);
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            if (_report is null || now >= _expires || _report.IsFaulted || _report.IsCanceled)
            {
                _report = build(CancellationToken.None);
                _expires = now + Lifetime;
            }

            return _report.WaitAsync(ct);
        }
    }
}
