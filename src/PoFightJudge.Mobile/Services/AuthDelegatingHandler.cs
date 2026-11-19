using System.Net.Http.Headers;

namespace PoFightJudge.Mobile.Services;

/// <summary>
/// Adds the Entra bearer token when signed in with Microsoft. A Debug guest sign-in rides on the
/// API's guest cookie instead (the HttpClientHandler's CookieContainer), exactly like the web.
/// </summary>
public sealed class AuthDelegatingHandler(SecureSessionStore sessionStore) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await sessionStore.GetBearerTokenAsync().ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
