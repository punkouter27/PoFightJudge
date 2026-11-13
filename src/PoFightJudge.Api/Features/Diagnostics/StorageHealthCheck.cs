using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace PoFightJudge.Api.Features.Diagnostics;

/// <summary>
/// Reports unhealthy when Table or Blob storage cannot be reached (locally: Azurite not running). Uses data-plane list
/// calls (one page each) because the app's managed identity holds only the <c>Storage Table/Blob Data Contributor</c>
/// roles; service-level <c>GetProperties</c> needs control-plane <c>tableServices/read</c> and fails with 403 in Azure.
/// </summary>
public sealed class StorageHealthCheck(TableServiceClient tables, BlobServiceClient blobs) : IHealthCheck
{
    /// <summary>Generous enough for a cold DefaultAzureCredential (the first token can take a few seconds locally), short enough for a probe.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(Timeout);
            await foreach (var _ in tables.QueryAsync(maxPerPage: 1, cancellationToken: cts.Token).AsPages())
            {
                break;
            }

            await foreach (var _ in blobs.GetBlobContainersAsync(cancellationToken: cts.Token).AsPages(pageSizeHint: 1))
            {
                break;
            }

            return HealthCheckResult.Healthy("Table and Blob storage reachable.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("Storage unreachable. Locally: ./scripts/azurite.ps1", ex);
        }
    }
}
