namespace PoFightJudge.Shared.Models;

/// <summary>Model ids in use per operation, as reported by /api/diag/admin.</summary>
public sealed record ModelIdsDto(string Round, string Judge, string Profile, string Tts, string Live, string Transcribe, string TurnTranscribe, string Voice);

/// <summary>
/// What <c>/api/diag/admin</c> reports: environment, readiness, provider presence and the model ids in use. Presence only —
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
