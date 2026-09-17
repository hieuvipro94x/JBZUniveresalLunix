using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using JBZUniveresalLunix.Models;

namespace JBZUniveresalLunix.Services;

/// <summary>
/// Lưu sản lượng riêng cho từng mã hàng. Dữ liệu tồn tại qua lần đổi model,
/// đóng/mở phần mềm và không phụ thuộc vào trạng thái DataGrid hiện tại.
/// </summary>
public sealed class ProductionStatisticsStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly TimeProvider _timeProvider;
    private Dictionary<string, ModelProductionStatistics> _items;
    private List<ProbeMaintenanceRecord> _maintenanceRecords;

    public ProductionStatisticsStore(string path, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _timeProvider = timeProvider ?? TimeProvider.System;

        StatisticsFile file = LoadFile();
        _items = (file.Models ?? [])
            .Select(Migrate)
            .Where(x => !string.IsNullOrWhiteSpace(x.ModelKey))
            .GroupBy(x => x.ModelKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                MergeStatistics,
                StringComparer.OrdinalIgnoreCase);
        _maintenanceRecords = file.MaintenanceRecords ?? [];
        foreach (ProbeMaintenanceRecord record in _maintenanceRecords)
            record.ModelKey = BuildModelKey(record.PartNumber, record.ModelName, record.ModelKey);
    }

    public string StoragePath => _path;
    public string RecoveryNotice { get; private set; } = string.Empty;

    public ModelProductionStatistics Get(ProductModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        lock (_gate)
        {
            string key = BuildModelKey(model);
            if (_items.TryGetValue(key, out ModelProductionStatistics? saved))
            {
                ModelProductionStatistics result = saved.Clone();
                RollPeriods(result, LocalNow());
                return result;
            }

            return new ModelProductionStatistics
            {
                ModelKey = key,
                ModelName = model.ModelName ?? string.Empty,
                PartNumber = model.PartNumber ?? string.Empty,
                SourceFile = model.SourcePath ?? string.Empty
            };
        }
    }

    public ModelProductionStatistics Record(
        ProductModel model,
        bool passed,
        long lotNo,
        string resultText)
    {
        ArgumentNullException.ThrowIfNull(model);

        lock (_gate)
        {
            string key = BuildModelKey(model);
            bool existed = _items.TryGetValue(key, out ModelProductionStatistics? item);
            ModelProductionStatistics? original = item?.Clone();
            if (!existed || item is null)
            {
                item = new ModelProductionStatistics
                {
                    ModelKey = key
                };
                _items[key] = item;
            }

            try
            {
                item.ModelName = model.ModelName ?? string.Empty;
                item.PartNumber = model.PartNumber ?? string.Empty;
                item.SourceFile = model.SourcePath ?? string.Empty;
                DateTime now = LocalNow();
                RollPeriods(item, now);
                checked
                {
                    item.Total++;
                    item.LifetimeTestCount++;
                    item.DailyTestCount++;
                    item.MonthlyTestCount++;
                    if (passed)
                    {
                        item.Pass++;
                        item.DailyPassCount++;
                    }
                    else
                    {
                        item.Fail++;
                        item.DailyFailCount++;
                    }
                }

                item.LastLotNo = lotNo;
                item.LastResult = resultText ?? (passed ? "PASS" : "FAIL");
                item.LastTestedAt = now;
                SaveFile();
                return item.Clone();
            }
            catch
            {
                RestoreItem(key, existed, original);
                throw;
            }
        }
    }

    /// <summary>
    /// Regenerates the legacy JSON cache from authoritative SQLite values.
    /// No production decision may be made from this mirror.
    /// </summary>
    public ModelProductionStatistics Mirror(
        ProductModel model,
        ProductionStatisticsSnapshot statistics,
        ProbeCounterSnapshot probe)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(statistics);
        ArgumentNullException.ThrowIfNull(probe);
        lock (_gate)
        {
            string key = BuildModelKey(model);
            ModelProductionStatistics item = GetOrCreate(model);
            item.Total = checked((int)statistics.LifetimeTotal);
            item.Pass = checked((int)statistics.LifetimePass);
            item.Fail = checked((int)statistics.LifetimeFail);
            item.DailyTestCount = statistics.DailyTotal;
            item.DailyPassCount = statistics.DailyPass;
            item.DailyFailCount = statistics.DailyFail;
            item.MonthlyTestCount = statistics.MonthlyTotal;
            item.LifetimeTestCount = statistics.LifetimeTotal;
            item.LastLotNo = statistics.LastLotNo;
            item.LastResult = statistics.LastResult;
            item.ProbeCycleCount = probe.Counter;
            item.ProbeReplacementThreshold = probe.ReplacementThreshold;
            item.LastTestedAt = LocalNow();
            _items[key] = item;
            SaveFile();
            return item.Clone();
        }
    }

    public ModelProductionStatistics RecordProbeCycle(
        ProductModel model,
        long replacementThreshold)
    {
        ArgumentNullException.ThrowIfNull(model);

        lock (_gate)
        {
            string key = BuildModelKey(model);
            bool existed = _items.TryGetValue(key, out ModelProductionStatistics? saved);
            ModelProductionStatistics? original = saved?.Clone();
            ModelProductionStatistics item = GetOrCreate(model);
            try
            {
                checked { item.ProbeCycleCount++; }
                item.ProbeReplacementThreshold = replacementThreshold;
                SaveFile();
                return item.Clone();
            }
            catch
            {
                RestoreItem(key, existed, original);
                throw;
            }
        }
    }

    public ProbeMaintenanceRecord ResetProbeCycle(
        ProductModel model,
        long replacementThreshold,
        string adminIdentity,
        string station)
    {
        ArgumentNullException.ThrowIfNull(model);

        lock (_gate)
        {
            string key = BuildModelKey(model);
            bool existed = _items.TryGetValue(key, out ModelProductionStatistics? saved);
            ModelProductionStatistics? original = saved?.Clone();
            ModelProductionStatistics item = GetOrCreate(model);
            long previous = item.ProbeCycleCount;
            var record = new ProbeMaintenanceRecord
            {
                Timestamp = LocalNow(),
                ModelKey = item.ModelKey,
                ModelName = item.ModelName,
                PartNumber = item.PartNumber,
                PreviousProbeCycleCount = previous,
                NewProbeCycleCount = 0,
                ReplacementThreshold = replacementThreshold,
                AdminIdentity = adminIdentity ?? string.Empty,
                Station = station ?? string.Empty,
                Action = "PROBE PIN REPLACED"
            };

            item.ProbeCycleCount = 0;
            item.ProbeReplacementThreshold = replacementThreshold;
            item.LastProbeResetAt = record.Timestamp;
            _maintenanceRecords.Add(record);

            try
            {
                SaveFile();
            }
            catch
            {
                RestoreItem(key, existed, original);
                _maintenanceRecords.Remove(record);
                throw;
            }

            return record;
        }
    }

    public IReadOnlyList<ProbeMaintenanceRecord> GetMaintenanceRecords(ProductModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        string key = BuildModelKey(model);
        lock (_gate)
        {
            return _maintenanceRecords
                .Where(record => string.Equals(record.ModelKey, key, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(record => record.Timestamp)
                .Select(record => record.Clone())
                .ToArray();
        }
    }

    public static string BuildModelKey(ProductModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        string sourceKey = string.IsNullOrWhiteSpace(model.SourcePath)
            ? string.Empty
            : $"FILE:{Normalize(Path.GetFileNameWithoutExtension(model.SourcePath))}";
        return BuildModelKey(model.PartNumber, model.ModelName, sourceKey);
    }

    private ModelProductionStatistics GetOrCreate(ProductModel model)
    {
        string key = BuildModelKey(model);
        if (!_items.TryGetValue(key, out ModelProductionStatistics? item))
        {
            item = new ModelProductionStatistics { ModelKey = key };
            _items[key] = item;
        }

        item.ModelName = model.ModelName ?? string.Empty;
        item.PartNumber = model.PartNumber ?? string.Empty;
        item.SourceFile = model.SourcePath ?? string.Empty;
        RollPeriods(item, LocalNow());
        return item;
    }

    private void RestoreItem(
        string key,
        bool existed,
        ModelProductionStatistics? original)
    {
        if (existed && original is not null)
            _items[key] = original;
        else
            _items.Remove(key);
    }

    private StatisticsFile LoadFile()
    {
        try
        {
            if (!File.Exists(_path))
                return new StatisticsFile();

            return DeserializeFile(_path);
        }
        catch
        {
            string backup = _path + ".bak";
            try
            {
                if (File.Exists(backup))
                {
                    StatisticsFile recovered = DeserializeFile(backup);
                    File.Copy(backup, _path, overwrite: true);
                    RecoveryNotice = "production.statistics.json hỏng; đã khôi phục từ bản .bak.";
                    return recovered;
                }
            }
            catch
            {
            }

            // Không ghi đè bằng chứng: giữ bản corrupt để kỹ thuật có thể khôi phục.
            try
            {
                if (File.Exists(_path))
                {
                    string corrupt = _path + $".corrupt.{DateTime.UtcNow:yyyyMMddHHmmssfff}";
                    File.Copy(_path, corrupt, overwrite: false);
                }
            }
            catch
            {
            }

            RecoveryNotice = "production.statistics.json không đọc được; đã giữ bản .corrupt và khởi tạo store mới.";
            return new StatisticsFile();
        }
    }

    private static StatisticsFile DeserializeFile(string path) =>
        JsonSerializer.Deserialize<StatisticsFile>(File.ReadAllText(path, Encoding.UTF8))
        ?? new StatisticsFile();

    private void SaveFile()
    {
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var payload = new StatisticsFile
        {
            Version = 3,
            Models = _items.Values
                .OrderBy(x => x.PartNumber, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.ModelName, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Clone())
                .ToList(),
            MaintenanceRecords = _maintenanceRecords
                .OrderBy(record => record.Timestamp)
                .Select(record => record.Clone())
                .ToList()
        };

        string json = JsonSerializer.Serialize(
            payload,
            new JsonSerializerOptions { WriteIndented = true });

        string temp = _path + ".tmp";
        string backup = _path + ".bak";
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        if (File.Exists(_path))
            File.Replace(temp, _path, backup, ignoreMetadataErrors: true);
        else
            File.Move(temp, _path);
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToUpperInvariant();

    private DateTime LocalNow() => _timeProvider.GetLocalNow().LocalDateTime;

    private static void RollPeriods(ModelProductionStatistics item, DateTime now)
    {
        string day = now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        string month = now.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        if (!string.Equals(item.DailyPeriod, day, StringComparison.Ordinal))
        {
            item.DailyPeriod = day;
            item.DailyTestCount = 0;
            item.DailyPassCount = 0;
            item.DailyFailCount = 0;
        }
        if (!string.Equals(item.MonthlyPeriod, month, StringComparison.Ordinal))
        {
            item.MonthlyPeriod = month;
            item.MonthlyTestCount = 0;
        }
    }

    private static ModelProductionStatistics Migrate(ModelProductionStatistics item)
    {
        item.ModelKey = BuildModelKey(item.PartNumber, item.ModelName, item.ModelKey);
        item.LifetimeTestCount = Math.Max(item.LifetimeTestCount, item.Total);
        if (item.DailyTestCount > 0 &&
            item.DailyPassCount == 0 &&
            item.DailyFailCount == 0)
        {
            // V2 did not persist the daily PASS/FAIL split. Preserve an exact split
            // when the saved production counters all belong to the current period.
            if (item.DailyTestCount == item.Total)
            {
                item.DailyPassCount = item.Pass;
                item.DailyFailCount = item.Fail;
            }
            else
            {
                // The historic file cannot reconstruct the split. Keep the daily
                // total internally consistent without altering lifetime counters.
                item.DailyFailCount = Math.Min(item.DailyTestCount, item.Fail);
                item.DailyPassCount = item.DailyTestCount - item.DailyFailCount;
            }
        }
        return item;
    }

    private static string BuildModelKey(string? partNumber, string? modelName, string? fallbackKey)
    {
        string part = Normalize(partNumber);
        if (part.Length > 0)
            return $"PN:{part}";

        string name = Normalize(modelName);
        if (name.Length > 0)
            return $"MODEL:{name}";

        return string.IsNullOrWhiteSpace(fallbackKey)
            ? "FILE:UNKNOWN"
            : Normalize(fallbackKey);
    }

    private static ModelProductionStatistics MergeStatistics(
        IGrouping<string, ModelProductionStatistics> group)
    {
        ModelProductionStatistics[] items = group.ToArray();
        ModelProductionStatistics latest = items
            .OrderByDescending(item => item.LastTestedAt ?? DateTime.MinValue)
            .First()
            .Clone();

        latest.ModelKey = group.Key;
        latest.Total = checked(items.Sum(item => item.Total));
        latest.Pass = checked(items.Sum(item => item.Pass));
        latest.Fail = checked(items.Sum(item => item.Fail));
        latest.LifetimeTestCount = checked(items.Sum(item => Math.Max(item.LifetimeTestCount, item.Total)));
        latest.ProbeCycleCount = checked(items.Sum(item => item.ProbeCycleCount));
        latest.ProbeReplacementThreshold = items.Max(item => item.ProbeReplacementThreshold);
        latest.LastProbeResetAt = items.Max(item => item.LastProbeResetAt);

        string dailyPeriod = items.MaxBy(item => item.DailyPeriod, StringComparer.Ordinal)?.DailyPeriod
            ?? string.Empty;
        latest.DailyPeriod = dailyPeriod;
        latest.DailyTestCount = checked(items
            .Where(item => string.Equals(item.DailyPeriod, dailyPeriod, StringComparison.Ordinal))
            .Sum(item => item.DailyTestCount));
        latest.DailyPassCount = checked(items
            .Where(item => string.Equals(item.DailyPeriod, dailyPeriod, StringComparison.Ordinal))
            .Sum(item => item.DailyPassCount));
        latest.DailyFailCount = checked(items
            .Where(item => string.Equals(item.DailyPeriod, dailyPeriod, StringComparison.Ordinal))
            .Sum(item => item.DailyFailCount));

        string monthlyPeriod = items.MaxBy(item => item.MonthlyPeriod, StringComparer.Ordinal)?.MonthlyPeriod
            ?? string.Empty;
        latest.MonthlyPeriod = monthlyPeriod;
        latest.MonthlyTestCount = checked(items
            .Where(item => string.Equals(item.MonthlyPeriod, monthlyPeriod, StringComparison.Ordinal))
            .Sum(item => item.MonthlyTestCount));

        return latest;
    }

    private sealed class StatisticsFile
    {
        public int Version { get; set; } = 3;
        public List<ModelProductionStatistics> Models { get; set; } = new();
        public List<ProbeMaintenanceRecord> MaintenanceRecords { get; set; } = new();
    }
}

public sealed class ModelProductionStatistics
{
    public string ModelKey { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string PartNumber { get; set; } = string.Empty;
    public string SourceFile { get; set; } = string.Empty;
    public int Total { get; set; }
    public int Pass { get; set; }
    public int Fail { get; set; }
    public long LastLotNo { get; set; }
    public string LastResult { get; set; } = string.Empty;
    public DateTime? LastTestedAt { get; set; }
    public string DailyPeriod { get; set; } = string.Empty;
    public long DailyTestCount { get; set; }
    public long DailyPassCount { get; set; }
    public long DailyFailCount { get; set; }
    public string MonthlyPeriod { get; set; } = string.Empty;
    public long MonthlyTestCount { get; set; }
    public long LifetimeTestCount { get; set; }
    public long ProbeCycleCount { get; set; }
    public long ProbeReplacementThreshold { get; set; } = 200_000;
    public DateTime? LastProbeResetAt { get; set; }

    public double Rate => Total <= 0 ? 0 : 100.0 * Pass / Total;

    public ModelProductionStatistics Clone() => new()
    {
        ModelKey = ModelKey,
        ModelName = ModelName,
        PartNumber = PartNumber,
        SourceFile = SourceFile,
        Total = Total,
        Pass = Pass,
        Fail = Fail,
        LastLotNo = LastLotNo,
        LastResult = LastResult,
        LastTestedAt = LastTestedAt,
        DailyPeriod = DailyPeriod,
        DailyTestCount = DailyTestCount,
        DailyPassCount = DailyPassCount,
        DailyFailCount = DailyFailCount,
        MonthlyPeriod = MonthlyPeriod,
        MonthlyTestCount = MonthlyTestCount,
        LifetimeTestCount = LifetimeTestCount,
        ProbeCycleCount = ProbeCycleCount,
        ProbeReplacementThreshold = ProbeReplacementThreshold,
        LastProbeResetAt = LastProbeResetAt
    };
}

public sealed class ProbeMaintenanceRecord
{
    public DateTime Timestamp { get; set; }
    public string ModelKey { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string PartNumber { get; set; } = string.Empty;
    public long PreviousProbeCycleCount { get; set; }
    public long NewProbeCycleCount { get; set; }
    public long ReplacementThreshold { get; set; }
    public string AdminIdentity { get; set; } = string.Empty;
    public string Station { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;

    public ProbeMaintenanceRecord Clone() => (ProbeMaintenanceRecord)MemberwiseClone();
}
