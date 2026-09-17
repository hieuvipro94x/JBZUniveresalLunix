using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using JBZUniveresalLunix.Models;

namespace JBZUniveresalLunix.Services;

/// <summary>
/// Explicit legacy-file helper retained for migration tests. Runtime label
/// print state is persisted in SQLite and never creates LastBarcode.txt.
/// </summary>
public sealed class LabelDuplicateGuard
{
    private readonly object _gate = new();
    private readonly string _path;

    public LabelDuplicateGuard(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public LabelDuplicateRecord? LoadLast()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
                return null;
            try
            {
                return JsonSerializer.Deserialize<LabelDuplicateRecord>(File.ReadAllText(_path));
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    public void RecordSuccessfulPrint(LabelPrintRequest request, DateTime printedAt)
    {
        ArgumentNullException.ThrowIfNull(request);
        var record = new LabelDuplicateRecord(
            request.Data.Barcode,
            printedAt,
            request.Data.PartNumber,
            request.CycleId,
            request.Data.LotNo);

        lock (_gate)
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(
                _path,
                JsonSerializer.Serialize(record, JsonOptions),
                new System.Text.UTF8Encoding(false));
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public sealed record LabelDuplicateRecord(
    string Barcode,
    DateTime PrintedAt,
    string PartNumber,
    string CycleId,
    long LotNo);
