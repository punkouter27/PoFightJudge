using System.Text.Json;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Mobile.Services;

public sealed class OfflineVerdictCache : IDisposable
{
    private readonly string _cachePath = Path.Combine(FileSystem.AppDataDirectory, "pofightjudge-matches.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public void Dispose() => _gate.Dispose();

    public async Task SaveMatchesAsync(IReadOnlyList<MatchDto> matches, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var temp = _cachePath + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(matches), ct).ConfigureAwait(false);
            File.Move(temp, _cachePath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<MatchDto>> LoadMatchesAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_cachePath))
            {
                return [];
            }

            var json = await File.ReadAllTextAsync(_cachePath, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<List<MatchDto>>(json) ?? [];
        }
        finally
        {
            _gate.Release();
        }
    }
}
