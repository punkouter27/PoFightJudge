using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.JSInterop;

namespace PoFightJudge.Client.Services;

/// <summary>
/// Attaches <c>X-Fake-User</c> and <c>X-Fake-Roles</c> headers in Development/Test
/// when a dev persona is selected via <see cref="Layout.AccountMenu"/>.
/// Never attaches anything in Production.
/// </summary>
public sealed class DevFakeUserHandler(IWebAssemblyHostEnvironment env, IJSRuntime js) : DelegatingHandler
{
    public const string FakeUserStorageKey = "pofightjudge:fake-user";
    public const string FakeRolesStorageKey = "pofightjudge:fake-roles";

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!env.IsProduction())
        {
            try
            {
                string? fakeUser = await js.InvokeAsync<string?>("localStorage.getItem", cancellationToken, FakeUserStorageKey);
                if (!string.IsNullOrWhiteSpace(fakeUser))
                {
                    request.Headers.TryAddWithoutValidation("X-Fake-User", fakeUser);
                    string? fakeRoles = await js.InvokeAsync<string?>("localStorage.getItem", cancellationToken, FakeRolesStorageKey);
                    if (!string.IsNullOrWhiteSpace(fakeRoles))
                        request.Headers.TryAddWithoutValidation("X-Fake-Roles", fakeRoles);
                }
            }
            catch
            {
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
