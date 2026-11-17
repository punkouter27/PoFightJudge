namespace PoFightJudge.Shared.Models;

/// <summary>Outcome of a single health probe.</summary>
public enum HealthState
{
    /// <summary>Reachable / configured as expected.</summary>
    Ok,

    /// <summary>Optional dependency is absent — the app still runs.</summary>
    NotConfigured,

    /// <summary>Reachable but not in a fully healthy state.</summary>
    Degraded,

    /// <summary>Required dependency is unreachable or unconfigured.</summary>
    Failed,
}

/// <summary>Grouping used by the /diag page to lay out its sections.</summary>
public enum HealthCategory
{
    /// <summary>A data store the app reads and writes (Table and Blob storage), probed live.</summary>
    Storage,

    /// <summary>An external dependency: AI provider, Key Vault, sign-in, required settings.</summary>
    Service,

    /// <summary>Presence (never the raw value) of configuration.</summary>
    Configuration,

    /// <summary>Runtime feature flags.</summary>
    Feature,
}

/// <summary>
/// One row of the health report. <see cref="Detail"/> is plain English written server-side — never a secret, a URI,
/// a path or an exception message. <see cref="Purpose"/> says in one line what the dependency is for, and
/// <see cref="LatencyMs"/> is how long a live probe took (null for a presence-only row).
/// </summary>
public sealed record HealthCheckDto(string Name, HealthState State, string Detail, HealthCategory Category, string? Purpose = null, double? LatencyMs = null);

/// <summary>Full connection/configuration status, rendered by the /diag page. Anonymous, so presence only.</summary>
public sealed record HealthReportDto(string Environment, HealthState Overall, DateTimeOffset Timestamp, IReadOnlyList<HealthCheckDto> Checks);

/// <summary>Model ids in use per operation, as reported by /api/diag.</summary>
public sealed record ModelIdsDto(string Round, string Judge, string Profile, string Tts, string Live, string Transcribe, string TurnTranscribe, string Voice);

/// <summary>
/// What <c>/api/diag</c> reports: environment, readiness, provider presence and the model ids in use. Presence only —
/// never a secret value. Both ends read this one record, so a field added here cannot go missing on the other side.
/// </summary>
public sealed record DiagDto(
    string Environment,
    string Status,
    IReadOnlyList<string> MissingKeys,
    bool FakeAi,
    string Gemini,
    string TableStorage,
    string BlobStorage,
    ModelIdsDto Models,
    IReadOnlyDictionary<string, bool> Flags,
    IReadOnlyList<AiLatencyDto> Latency,
    IReadOnlyList<AiUsageDto> Usage)
{
    public const string Configured = "Configured";
    public const string NotConfigured = "NotConfigured";

    /// <summary>What the client needs before it will let anyone start a match.</summary>
    public bool Ready => string.Equals(Status, "ok", StringComparison.Ordinal) && (FakeAi || string.Equals(Gemini, Configured, StringComparison.Ordinal));
}

/// <summary>Flags returned by <c>GET /api/features</c> — the effective values, after environment rules.</summary>
public sealed record FeatureFlagsDto(bool UseFakeAi, bool DevGuestEnabled, bool HumanInWatch, bool FishVoices);

/// <summary>One AI operation's rolling latency summary (window of recent calls, milliseconds).</summary>
public sealed record AiLatencyDto(string Operation, int Count, double P50Ms, double P95Ms, double LastMs);

/// <summary>One AI operation's cumulative token usage since process start.</summary>
public sealed record AiUsageDto(string Operation, int Calls, long PromptTokens, long OutputTokens, long CachedTokens)
{
    /// <summary>Share of prompt tokens served from the provider's context cache — the scoreboard for cache-friendly prompt ordering.</summary>
    public double CacheHitRatio => PromptTokens == 0 ? 0 : (double)CachedTokens / PromptTokens;

    /// <summary>Mean prompt tokens per call — the number a prompt trim is supposed to move.</summary>
    public double AveragePromptTokens => Calls == 0 ? 0 : (double)PromptTokens / Calls;
}
