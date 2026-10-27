using Azure.Data.Tables;
using PoFightJudge.Api.Common;
using PoFightJudge.Shared.Identifiers;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Api.Features.Profiles;

/// <summary>Persistence contract for WATCH personas; the endpoints and the seeding depend on this, not on Tables.</summary>
public interface IProfileRepository
{
    Task<Profile?> GetByIdAsync(ProfileId id, CancellationToken ct = default);

    Task<IReadOnlyList<Profile>> GetAllAsync(CancellationToken ct = default);

    Task UpsertAsync(Profile profile, CancellationToken ct = default);

    Task DeleteAsync(ProfileId id, CancellationToken ct = default);
}

/// <summary>
/// Azure Table Storage implementation. One partition, RowKey = initials; the table is created on first use by
/// <see cref="StorageBootstrap"/>, which also recreates it if it disappears under us.
/// </summary>
public sealed class ProfileRepository(TableServiceClient tables) : IProfileRepository
{
    public const string TableName = "pofightjudgeprofiles";

    private TableClient Table => tables.GetTableClient(TableName);

    public Task<Profile?> GetByIdAsync(ProfileId id, CancellationToken ct = default) =>
        StorageBootstrap.WithTableAsync<Profile?>(tables, TableName, async token =>
        {
            var response = await Table.GetEntityIfExistsAsync<ProfileTableEntity>(ProfileTableEntity.Partition, id.Value, cancellationToken: token);
            return response.HasValue ? response.Value!.ToDomain() : null;
        }, ct);

    public Task<IReadOnlyList<Profile>> GetAllAsync(CancellationToken ct = default) =>
        QueryAsync(TableClient.CreateQueryFilter($"PartitionKey eq {ProfileTableEntity.Partition}"), ct);

    public Task UpsertAsync(Profile profile, CancellationToken ct = default) =>
        StorageBootstrap.WithTableAsync(tables, TableName, token => Table.UpsertEntityAsync(profile.ToEntity(), TableUpdateMode.Replace, token), ct);

    /// <summary>Idempotent: the service answers 404 for a missing row and the client does not throw for it.</summary>
    public Task DeleteAsync(ProfileId id, CancellationToken ct = default) =>
        StorageBootstrap.WithTableAsync(tables, TableName, token => Table.DeleteEntityAsync(ProfileTableEntity.Partition, id.Value, cancellationToken: token), ct);

    private Task<IReadOnlyList<Profile>> QueryAsync(string filter, CancellationToken ct) =>
        StorageBootstrap.WithTableAsync<IReadOnlyList<Profile>>(tables, TableName, async token =>
        {
            var results = new List<Profile>();
            await foreach (var entity in Table.QueryAsync<ProfileTableEntity>(filter, cancellationToken: token))
            {
                results.Add(entity.ToDomain());
            }

            return results;
        }, ct);
}
