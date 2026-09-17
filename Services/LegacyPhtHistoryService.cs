using System.Globalization;
using System.IO;
using System.Text;
using JBZUniveresalLunix.Models;

namespace JBZUniveresalLunix.Services;

/// <summary>
/// Appends records compatible with the original PHT20 history files. PASS uses
/// the verified UTF-8 pipe layout; ERR keeps the legacy CP949 layout. Existing
/// files are never truncated or rewritten.
/// </summary>
public sealed class LegacyPhtHistoryService
{
    private const int AppendRetryCount = 20;
    private const int AppendRetryDelayMs = 25;
    private static readonly object AppendGate = new();
    private static readonly Encoding KoreanEncoding = CreateKoreanEncoding();
    private static readonly Encoding PassEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly string _passRoot;
    private readonly string _errorRoot;
    private readonly bool _enabled;

    public LegacyPhtHistoryService(
        string? passRoot = null,
        string? errorRoot = null,
        bool enabled = true)
    {
        _passRoot = string.IsNullOrWhiteSpace(passRoot) ? RuntimePaths.PassRoot : Path.GetFullPath(passRoot);
        _errorRoot = string.IsNullOrWhiteSpace(errorRoot) ? RuntimePaths.ErrorRoot : Path.GetFullPath(errorRoot);
        _enabled = enabled;
    }

    public string AppendProduct(
        ProductModel model,
        CompletedTestResult result,
        long lotNo)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(result);
        if (!_enabled)
            return string.Empty;
        lotNo = Math.Max(0, lotNo);

        string path;
        string record;
        if (result.Passed)
        {
            path = BuildDailyPath(_passRoot, result.Finished, ".dat");
            record = BuildPassRecord(model, result.Finished, lotNo, "1");
        }
        else
        {
            path = BuildDailyPath(_errorRoot, result.Finished, ".err");
            record = BuildErrorRecord(model, result, lotNo);
        }

        Append(path, record, result.Passed ? PassEncoding : KoreanEncoding);
        return path;
    }

    public string AppendMaster(
        ProductModel model,
        DateTime finished,
        long counter,
        bool goodMaster)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!_enabled)
            return string.Empty;
        string path = BuildDailyPath(_passRoot, finished, ".dat");
        string status = goodMaster ? "[정상마스터]101" : "[불량마스터]201";
        Append(path, BuildPassRecord(model, finished, Math.Max(0, counter), status), PassEncoding);
        return path;
    }

    internal static string BuildPassRecord(
        ProductModel model,
        DateTime finished,
        long counter,
        string status)
    {
        string date = finished.ToString("yyMMdd", CultureInfo.InvariantCulture);
        string time = finished.ToString("HHmmss", CultureInfo.InvariantCulture);
        string part = ResolvePartNumber(model);
        string counterText = counter.ToString("D4", CultureInfo.InvariantCulture);
        string productName = CleanField(model.ProductName);
        string vehicleType = CleanField(model.VehicleType);
        string eco = CleanField(model.Eco);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"|{status}|..|{date}|{time}|{date}{counterText}|||{part}{counterText}|" +
            $"{productName}|{vehicleType}|{eco}||||\r\n");
    }

    internal static string BuildErrorRecord(
        ProductModel model,
        CompletedTestResult result,
        long lotNo)
    {
        string qualifiers = BuildFaultQualifiers(result.Faults);
        string measurement = BuildMeasurementText(result.Resistance);
        string header = string.Create(
            CultureInfo.InvariantCulture,
            $"[정상마스터{qualifiers}] *{CleanField(model.ProductName)}; " +
            $"{CleanField(model.PartNumber)}; {CleanField(model.VehicleType)}; " +
            $"{CleanField(model.CustomerCode)}; ; ; {lotNo:D4} " +
            $"{result.Started:yyyy/MM/dd HH:mm:ss} - {result.Finished:HH:mm:ss}{measurement}|");

        var lines = new List<string> { header };
        foreach (FaultDetail fault in result.Faults
                     .OrderBy(item => FaultTypeCatalog.Priority(item.Type))
                     .ThenBy(item => PrimaryIo(item)))
        {
            AppendFaultLines(lines, fault);
        }

        lines.Add(string.Empty);
        return string.Join("\r\n", lines) + "\r\n";
    }

    private static void AppendFaultLines(List<string> lines, FaultDetail fault)
    {
        int source = fault.ActualSourceIo
            ?? fault.ExpectedSourceIo
            ?? fault.RelatedIos.FirstOrDefault(io => io > 0);
        int target = fault.ActualTargetIo
            ?? fault.ExpectedTargetIo
            ?? fault.RelatedIos.FirstOrDefault(io => io > 0 && io != source);

        if (source > 0)
            lines.Add($" >검사 IO:{source}");

        switch (fault.Type)
        {
            case ProductFaultType.ShortCircuit:
            case ProductFaultType.WrongWiring:
                if (target > 0)
                    lines.Add($" -합선 IO:{target}");
                if (fault.Type == ProductFaultType.WrongWiring &&
                    fault.ExpectedTargetIo is int expected &&
                    expected > 0 && expected != target)
                {
                    lines.Add($"  단선 IO:{expected}");
                }
                break;

            case ProductFaultType.OpenCircuit:
                if (target > 0)
                    lines.Add($"  단선 IO:{target}");
                break;

            case ProductFaultType.ResistanceOutOfRange:
                lines.Add($" 측정오류 {CleanField(fault.Message)}".TrimEnd());
                break;

            case ProductFaultType.WaterProofLeak:
                lines.Add($" 누수 {CleanField(fault.Message)}".TrimEnd());
                break;

            default:
                lines.Add($" 오류 {CleanField(fault.Message)}".TrimEnd());
                break;
        }
    }

    private static string BuildFaultQualifiers(IReadOnlyList<FaultDetail> faults)
    {
        bool hasShort = faults.Any(fault =>
            fault.Type is ProductFaultType.ShortCircuit or ProductFaultType.WrongWiring);
        bool hasOpen = faults.Any(fault => fault.Type == ProductFaultType.OpenCircuit);
        return (hasShort, hasOpen) switch
        {
            (true, true) => " Short Open",
            (true, false) => " Short",
            (false, true) => " Open",
            _ => string.Empty
        };
    }

    private static string BuildMeasurementText(IReadOnlyList<ResistanceResult> resistance)
    {
        if (resistance.Count == 0)
            return string.Empty;

        string values = string.Join(", ", resistance.Select((item, index) =>
            $"{MeasurementOrdinal(index + 1)} {FormatResistance(item)}"));
        return $"측정({values})|";
    }

    private static string FormatResistance(ResistanceResult result)
    {
        if (result.IsOpen || result.ValueOhm is null || !double.IsFinite(result.ValueOhm.Value))
            return "∞Ω";

        double ohm = result.ValueOhm.Value;
        if (Math.Abs(ohm) >= 1_000_000)
            return $"{ohm / 1_000_000:0.000}MΩ";
        if (Math.Abs(ohm) >= 1_000)
            return $"{ohm / 1_000:0.000}KΩ";
        return $"{ohm:0.000}Ω";
    }

    private static string MeasurementOrdinal(int ordinal) => ordinal switch
    {
        1 => "①", 2 => "②", 3 => "③", 4 => "④", 5 => "⑤",
        6 => "⑥", 7 => "⑦", 8 => "⑧", 9 => "⑨", 10 => "⑩",
        _ => $"{ordinal}."
    };

    private static int PrimaryIo(FaultDetail fault) =>
        fault.ActualSourceIo
        ?? fault.ExpectedSourceIo
        ?? fault.ActualTargetIo
        ?? fault.ExpectedTargetIo
        ?? fault.RelatedIos.FirstOrDefault();

    private static string BuildDailyPath(string root, DateTime timestamp, string extension) =>
        Path.Combine(
            root,
            $"Year{timestamp:yyyy}",
            $"Month{timestamp:MM}",
            $"Day{timestamp:dd}{extension}");

    private static string ResolvePartNumber(ProductModel model) =>
        !string.IsNullOrWhiteSpace(model.PartNumber)
            ? CleanField(model.PartNumber)
            : !string.IsNullOrWhiteSpace(model.ModelName)
                ? CleanField(model.ModelName)
                : CleanField(Path.GetFileNameWithoutExtension(model.SourcePath));

    private static string CleanField(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal)
                .Replace("|", " ", StringComparison.Ordinal)
                .Replace(";", " ", StringComparison.Ordinal)
                .Trim();

    private static void Append(string path, string text, Encoding encoding)
    {
        byte[] payload = encoding.GetBytes(text);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        lock (AppendGate)
        {
            IOException? lastError = null;
            for (int attempt = 0; attempt < AppendRetryCount; attempt++)
            {
                try
                {
                    using var stream = new FileStream(
                        path,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.Read,
                        bufferSize: 4096,
                        FileOptions.WriteThrough);
                    stream.Write(payload, 0, payload.Length);
                    stream.Flush(flushToDisk: true);
                    return;
                }
                catch (IOException ex)
                {
                    lastError = ex;
                    Thread.Sleep(AppendRetryDelayMs);
                }
            }

            throw new IOException(
                $"Không thể append lịch sử dùng chung sau {AppendRetryCount} lần: {path}",
                lastError);
        }
    }

    private static Encoding CreateKoreanEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(
            949,
            new EncoderReplacementFallback("?"),
            DecoderFallback.ExceptionFallback);
    }
}
