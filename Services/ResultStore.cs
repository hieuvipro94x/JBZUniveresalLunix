using System.IO;
using JBZUniveresalLunix.Models;

namespace JBZUniveresalLunix.Services;

public sealed class ResultStore
{
    private readonly TestHistoryStore _history;

    public ResultStore(string path)
    {
        _history = new TestHistoryStore(RuntimePaths.DatabaseFile);
        if (!string.IsNullOrWhiteSpace(path) &&
            !string.Equals(
                Path.GetFullPath(path),
                RuntimePaths.DatabaseFile,
                StringComparison.OrdinalIgnoreCase))
        {
            AsyncFileLogService.Current.Application(
                $"LEGACY_RESULTSTORE_PATH_IGNORED requested={path} canonical={RuntimePaths.DatabaseFile}",
                AppLogLevel.Diagnostic);
        }
    }

    public void Save(TestSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        _history.Add(new TestHistoryRecord
        {
            Started = summary.Started,
            Finished = summary.Finished,
            ResultAt = summary.Finished,
            ModelName = summary.Model ?? string.Empty,
            PartName = summary.Model ?? string.Empty,
            BarcodeValue = summary.Barcode ?? string.Empty,
            Passed = summary.Passed,
            Result = summary.Passed ? "PASS" : "FAIL",
            OpenCount = summary.OpenCount,
            WrongCount = summary.WrongCount,
            ShortCount = summary.ShortCount,
            Resistance = string.Join("; ", summary.Resistance.Select(item => $"{item.Name}={item.Display}"))
        });
    }
}
