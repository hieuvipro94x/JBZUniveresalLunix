using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using JBZUniveresalLunix.Models;

namespace JBZUniveresalLunix.Services;

public sealed record LegacyProductionSnapshot(
    long DailyTotal,
    long DailyPass,
    long DailyFail,
    long MonthlyTotal,
    long LifetimeTotal);

/// <summary>
/// Reads DAT/ERR history for explicit compatibility import. Runtime production
/// counters and history are always queried from SQLite.
/// </summary>
public sealed partial class LegacyPhtHistoryReader
{
    private static readonly Encoding KoreanEncoding = CreateKoreanEncoding();
    private readonly string _passRoot;
    private readonly string _errorRoot;
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, CachedFile> _cache = new(StringComparer.OrdinalIgnoreCase);

    public LegacyPhtHistoryReader(string? passRoot = null, string? errorRoot = null)
    {
        _passRoot = string.IsNullOrWhiteSpace(passRoot) ? RuntimePaths.PassRoot : Path.GetFullPath(passRoot);
        _errorRoot = string.IsNullOrWhiteSpace(errorRoot) ? RuntimePaths.ErrorRoot : Path.GetFullPath(errorRoot);
    }

    public bool HasSharedHistory => Directory.Exists(_passRoot) || Directory.Exists(_errorRoot);
    public string PassRoot => _passRoot;
    public string ErrorRoot => _errorRoot;

    public IReadOnlyList<LegacyImportFile> EnumerateImportFiles() =>
        EnumerateFiles(_passRoot, "Day*.dat")
            .Select(path => CreateImportFile(path, passedFile: true))
            .Concat(EnumerateFiles(_errorRoot, "Day*.err")
                .Select(path => CreateImportFile(path, passedFile: false)))
            .Where(item => item is not null)
            .Cast<LegacyImportFile>()
            .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public IReadOnlyList<TestHistoryRecord> ReadImportFile(LegacyImportFile file) =>
        ReadCachedFile(file.Path, file.PassedFile);

    private static LegacyImportFile? CreateImportFile(string path, bool passedFile)
    {
        try
        {
            var info = new FileInfo(path);
            return new LegacyImportFile(
                info.FullName,
                info.Length,
                info.LastWriteTimeUtc,
                passedFile);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public LegacyProductionSnapshot GetProductionSnapshot(ProductModel model, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(model);
        string partNumber = ResolvePartNumber(model);
        TestHistoryRecord[] rows = ReadAllProductRecords()
            .Where(row => string.Equals(row.PartNumber, partNumber, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        long dailyPass = rows.LongCount(row => row.Passed && row.Finished.Date == now.Date);
        long dailyFail = rows.LongCount(row => !row.Passed && row.Finished.Date == now.Date);
        long monthly = rows.LongCount(row =>
            row.Finished.Year == now.Year && row.Finished.Month == now.Month);
        return new LegacyProductionSnapshot(
            dailyPass + dailyFail,
            dailyPass,
            dailyFail,
            monthly,
            rows.LongLength);
    }

    public IReadOnlyList<TestHistoryRecord> Search(HistorySearchCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        return SearchCore(criteria)
            .Take(Math.Clamp(criteria.MaxRows, 1, 50_000))
            .ToArray();
    }

    public IReadOnlyList<TestHistoryRecord> SearchForExport(HistorySearchCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        return SearchCore(criteria).ToArray();
    }

    private IEnumerable<TestHistoryRecord> SearchCore(HistorySearchCriteria criteria)
    {
        IEnumerable<TestHistoryRecord> query = ReadAllProductRecords();
        if (criteria.From is DateTime from)
            query = query.Where(row => row.Started >= from);
        if (criteria.To is DateTime to)
            query = query.Where(row => row.Started <= to);
        if (criteria.LotNo is long lotNo)
            query = query.Where(row => row.LotNo == lotNo);
        if (!string.IsNullOrWhiteSpace(criteria.PartKeyword))
        {
            string keyword = criteria.PartKeyword.Trim();
            query = query.Where(row =>
                row.PartNumber.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                row.PartName.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(criteria.Result) &&
            !criteria.Result.Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            string result = criteria.Result.Trim();
            query = query.Where(row => row.Result.Contains(result, StringComparison.OrdinalIgnoreCase));
        }

        return query.OrderByDescending(row => row.Finished);
    }

    public static IReadOnlyList<TestHistoryRecord> MergeWithoutDuplicates(
        IEnumerable<TestHistoryRecord> detailedRows,
        IEnumerable<TestHistoryRecord> legacyRows,
        bool exportOrder)
    {
        TestHistoryRecord[] detailed = detailedRows.ToArray();
        var matched = detailed
            .GroupBy(BuildIdentity)
            .ToDictionary(group => group.Key, group => group.Count());
        var merged = new List<TestHistoryRecord>(detailed);

        foreach (TestHistoryRecord legacy in legacyRows)
        {
            string identity = BuildIdentity(legacy);
            if (matched.TryGetValue(identity, out int remaining) && remaining > 0)
            {
                matched[identity] = remaining - 1;
                continue;
            }
            merged.Add(legacy);
        }

        return exportOrder
            ? merged.OrderBy(row => row.PartNumber, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Started).ThenBy(row => row.Id).ToArray()
            : merged.OrderByDescending(row => row.Finished).ThenByDescending(row => row.Id).ToArray();
    }

    private IEnumerable<TestHistoryRecord> ReadAllProductRecords()
    {
        foreach (string path in EnumerateFiles(_passRoot, "Day*.dat"))
        {
            foreach (TestHistoryRecord row in ReadCachedFile(path, passedFile: true))
                yield return row;
        }
        foreach (string path in EnumerateFiles(_errorRoot, "Day*.err"))
        {
            foreach (TestHistoryRecord row in ReadCachedFile(path, passedFile: false))
                yield return row;
        }
    }

    private IReadOnlyList<TestHistoryRecord> ReadCachedFile(string path, bool passedFile)
    {
        try
        {
            var info = new FileInfo(path);
            long length = info.Length;
            long modified = info.LastWriteTimeUtc.Ticks;
            lock (_cacheGate)
            {
                if (_cache.TryGetValue(path, out CachedFile? cached) &&
                    cached.Length == length && cached.LastWriteUtcTicks == modified)
                {
                    return cached.Rows;
                }
            }

            TestHistoryRecord[] rows = (passedFile ? ReadPassFile(path) : ReadErrorFile(path)).ToArray();
            info.Refresh();
            var updated = new CachedFile(info.Length, info.LastWriteTimeUtc.Ticks, rows);
            lock (_cacheGate)
                _cache[path] = updated;
            return rows;
        }
        catch (IOException)
        {
            lock (_cacheGate)
                return _cache.TryGetValue(path, out CachedFile? cached) ? cached.Rows : [];
        }
    }

    private static IEnumerable<string> EnumerateFiles(string root, string pattern) =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
            : [];

    private static IEnumerable<TestHistoryRecord> ReadPassFile(string path)
    {
        foreach (string line in ReadLines(path))
        {
            string[] fields = line.Split('|');
            if (fields.Length < 9 || !string.Equals(fields[1].Trim(), "1", StringComparison.Ordinal))
                continue; // Original MASTER rows use a different status and are not production.
            if (!DateTime.TryParseExact(
                    fields[3].Trim() + fields[4].Trim(),
                    "yyMMddHHmmss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTime finished))
            {
                continue;
            }

            string dateLot = fields[5].Trim();
            if (dateLot.Length <= 6 ||
                !long.TryParse(dateLot[6..], NumberStyles.None, CultureInfo.InvariantCulture, out long lotNo))
            {
                continue;
            }
            string partAndLot = fields[8].Trim();
            int lotLength = dateLot.Length - 6;
            string partNumber = partAndLot.Length > lotLength
                ? partAndLot[..^lotLength]
                : string.Empty;

            yield return CreateLegacyRecord(finished, partNumber, lotNo, passed: true);
        }
    }

    private static IEnumerable<TestHistoryRecord> ReadErrorFile(string path)
    {
        string text = KoreanEncoding.GetString(ReadSharedBytes(path));
        string[] blocks = Regex.Split(text, @"(?:\r?\n){2,}");
        foreach (string block in blocks)
        {
            string[] lines = block.Split(["\r\n", "\n"], StringSplitOptions.None);
            if (lines.Length == 0)
                continue;
            Match match = ErrorHeaderRegex().Match(lines[0]);
            if (!match.Success ||
                !DateTime.TryParseExact(
                    match.Groups["start"].Value,
                    "yyyy/MM/dd HH:mm:ss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTime started) ||
                !TimeSpan.TryParseExact(
                    match.Groups["end"].Value,
                    "hh\\:mm\\:ss",
                    CultureInfo.InvariantCulture,
                    out TimeSpan endTime) ||
                !long.TryParse(match.Groups["lot"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long lotNo))
            {
                continue;
            }

            DateTime finished = started.Date + endTime;
            if (finished < started)
                finished = finished.AddDays(1);
            TestHistoryRecord row = CreateLegacyRecord(
                finished,
                match.Groups["part"].Value.Trim(),
                lotNo,
                passed: false);
            row.Started = started;
            row.PartName = match.Groups["name"].Value.Trim();
            row.VehicleType = match.Groups["vehicle"].Value.Trim();
            row.Alc = match.Groups["alc"].Value.Trim();
            row.FaultSummary = string.Join(" ", lines.Skip(1).Select(value => value.Trim()).Where(value => value.Length > 0));
            row.FaultType = lines[0].Contains("Open", StringComparison.OrdinalIgnoreCase)
                ? "HỞ MẠCH"
                : "SAI DÂY / CHẬP MẠCH";
            yield return row;
        }
    }

    private static TestHistoryRecord CreateLegacyRecord(
        DateTime finished,
        string partNumber,
        long lotNo,
        bool passed) => new()
        {
            Id = 0,
            Started = finished,
            Finished = finished,
            TestStartedAt = finished,
            ResultAt = finished,
            InspectionType = HistoryInspectionType.Product,
            PartNumber = partNumber,
            LotNo = lotNo,
            Result = passed ? "PASS" : "FAIL",
            Passed = passed,
            HtdrvName = "PHT20",
            PrintStatus = LabelPrintStatus.NotRequested.ToString()
        };

    private static IEnumerable<string> ReadLines(string path)
    {
        byte[] bytes = ReadSharedBytes(path);
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Đọc được cả lịch sử CP949 do các phiên bản cũ đã ghi.
            text = KoreanEncoding.GetString(bytes);
        }
        return text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
    }

    private static byte[] ReadSharedBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[stream.Length];
        int offset = 0;
        while (offset < bytes.Length)
        {
            int read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
                break;
            offset += read;
        }
        return offset == bytes.Length ? bytes : bytes[..offset];
    }

    private static string BuildIdentity(TestHistoryRecord row) => string.Create(
        CultureInfo.InvariantCulture,
        $"{row.Finished:yyyyMMddHHmmss}|{(row.Passed ? 1 : 0)}|{row.PartNumber.Trim().ToUpperInvariant()}|{row.LotNo}");

    private static string ResolvePartNumber(ProductModel model) =>
        !string.IsNullOrWhiteSpace(model.PartNumber)
            ? model.PartNumber.Trim()
            : !string.IsNullOrWhiteSpace(model.ModelName)
                ? model.ModelName.Trim()
                : Path.GetFileNameWithoutExtension(model.SourcePath).Trim();

    private static Encoding CreateKoreanEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(949, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
    }

    [GeneratedRegex(
        @"^\[[^\]]*\]\s*\*(?<name>[^;]*);\s*(?<part>[^;]*);\s*(?<vehicle>[^;]*);\s*(?<alc>[^;]*);\s*;\s*;\s*(?<lot>\d+)\s+(?<start>\d{4}/\d{2}/\d{2}\s+\d{2}:\d{2}:\d{2})\s*-\s*(?<end>\d{2}:\d{2}:\d{2})\|?",
        RegexOptions.CultureInvariant)]
    private static partial Regex ErrorHeaderRegex();

    private sealed record CachedFile(long Length, long LastWriteUtcTicks, TestHistoryRecord[] Rows);
}
