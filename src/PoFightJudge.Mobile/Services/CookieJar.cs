using System.Net;

namespace PoFightJudge.Mobile.Services;

/// <summary>
/// Keeps the API's session cookie (the Debug guest sign-in) in SecureStorage, so it survives an
/// app restart and a background geofence wake-up. Bearer sign-ins don't use it.
/// </summary>
public sealed class CookieJar(CookieContainer cookies, MobileConfig config)
{
    private const string Key = "pofightjudge_session_cookies";

    private Uri Base => new(config.BaseUrl);

    public async Task RestoreAsync()
    {
        string? saved = await SecureStorage.Default.GetAsync(Key);
        foreach (string pair in (saved ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                cookies.Add(Base, new Cookie(pair[..eq], pair[(eq + 1)..]));
            }
        }
    }

    public Task SaveAsync()
        => SecureStorage.Default.SetAsync(Key, string.Join('\n', cookies.GetCookies(Base).Select(c => $"{c.Name}={c.Value}")));

    public void Clear()
    {
        foreach (Cookie cookie in cookies.GetCookies(Base))
        {
            cookie.Expired = true;
        }

        SecureStorage.Default.Remove(Key);
    }
}
