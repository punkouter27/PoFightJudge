using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PoFightJudge.Api.Features.Auth;
using PoFightJudge.Shared;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.E2EAPI;

[Collection(ApiCollection.Name)]
public class AuthTests(ApiFactory factory)
{
    // Every route a signed-out caller may reach. A new public route fails this test until it is added here on purpose.
    // Static files and the Blazor host itself are public by design and are identified by their metadata, not listed.
    private static readonly string[] AnonymousAllowList =
    [
        "/api/features",
        "/api/health",
        "/api/health/details",
        "/api/profiles/{id}/face",
        "/api/shared/{token}",
        "/api/{**path}",
        "/auth/guest",
        "/auth/logout",
        "/auth/me",
        "/health",
        "/health/live",
        "/{*path:nonfile}",
    ];

    [Fact]
    public async Task Me_reflects_the_credentials_on_the_request()
    {
        // Fake user header authenticates
        {
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add(FakeAuthOptions.UserHeader, "e2e-user");

            var me = await client.GetFromJsonAsync<AuthMeDto>(ApiRoutes.Auth.Me);

            me!.Authenticated.Should().BeTrue();
            me.UserId.Should().Be("e2e-user");
            me.AuthType.Should().Be(FakeAuthOptions.DefaultScheme);
        }

        // Anonymous without credentials
        {
            using var client = factory.CreateClient();
            var me = await client.GetFromJsonAsync<AuthMeDto>(ApiRoutes.Auth.Me);
            me!.Authenticated.Should().BeFalse();
        }

        // Folded in here because the tier is at its cap: the anonymous surface is exactly the allow-list, and every
        // other endpoint falls through to a policy that refuses anonymous callers.
        {
            var anonymous = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
                .OfType<RouteEndpoint>()
                .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
                .Where(e => !IsHostOrStaticAsset(e))
                .Select(e => "/" + e.RoutePattern.RawText!.TrimStart('/'))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal);
            anonymous.Should().Equal(AnonymousAllowList);

            var fallback = factory.Services.GetRequiredService<IOptions<AuthorizationOptions>>().Value.FallbackPolicy;
            fallback!.Requirements.Should().Contain(r => r is DenyAnonymousAuthorizationRequirement);
        }
    }

    [Fact]
    public async Task Guest_cookie_flow_authenticates_until_logout()
    {
        using var client = factory.CreateClient(new() { HandleCookies = true });

        var start = await client.PostAsync(ApiRoutes.Auth.Guest, null);
        start.StatusCode.Should().Be(HttpStatusCode.OK);
        var issued = await start.Content.ReadFromJsonAsync<AuthMeDto>();
        issued!.UserId.Should().StartWith(GuestMiddleware.IdPrefix);

        var me = await client.GetFromJsonAsync<AuthMeDto>(ApiRoutes.Auth.Me);
        me!.Authenticated.Should().BeTrue();
        me.AuthType.Should().Be(GuestMiddleware.AuthenticationType);
        me.UserId.Should().Be(issued.UserId);

        (await client.PostAsync(ApiRoutes.Auth.Logout, null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetFromJsonAsync<AuthMeDto>(ApiRoutes.Auth.Me))!.Authenticated.Should().BeFalse();
    }

    private static bool IsHostOrStaticAsset(RouteEndpoint endpoint) =>
        endpoint.RoutePattern.RawText!.TrimStart('/').StartsWith("_framework", StringComparison.Ordinal)
        || endpoint.Metadata.Any(m => m.GetType().Name is "StaticAssetDescriptor" or "ComponentTypeMetadata" or "RootComponentMetadata");
}
