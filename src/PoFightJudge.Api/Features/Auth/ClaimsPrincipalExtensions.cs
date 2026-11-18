using System.Security.Claims;

namespace PoFightJudge.Api.Features.Auth;

public static class ClaimsPrincipalExtensions
{
    /// <summary>The stable user id: NameIdentifier, else Entra <c>oid</c>, else <c>sub</c>; null when unauthenticated.</summary>
    public static string? UserIdOrNull(this ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true
            ? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("oid") ?? user.FindFirstValue("sub")
            : null;

    public static string UserId(this ClaimsPrincipal user) =>
        user.UserIdOrNull() ?? throw new InvalidOperationException("No user id claim on an authenticated principal.");

    public static string? DisplayName(this ClaimsPrincipal user) =>
        user.Identity?.Name ?? user.FindFirstValue("name") ?? user.FindFirstValue("preferred_username");

    /// <summary>
    /// The sign-in address, when the credential has one: an Entra token's email or preferred_username, or a FakeAuth
    /// name that is an address. A guest has none.
    /// </summary>
    public static string? Email(this ClaimsPrincipal user)
    {
        foreach (var candidate in new[] { user.FindFirstValue(ClaimTypes.Email), user.FindFirstValue("email"), user.FindFirstValue("preferred_username"), user.FindFirstValue(ClaimTypes.Upn), user.Identity?.Name })
        {
            if (candidate is { Length: > 0 } value && value.Contains('@', StringComparison.Ordinal))
            {
                return value;
            }
        }

        return null;
    }

    public static bool IsAdmin(this ClaimsPrincipal user) => user.IsInRole("Admin");
}
