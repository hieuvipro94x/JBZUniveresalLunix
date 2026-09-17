using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using JBZUniveresalLunix.Models;

namespace JBZUniveresalLunix.Services;

/// <summary>
/// Explicit compatibility importer for PHT20 files. Fingerprints are checked
/// before parsing so unchanged files are not reparsed on later runs.
/// </summary>
public sealed class LegacyPhtImportService
{
    private readonly LegacyPhtHistoryReader _reader;
    private readonly ProductionPersistenceService _persistence;

    public LegacyPhtImportService(
        ProductionPersistenceService persistence,
        LegacyPhtHistoryReader? reader = null)
    {
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _reader = reader ?? new LegacyPhtHistoryReader();
    }

    public async Task<IReadOnlyList<LegacyImportResult>> ImportChangedFilesAsync(
        CancellationToken cancellationToken = default)
    {
        var results = new List<LegacyImportResult>();
        foreach (LegacyImportFile file in _reader.EnumerateImportFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string hash = await ComputeHashAsync(file.Path, cancellationToken).ConfigureAwait(false);
            if (!await _persistence.IsLegacyImportRequiredAsync(
                    file, hash, cancellationToken).ConfigureAwait(false))
            {
                results.Add(new LegacyImportResult(file.Path, 0, 0, 0, true));
                continue;
            }

            IReadOnlyList<TestHistoryRecord> records = await Task.Run(
                () => _reader.ReadImportFile(file), cancellationToken).ConfigureAwait(false);
            TestHistoryRecord[] snapshots = records
                .Select((record, index) => PrepareRecord(record, file, index))
                .ToArray();
            results.Add(await _persistence.ImportLegacyFileAsync(
                file, hash, snapshots, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    private static TestHistoryRecord PrepareRecord(
        TestHistoryRecord source,
        LegacyImportFile file,
        int recordIndex)
    {
        TestHistoryRecord record = source.ClonePersistenceSnapshot();
        string identity = string.Create(
            CultureInfo.InvariantCulture,
            $"{Path.GetFullPath(file.Path).ToUpperInvariant()}|{recordIndex}|" +
            $"{record.Finished:O}|{record.PartNumber}|{record.LotNo}|{record.Result}");
        record.CycleId = "legacy-pht-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return record;
    }

    private static async Task<string> ComputeHashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }
}
