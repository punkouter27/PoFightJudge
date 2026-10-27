namespace PoFightJudge.Api.Common;

/// <summary>
/// Masking helpers shared by the diagnostics surfaces (/api/diag and /api/health/details). Centralised so a new
/// diagnostic view cannot accidentally ship an unmasked secret — every caller goes through the same code path.
/// </summary>
public static class SecretMasker
{
    /// <summary>
    /// Masks a URI down to a partially obscured host (<c>https://kv-p****ared.vault.azure.net/</c>). Returns
    /// <c>****</c> when the input is not a parseable absolute URI.
    /// </summary>
    public static string MaskHost(string? uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return "****";
        }

        var host = parsed.Host;
        return host.Length <= 8 ? "****" : $"{parsed.Scheme}://{host[..4]}{new string('*', host.Length - 8)}{host[^4..]}/";
    }

    /// <summary>"Configured" / "NotConfigured" for presence-only reporting.</summary>
    public static string Presence(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Shared.Models.DiagDto.NotConfigured : Shared.Models.DiagDto.Configured;
}
