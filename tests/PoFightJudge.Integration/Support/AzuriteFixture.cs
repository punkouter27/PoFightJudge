using System.Text.RegularExpressions;
using Azure;
using Azure.Data.Tables;
using Azure.Storage;
using Azure.Storage.Blobs;
using Testcontainers.Azurite;

namespace PoFightJudge.Integration.Support;

/// <summary>
/// One throwaway Azurite container per test run (Testcontainers, random ports). Tests skip when Docker is unavailable
/// — a green run with no Azurite proves nothing, so the skip is loud. Clients are built from endpoint + shared key
/// (the app itself never uses connection strings; the analyzer bans those constructors here too).
/// </summary>
public sealed partial class AzuriteFixture : IAsyncLifetime
{
    private AzuriteContainer? _container;

    public bool IsAvailable { get; private set; }

    public string Unavailable { get; private set; } = "Azurite has not started.";

    public TableServiceClient Tables { get; private set; } = null!;

    public BlobServiceClient Blobs { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest")
                .WithCommand("--skipApiVersionCheck") // the SDK asks for API versions newer than the emulator knows
                .Build();
            await _container.StartAsync();

            var cs = _container.GetConnectionString();
            var account = Part(cs, "AccountName");
            var key = Part(cs, "AccountKey");
            var credential = new StorageSharedKeyCredential(account, key);
            Tables = new TableServiceClient(new Uri(Part(cs, "TableEndpoint")), new TableSharedKeyCredential(account, key));
            Blobs = new BlobServiceClient(new Uri(Part(cs, "BlobEndpoint")), credential);
            IsAvailable = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Unavailable = $"Azurite unavailable — integration tests skip: {ex.Message}";
            Console.WriteLine($"[AzuriteFixture] {Unavailable}");
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private static string Part(string connectionString, string name) =>
        PartPattern().Matches(connectionString).Select(m => (m.Groups["key"].Value, m.Groups["value"].Value))
            .First(p => string.Equals(p.Item1, name, StringComparison.OrdinalIgnoreCase)).Item2;

    [GeneratedRegex("(?<key>[A-Za-z]+)=(?<value>[^;]+)", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PartPattern();
}

[CollectionDefinition(Name)]
public sealed class AzuriteCollection : ICollectionFixture<AzuriteFixture>
{
    public const string Name = "Azurite";
}
