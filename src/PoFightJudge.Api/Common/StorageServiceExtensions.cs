using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using Azure.Identity;
using Azure.Storage.Blobs;
using PoFightJudge.Api.Features.Diagnostics;
using PoFightJudge.Api.Features.Storage;
using PoFightJudge.Shared.Configuration;

namespace PoFightJudge.Api.Common;

/// <summary>Container names, bound from <c>PoFightJudge:Storage</c>.</summary>
public sealed record StorageContainers(string AudioContainer, string FacesContainer, string TtsCacheContainer)
{
    public static StorageContainers From(IConfiguration configuration) => new(
        configuration[ConfigKeys.Storage.AudioContainer] ?? "pofightjudge-audio",
        configuration[ConfigKeys.Storage.FacesContainer] ?? "pofightjudge-faces",
        configuration[ConfigKeys.Storage.TtsCacheContainer] ?? "pofightjudge-ttscache");
}

public static class StorageServiceExtensions
{
    /// <summary>
    /// Identity-only storage: both clients bind to an endpoint URI and authenticate with one shared
    /// <see cref="DefaultAzureCredential"/> — managed identity in Azure, <c>az login</c> locally, and the same flow
    /// against Azurite (<c>--oauth basic</c> over HTTPS). There is deliberately no connection-string branch, and the
    /// BannedApi analyzer makes the connection-string constructors a build error.
    /// A missing endpoint must not throw here: these are eagerly constructed singletons, so a throw would kill the host
    /// during <c>Build()</c> and defeat the degraded-mode contract. An unreachable placeholder is bound instead and the
    /// storage health check reports the real problem.
    /// </summary>
    public static IServiceCollection AddPoStorage(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var azurite = environment.IsDevelopment() && configuration.GetValue<bool>(Toggles.UseAzurite);

        // Locally the cloud-only providers (environment, workload identity, IMDS) only add seconds of probing before
        // `az login` is tried; Production keeps the full chain, where managed identity is the one that answers.
        TokenCredential credential = environment.IsDevelopment()
            ? new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ExcludeEnvironmentCredential = true,
                ExcludeWorkloadIdentityCredential = true,
                ExcludeManagedIdentityCredential = true,
                ExcludeInteractiveBrowserCredential = true,
            })
            : new DefaultAzureCredential();

        // The same identity signs the Azure OpenAI calls, so it is shared rather than built twice.
        services.AddSingleton(credential);

        // Azurite rejects the audience Azure.Data.Tables asks for; pin the storage scope in that one configuration only.
        if (azurite)
        {
            credential = new AzuriteStorageScopedCredential(credential);
        }

        var tableEndpoint = EndpointOrPlaceholder(configuration, ConfigKeys.Storage.TableEndpoint, "table");
        var blobEndpoint = EndpointOrPlaceholder(configuration, ConfigKeys.Storage.BlobEndpoint, "blob");
        var tableOptions = new TableClientOptions();
        var blobOptions = new BlobClientOptions();
        if (azurite)
        {
            // The emulator's TLS certificate is the exported ASP.NET dev cert, which this machine may not have trusted
            // (trusting it needs an interactive prompt). Accept exactly that cert — CN=localhost, on a loopback host — for
            // the storage clients in this one configuration; Production validation is untouched.
#pragma warning disable CA2000 // HttpClientTransport owns and disposes the handler.
            tableOptions.Transport = new HttpClientTransport(AzuriteTransportHandler(tableEndpoint.Host));
            blobOptions.Transport = new HttpClientTransport(AzuriteTransportHandler(blobEndpoint.Host));
#pragma warning restore CA2000
        }

        services.AddSingleton(new TableServiceClient(tableEndpoint, credential, tableOptions));
        services.AddSingleton(new BlobServiceClient(blobEndpoint, credential, blobOptions));
        services.AddSingleton(StorageContainers.From(configuration));
        services.AddSingleton<IAudioBlobStore, AudioBlobStore>();
        services.AddHealthChecks().AddCheck<StorageHealthCheck>("storage", tags: [HealthEndpoints.ReadyTag]);
        return services;
    }

    /// <summary>A well-formed URI keeps client construction total; misconfiguration surfaces as a degraded app, not a dead host.</summary>
    public static Uri EndpointOrPlaceholder(IConfiguration configuration, string key, string service)
    {
        var configured = configuration[key];
        return !string.IsNullOrWhiteSpace(configured) && Uri.TryCreate(configured, UriKind.Absolute, out var endpoint)
            ? endpoint
            : new Uri($"https://unconfigured.{service}.core.windows.net/");
    }

    /// <summary>
    /// True for the local emulator's certificate only: a loopback host presenting the dev cert (CN=localhost). Two
    /// errors are tolerated there and nowhere else — an untrusted chain (the cert was never trusted on this machine) and
    /// a name mismatch (the endpoints use 127.0.0.1, because the Azure SDK parses a path-style account out of an IP host
    /// but reads "localhost" as a DNS-style account name and drops the container from blob URLs).
    /// </summary>
    public static bool IsLocalDevCertificate(string host, X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        const SslPolicyErrors tolerated = SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch;
        var loopback = host is "localhost" or "127.0.0.1" or "::1";
        return loopback
            && (errors & ~tolerated) == SslPolicyErrors.None
            && certificate is X509Certificate2 { Subject: "CN=localhost" };
    }

    /// <summary>The endpoint host is captured up front: the callbacks sender is the SslStream, not the request.</summary>
    private static SocketsHttpHandler AzuriteTransportHandler(string host) => new()
    {
        SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, certificate, _, errors) => IsLocalDevCertificate(host, certificate, errors),
        },
    };
}
