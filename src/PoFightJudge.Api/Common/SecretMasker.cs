namespace PoFightJudge.Api.Common;

/// <summary>
/// Masking helpers shared by the diagnostics surfaces (/api/diag and /api/health/details). Centralised so a new
/// diagnostic view cannot accidentally ship an unmasked secret — every caller goes through the same code path.
/// </summary>
public static class SecretMasker
{
    /// <summary>"Configured" / "NotConfigured" for presence-only reporting.</summary>
    public static string Presence(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Shared.Models.DiagDto.NotConfigured : Shared.Models.DiagDto.Configured;
}
