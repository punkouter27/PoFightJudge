using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Security.KeyVault.Secrets;
using PoFightJudge.Shared.Configuration;

namespace PoFightJudge.Api.Common;

/// <summary>
/// Loads only <c>PoFightJudge--*</c> secrets from the shared vault (<c>kv-poshared2</c> also holds the sibling
/// apps' secrets) and maps them under <c>PoFightJudge:</c>, so <c>PoFightJudge--AzureAd--ClientId</c> becomes
/// <c>PoFightJudge:AzureAd:ClientId</c>.
/// </summary>
public sealed class PoFightJudgeSecretManager : KeyVaultSecretManager
{
    public const string Prefix = ConfigKeys.Root + "--";

    public override bool Load(SecretProperties secret) => ShouldLoad(secret.Name);

    public override string GetKey(KeyVaultSecret secret) => MapKey(secret.Name);

    public static bool ShouldLoad(string secretName) => secretName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Pure mapping used by the override and the tests.</summary>
    public static string MapKey(string secretName)
    {
        var tail = secretName[Prefix.Length..].Replace("--", ConfigurationPath.KeyDelimiter, StringComparison.Ordinal);
        return ConfigurationPath.Combine(ConfigKeys.Root, tail);
    }
}
