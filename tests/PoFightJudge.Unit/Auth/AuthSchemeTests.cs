using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using PoFightJudge.Api.Features.Auth;
using PoFightJudge.Shared;

namespace PoFightJudge.Unit.Auth;

/// <summary>
/// Outside Production both doors are open: a request carrying a Microsoft token is checked by Entra, and anything
/// else falls to the guest cookie and the FakeAuth header, exactly as before.
/// </summary>
public class AuthSchemeTests
{
    [Fact]
    public void A_microsoft_token_goes_to_entra_and_everything_else_to_the_guest_door()
    {
        foreach (var (path, authorization, accessToken, entra) in new (string, string?, string?, bool)[]
        {
            ("/api/matches", "Bearer abc.def.ghi", null, true),
            (ApiRoutes.Hubs.Live, null, "abc.def.ghi", true),
            ("/api/matches", null, "abc.def.ghi", false),
            ("/api/matches", null, null, false),
            ("/api/matches", "Basic dXNlcjpwYXNz", null, false),
        })
        {
            var http = new DefaultHttpContext();
            http.Request.Path = path;
            if (authorization is not null)
            {
                http.Request.Headers.Authorization = authorization;
            }

            if (accessToken is not null)
            {
                http.Request.QueryString = new QueryString($"?access_token={accessToken}");
            }

            AuthServiceExtensions.SchemeFor(http).Should()
                .Be(entra ? JwtBearerDefaults.AuthenticationScheme : FakeAuthOptions.DefaultScheme, $"{path} {authorization} {accessToken}");
        }
    }
}
