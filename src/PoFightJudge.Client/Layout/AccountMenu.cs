using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using PoFightJudge.Client.Services;

namespace PoFightJudge.Client.Layout;

public static class AccountMenu
{
    public sealed record Link(string Path, string Icon, string Label);

    public sealed record Identity(bool IsAuthenticated, string Label);

    public static IReadOnlyList<Link> Links() =>
    [
        new("/profiles", "theater_comedy", "Cast profiles"),
        new("/history", "history", "History"),
        new("/status", "health_and_safety", "System status"),
    ];

    public static Identity Describe(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true)
            return new Identity(false, string.Empty);

        string name = user.Identity?.Name
            ?? user.FindFirst("email")?.Value
            ?? user.FindFirst(ClaimTypes.Email)?.Value
            ?? "Guest";

        return new Identity(true, name);
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
