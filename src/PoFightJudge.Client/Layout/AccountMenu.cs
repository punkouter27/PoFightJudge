using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using PoFightJudge.Client.Services;

namespace PoFightJudge.Client.Layout;

public static class AccountMenu
{
    public sealed record Link(string Path, string Icon, string Label);

    /// <param name="Email">What the Po header shows: the sign-in address, or the guest's id when there is none.</param>
    public sealed record Identity(bool IsAuthenticated, string Label, string Email);

    public static IReadOnlyList<Link> Links() =>
    [
        new("/profiles", "theater_comedy", "Cast profiles"),
        new("/history", "history", "History"),
        new("/diag", "health_and_safety", "System status"),
    ];

    public static Identity Describe(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true)
            return new Identity(false, string.Empty, string.Empty);

        string name = user.Identity?.Name
            ?? user.FindFirst("email")?.Value
            ?? user.FindFirst(ClaimTypes.Email)?.Value
            ?? "Guest";

        return new Identity(true, name, EmailOf(user) ?? name);
    }

    /// <summary>The sign-in address on <paramref name="user"/>: an email claim, Entra's preferred_username, or a name that is one.</summary>
    public static string? EmailOf(ClaimsPrincipal user)
    {
        foreach (var candidate in new[] { user.FindFirst(ClaimTypes.Email)?.Value, user.FindFirst("email")?.Value, user.FindFirst("preferred_username")?.Value, user.Identity?.Name })
        {
            if (candidate is { Length: > 0 } value && value.Contains('@', StringComparison.Ordinal))
            {
                return value;
            }
        }

        return null;
    }

    public static async Task SwitchFakeUserAsync(IJSRuntime js, NavigationManager nav, string? user, string? roles = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(user))
            {
                await js.InvokeVoidAsync("localStorage.removeItem", DevFakeUserHandler.FakeUserStorageKey);
                await js.InvokeVoidAsync("localStorage.removeItem", DevFakeUserHandler.FakeRolesStorageKey);
            }
            else
            {
                await js.InvokeVoidAsync("localStorage.setItem", DevFakeUserHandler.FakeUserStorageKey, user);
                if (!string.IsNullOrWhiteSpace(roles))
                    await js.InvokeVoidAsync("localStorage.setItem", DevFakeUserHandler.FakeRolesStorageKey, roles);
                else
                    await js.InvokeVoidAsync("localStorage.removeItem", DevFakeUserHandler.FakeRolesStorageKey);
            }
        }
        catch
        {
        }

        nav.NavigateTo(nav.Uri, forceLoad: true);
    }
}
