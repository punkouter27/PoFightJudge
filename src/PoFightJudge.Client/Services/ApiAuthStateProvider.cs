using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Client.Services;

/// <summary>
/// Development/Test auth state: asks the API who the guest cookie, the FakeAuth header or (in Development) a
/// Microsoft token says we are. Production uses MSAL's provider instead and never registers this one.
/// </summary>
public sealed class ApiAuthStateProvider : AuthenticationStateProvider
{
    private static readonly AuthenticationState Anonymous = new(new ClaimsPrincipal(new ClaimsIdentity()));

    private readonly IApiClient api;

    /// <param name="microsoft">MSAL's token provider where it is registered. It is also a state provider, and it
    /// says so when a Microsoft sign-in completes — which is when /auth/me has a new answer.</param>
    public ApiAuthStateProvider(IApiClient api, IAccessTokenProvider? microsoft = null)
    {
        this.api = api;
        if (microsoft is AuthenticationStateProvider msal)
        {
            msal.AuthenticationStateChanged += _ => NotifyChanged();
        }
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        try
        {
            return ToState(await api.GetMeAsync());
        }
        catch (HttpRequestException)
        {
            return Anonymous;
        }
    }

    /// <summary>Call after a guest sign-in or logout so AuthorizeView re-evaluates.</summary>
    public void NotifyChanged() => NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());

    public static AuthenticationState ToState(AuthMeDto me)
    {
        if (!me.Authenticated)
        {
            return Anonymous;
        }

        List<Claim> claims =
        [
            new Claim(ClaimTypes.NameIdentifier, me.UserId ?? string.Empty),
            new Claim(ClaimTypes.Name, me.Name ?? me.UserId ?? "user"),
        ];
        if (me.Email is { Length: > 0 } email)
        {
            claims.Add(new Claim(ClaimTypes.Email, email));
        }

        var identity = new ClaimsIdentity(claims, me.AuthType ?? "api");
        return new AuthenticationState(new ClaimsPrincipal(identity));
    }
}
