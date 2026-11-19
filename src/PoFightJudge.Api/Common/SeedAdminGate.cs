using System.Security.Claims;
using PoFightJudge.Api.Features.Auth;
using PoFightJudge.Shared.Configuration;

namespace PoFightJudge.Api.Common;

/// <summary>
/// Who may run the seed endpoint. In Production this is the real check, so a hand-crafted request with an ordinary
/// token cannot rewrite the cast: the <c>Admin</c> app role, or a real login whose email is on the list
/// (<c>PoFightJudge:Seed:AdminEmails</c>, defaulting to the repository owner); guests never qualify; fails closed.
/// Outside Production every identity is a header or a dev cookie anyone can mint, so gating there is theatre — any
/// signed-in user (the dev guest included) may load the cast.
/// </summary>
public static class SeedAdminGate
{
    public static IReadOnlyList<string> DefaultAdminEmails { get; } = ["punkouter27@gmail.com"];

    public static bool IsSeedAdmin(ClaimsPrincipal user, IConfiguration configuration, bool production)
    {
        if (user.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        if (!production)
        {
            return true;
        }

        if (IsGuest(user))
        {
            return false;
        }

        if (user.IsAdmin())
        {
            return true;
        }

        var email = user.FindFirstValue("preferred_username") ?? user.FindFirstValue(ClaimTypes.Email) ?? user.FindFirstValue("email");
        return !string.IsNullOrWhiteSpace(email) && Whitelist(configuration).Contains(email.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A configured list replaces the default rather than extending it.</summary>
    public static IReadOnlyList<string> Whitelist(IConfiguration configuration) =>
        configuration.GetSection(ConfigKeys.Seed.AdminEmails).Get<string[]>() is { Length: > 0 } configured ? configured : DefaultAdminEmails;

    private static bool IsGuest(ClaimsPrincipal user) =>
        string.Equals(user.Identity?.AuthenticationType, GuestMiddleware.AuthenticationType, StringComparison.Ordinal)
        || string.Equals(user.FindFirstValue("auth_type"), GuestMiddleware.AuthenticationType, StringComparison.Ordinal);
}
