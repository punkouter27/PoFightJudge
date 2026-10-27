using System.Security.Claims;
using Carter;
using PoFightJudge.Shared;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Api.Features.Auth;

/// <summary>BFF-facing auth probes at the root: who am I, guest sign-in (Dev/Test only), and logout.</summary>
public sealed class AuthEndpoints : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var mode = app.ServiceProvider.GetRequiredService<AuthMode>();

        app.MapGet(ApiRoutes.Auth.Me, (HttpContext ctx) => Results.Ok(ToDto(ctx.User))).AllowAnonymous().WithTags("Auth");

        app.MapPost(ApiRoutes.Auth.Logout, (HttpContext ctx) =>
        {
            // Clear the dev guest cookie regardless of the auth scheme in play. A follow-up /auth/me is then
            // anonymous and the SPA's AuthStateProvider signs the user out.
            ctx.Response.Cookies.Delete(GuestMiddleware.CookieName);
            return Results.NoContent();
        }).AllowAnonymous().WithTags("Auth");

        if (!mode.UseFake)
        {
            return;
        }

        app.MapPost(ApiRoutes.Auth.Guest, (HttpContext ctx, TimeProvider clock) =>
        {
            var id = GuestMiddleware.NewGuestId();
            ctx.Response.Cookies.Append(GuestMiddleware.CookieName, id, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Secure = ctx.Request.IsHttps,
                Expires = clock.GetUtcNow().AddHours(8),
            });
            return Results.Ok(new AuthMeDto(true, id, id, GuestMiddleware.AuthenticationType));
        }).AllowAnonymous().WithTags("Auth");
    }

    public static AuthMeDto ToDto(ClaimsPrincipal user) =>
        user.UserIdOrNull() is { } id
            ? new AuthMeDto(true, id, user.DisplayName() ?? id, user.FindFirstValue("auth_type") ?? user.Identity?.AuthenticationType)
            : AuthMeDto.Anonymous;
}
