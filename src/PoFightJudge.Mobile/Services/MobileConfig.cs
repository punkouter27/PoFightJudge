namespace PoFightJudge.Mobile.Services;

public sealed class MobileConfig
{
    private const string BaseUrlKey = "pofightjudge_api_base_url";
    private const string ClientIdKey = "pofightjudge_entra_client_id";
    private const string TenantIdKey = "pofightjudge_entra_tenant_id";
    private const string ScopeKey = "pofightjudge_entra_scope";
    private readonly IPreferences _preferences = Preferences.Default;

    public string BaseUrl
    {
        get => _preferences.Get(BaseUrlKey, GetDefaultBaseUrl());
        set => _preferences.Set(BaseUrlKey, NormalizeUrl(value));
    }

    public string EntraClientId
    {
        get => _preferences.Get(ClientIdKey, string.Empty);
        set => _preferences.Set(ClientIdKey, (value ?? string.Empty).Trim());
    }

    public string EntraTenantId
    {
        get => _preferences.Get(TenantIdKey, "common");
        set => _preferences.Set(TenantIdKey, string.IsNullOrWhiteSpace(value) ? "common" : value.Trim());
    }

    public string EntraScope
    {
        get => _preferences.Get(ScopeKey, "openid profile email");
        set => _preferences.Set(ScopeKey, string.IsNullOrWhiteSpace(value) ? "openid profile email" : value.Trim());
    }

    public static string RedirectUri => "pofightjudge://auth";

    public const string ProductionHost = "app-pofightjudge2.azurewebsites.net";

    /// <summary><c>pofightjudge://auth</c> (sign-in), <c>pofightjudge://match/{id}</c> (a ruling).</summary>
    public const string AppScheme = "pofightjudge";

    /// <summary>Debug builds may use the API's guest cookie (Development/Test only).</summary>
    public static bool GuestAllowed =>
#if DEBUG
        true;
#else
        false;
#endif

    // Debug: scripts/mobile-dev.ps1 runs `adb reverse tcp:5000 tcp:<api port>`; Release talks to production.
    public static string GetDefaultBaseUrl() =>
#if DEBUG
        DeviceInfo.Current.Platform == DevicePlatform.Android ? "http://localhost:5000" : "https://localhost:5001";
#else
        $"https://{ProductionHost}";
#endif

    private static string NormalizeUrl(string? url)
    {
        var trimmed = (url ?? string.Empty).Trim().TrimEnd('/');
        return string.IsNullOrWhiteSpace(trimmed) ? GetDefaultBaseUrl() : trimmed;
    }
}
