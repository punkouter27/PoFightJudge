using Blazored.LocalStorage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Authentication.WebAssembly.Msal.Models;
using Microsoft.Extensions.DependencyInjection;
using PoFightJudge.Client;
using PoFightJudge.Client.Services;
using Radzen;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var baseAddress = new Uri(builder.HostEnvironment.BaseAddress);
// No token handler on this one: it carries the calls a signed-out browser makes (the feature flags on the sign-in
// page). In Production the authorized client below throws AccessTokenNotAvailableException before the request leaves.
builder.Services.AddHttpClient(HttpClients.Anonymous, c => c.BaseAddress = baseAddress);
builder.Services.AddRadzenComponents();
builder.Services.AddBlazoredLocalStorage();
builder.Services.AddScoped<ThemeInterop>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IApiClient, ApiClient>();
builder.Services.AddScoped<AudioInterop>();
builder.Services.AddScoped<FxInterop>();
builder.Services.AddScoped<SfxInterop>();
builder.Services.AddScoped<Preferences>();
builder.Services.AddScoped<GfxInterop>();
builder.Services.AddScoped<ParticleInterop>();
builder.Services.AddScoped<MicInterop>();
builder.Services.AddScoped<AsrInterop>();
builder.Services.AddScoped<LiveAudio>();
builder.Services.AddScoped(sp => new LiveConnectionFactory(sp, new Uri(builder.HostEnvironment.BaseAddress)));
builder.Services.AddScoped<SimulationState>();
builder.Services.AddScoped<SetupMemory>();
builder.Services.AddScoped<SaveInterop>();
builder.Services.AddScoped<ConnectionState>();
builder.Services.AddScoped<FeatureGate>();
builder.Services.AddScoped<Viewport>();
builder.Services.AddScoped<ConnectionWatchHandler>();

// Environment split (SPEC §2): Production = Microsoft Entra ID via MSAL, API calls to our own origin carry the access
// token. Development = MSAL *and* the guest cookie: a token goes on a call when there is one, and auth state is
// whatever /auth/me says, which answers for either. Test = the guest cookie / FakeAuth header alone, offline.
if (MicrosoftSignIn.IsAvailable(builder.HostEnvironment))
{
    builder.Services.AddMsalAuthentication(options =>
    {
        builder.Configuration.Bind("AzureAd", options.ProviderOptions.Authentication);
        if (builder.Configuration["AzureAd:Scope"] is { Length: > 0 } scope)
        {
            options.ProviderOptions.DefaultAccessTokenScopes.Add(scope);
        }

        options.ProviderOptions.LoginMode = "redirect";
    });
}

if (builder.HostEnvironment.IsProduction())
{
    builder.Services.AddScoped<BaseAddressAuthorizationMessageHandler>();
    builder.Services.AddHttpClient(HttpClients.Api, c => c.BaseAddress = baseAddress)
        .AddHttpMessageHandler<ConnectionWatchHandler>()
        .AddHttpMessageHandler<BaseAddressAuthorizationMessageHandler>();
    builder.Services.AddScoped(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClients.Api));
}
else
{
    var api = builder.Services.AddHttpClient(HttpClients.Api, c => c.BaseAddress = baseAddress)
        .AddHttpMessageHandler<ConnectionWatchHandler>();
    builder.Services.AddScoped(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClients.Api));

    if (MicrosoftSignIn.IsAvailable(builder.HostEnvironment))
    {
        // MSAL registered its own state provider and points its token provider and redirect service at it by a
        // cast. Those two are pinned to MSAL's concrete service here, so /auth/me can be the state provider.
        builder.Services.AddScoped<RemoteAuthenticationService<RemoteAuthenticationState, RemoteUserAccount, MsalProviderOptions>>();
        builder.Services.AddScoped<IAccessTokenProvider>(sp => sp.GetRequiredService<RemoteAuthenticationService<RemoteAuthenticationState, RemoteUserAccount, MsalProviderOptions>>());
        builder.Services.AddScoped<IRemoteAuthenticationService<RemoteAuthenticationState>>(sp => sp.GetRequiredService<RemoteAuthenticationService<RemoteAuthenticationState, RemoteUserAccount, MsalProviderOptions>>());
        builder.Services.AddScoped<OptionalTokenHandler>();
        api.AddHttpMessageHandler<OptionalTokenHandler>();
    }

    builder.Services.AddScoped<ApiAuthStateProvider>();
    builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<ApiAuthStateProvider>());
}

// No anonymous browsing: every page needs a signed-in (or guest) user unless it opts out.
builder.Services.AddAuthorizationCore(options =>
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

var host = builder.Build();

// Before the first render, not after it. The fake-AI banner sits above the top bar, so a flag that arrives late is a
// strip that appears and pushes the whole app down — measured at CLS 0.29 on the home page. One request, and every
// page that needs the flags reads them from the gate instead of asking again.
await host.Services.GetRequiredService<FeatureGate>().LoadAsync();

await host.RunAsync();

namespace PoFightJudge.Client
{
    /// <summary>Named HttpClient registrations; the one place the name lives.</summary>
    public static class HttpClients
    {
        public const string Api = "PoFightJudge.Api";
        public const string Anonymous = "PoFightJudge.Anonymous";
    }
}
