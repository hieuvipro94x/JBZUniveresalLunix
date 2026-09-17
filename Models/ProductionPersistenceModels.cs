using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace JBZUniveresalLunix.Models;

public sealed record PartIdentitySnapshot(
    string PartKey,
    string PartNumber,
    string PartName,
    string VehicleType,
    string Eco,
    string Nco,
    string Alc,
    string CustomerCode)
{
    public static PartIdentitySnapshot Capture(ProductModel model)
    {
        string partNumber = model.PartNumber?.Trim() ?? string.Empty;
        string fallback = !string.IsNullOrWhiteSpace(model.ModelName)
            ? model.ModelName.Trim()
            : Path.GetFileNameWithoutExtension(model.SourcePath ?? string.Empty);
        string keyValue = partNumber.Length > 0 ? partNumber : fallback;
        string prefix = partNumber.Length > 0 ? "PN:" : "MODEL:";
        return new(
            prefix + NormalizeKey(keyValue),
            partNumber,
            model.ProductName?.Trim() ?? string.Empty,
            model.VehicleType?.Trim() ?? string.Empty,
            model.Eco?.Trim() ?? string.Empty,
            model.Nco?.Trim() ?? string.Empty,
            model.Alc?.Trim() ?? string.Empty,
            model.CustomerCode?.Trim() ?? string.Empty);
    }

    internal static string NormalizeKey(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "UNKNOWN" : value.Trim().ToUpperInvariant();
}

public sealed record ModelIdentitySnapshot(
    string ModelKey,
    string FilePath,
    string FileName,
    string FileHash,
    long FileLength,
    DateTime? FileModifiedAt,
    string ModelName,
    int MaxIo)
{
    public static ModelIdentitySnapshot Capture(ProductModel model)
    {
        string path = model.SourcePath?.Trim() ?? string.Empty;
        string hash = model.SourceHash?.Trim().ToUpperInvariant() ?? string.Empty;
        string key = hash.Length > 0
            ? "SHA256:" + hash
            : "PATH:" + PartIdentitySnapshot.NormalizeKey(
                path.Length > 0 ? Path.GetFullPath(path) : model.ModelName);
        return new(
            key,
            path,
            Path.GetFileName(path),
            hash,
            Math.Max(0, model.SourceLength),
            model.SourceModifiedAt,
            model.ModelName?.Trim() ?? string.Empty,
            model.MaxIo);
    }
}

public sealed record ProductionConfigSnapshot(
    string ConfigHash,
    string AppVersion,
    string BoardMode,
    int ExpansionCardCount,
    int StartCardNumber,
    int UsbDelay,
    int RelayWiringMode,
    bool JigEjectRelayEnabled,
    bool PassMarkingRelayEnabled,
    int MasterFaultRequiredCount,
    bool UseTestPointer,
    string ResistanceConfigJson,
    string WaterProofConfigJson,
    string LabelConfigJson,
    string MachineConfigJson,
    string FullConfigJson)
{
    public static ProductionConfigSnapshot Capture(
        ProductionSettings settings,
        string appVersion)
    {
        string resistance = CanonicalJson(settings.ResistanceChannels ?? []);
        string waterproof = CanonicalJson(new
        {
            settings.WaterProofMachine,
            Profiles = (settings.WaterProofProfilesByModel ?? [])
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        });
        string label = CanonicalJson(settings.Label);
        string machine = CanonicalJson(new
        {
            settings.DeviceName,
            settings.DeviceNumber,
            settings.OperatorCompany,
            settings.ProductionLine
        });
        string full = CanonicalJson(new
        {
            settings.BoardMode,
            settings.ExpansionCardCount,
            settings.StartCardNumber,
            settings.UsbDelay,
            settings.RelayWiringMode,
            settings.JigEjectRelayEnabled,
            settings.PassMarkingRelayEnabled,
            settings.MasterFaultRequiredCount,
            settings.UseTestPointer,
            Resistance = resistance,
            WaterProof = waterproof,
            Label = label,
            Machine = machine
        });
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)));
        return new(
            hash,
            appVersion,
            settings.BoardMode.ToString(),
            settings.ExpansionCardCount,
            settings.StartCardNumber,
            settings.UsbDelay,
            settings.RelayWiringMode,
            settings.JigEjectRelayEnabled,
            settings.PassMarkingRelayEnabled,
            settings.MasterFaultRequiredCount,
            settings.UseTestPointer,
            resistance,
            waterproof,
            label,
            machine,
            full);
    }

    private static string CanonicalJson<T>(T value) =>
        JsonSerializer.Serialize(value, new JsonSerializerOptions
        {
            PropertyNamingPolicy = null,
            WriteIndented = false
        });
}

public sealed record FaultPersistenceSnapshot(
    int Order,
    ProductFaultType Type,
    string Code,
    string Message,
    int? ExpectedSourceIo,
    int? ExpectedTargetIo,
    int? ActualSourceIo,
    int? ActualTargetIo,
    string ConnectorFrom,
    string PinFrom,
    string ConnectorTo,
    string PinTo,
    string ActualConnectorFrom,
    string ActualPinFrom,
    string ActualConnectorTo,
    string ActualPinTo,
    string WireName,
    string WireColor,
    string RelatedIosJson,
    double? MeasuredResistance,
    double? ResistanceMin,
    double? ResistanceMax)
{
    public static FaultPersistenceSnapshot Capture(FaultDetail fault, int order) => new(
        order,
        fault.Type,
        fault.Code,
        fault.Message ?? string.Empty,
        fault.ExpectedSourceIo,
        fault.ExpectedTargetIo,
        fault.ActualSourceIo,
        fault.ActualTargetIo,
        fault.ConnectorFrom ?? string.Empty,
        fault.PinFrom ?? string.Empty,
        fault.ConnectorTo ?? string.Empty,
        fault.PinTo ?? string.Empty,
        fault.ActualConnectorFrom ?? string.Empty,
        fault.ActualPinFrom ?? string.Empty,
        fault.ActualConnectorTo ?? string.Empty,
        fault.ActualPinTo ?? string.Empty,
        fault.WireName ?? string.Empty,
        fault.WireColor ?? string.Empty,
        JsonSerializer.Serialize(fault.RelatedIos ?? []),
        fault.MeasuredResistance,
        fault.ResistanceMin,
        fault.ResistanceMax);
}

public sealed record ResistancePersistenceSnapshot(
    int Channel,
    string Name,
    double? MeasuredOhm,
    double MinOhm,
    double MaxOhm,
    bool Passed,
    int SampleCount,
    long StabilizationMs)
{
    public static ResistancePersistenceSnapshot Capture(ResistanceResult result) => new(
        result.Channel,
        result.Name ?? string.Empty,
        result.ValueOhm,
        result.MinOhm,
        result.MaxOhm,
        result.Passed,
        result.SampleCount,
        result.StabilizationTimeMs);
}

public sealed record WaterProofPersistenceSnapshot(
    int Channel,
    bool Enabled,
    double FirstPressure,
    double SecondPressure,
    double Leak,
    bool Passed);

public sealed record ProductionResultCommitRequest(
    TestHistoryRecord History,
    PartIdentitySnapshot Part,
    ModelIdentitySnapshot Model,
    ProductionConfigSnapshot Config,
    IReadOnlyList<FaultPersistenceSnapshot> Faults,
    IReadOnlyList<ResistancePersistenceSnapshot> Resistance,
    IReadOnlyList<WaterProofPersistenceSnapshot> WaterProof,
    bool UpdateProductionTotals)
{
    public static ProductionResultCommitRequest Capture(
        TestHistoryRecord history,
        ProductModel model,
        ProductionSettings settings,
        IEnumerable<FaultDetail> faults,
        IEnumerable<ResistanceResult> resistance,
        IEnumerable<WaterProofChannelMeasurement>? waterProof,
        string appVersion)
    {
        TestHistoryRecord immutableHistory = history.ClonePersistenceSnapshot();
        return new(
            immutableHistory,
            PartIdentitySnapshot.Capture(model),
            ModelIdentitySnapshot.Capture(model),
            ProductionConfigSnapshot.Capture(settings, appVersion),
            faults
                .Select(FaultPersistenceSnapshot.Capture)
                .DistinctBy(item => new
                {
                    item.Type,
                    item.ExpectedSourceIo,
                    item.ExpectedTargetIo,
                    item.ActualSourceIo,
                    item.ActualTargetIo,
                    item.WireName,
                    item.MeasuredResistance,
                    item.ResistanceMin,
                    item.ResistanceMax
                })
                .Select((item, order) => item with { Order = order })
                .ToArray(),
            resistance.Select(ResistancePersistenceSnapshot.Capture).ToArray(),
            waterProof?.Select(item => new WaterProofPersistenceSnapshot(
                item.Channel,
                item.Enabled,
                item.FirstPressure,
                item.SecondPressure,
                item.Leak,
                item.Passed)).ToArray() ?? [],
            HistoryInspectionType.IsProduct(history.InspectionType));
    }
}

public sealed record ProductionCommitResult(
    long TestId,
    bool AlreadyCommitted,
    ProductionStatisticsSnapshot Statistics,
    ProbeCounterSnapshot ProbeCounter);

public sealed record ProductionStatisticsSnapshot(
    long DailyTotal,
    long DailyPass,
    long DailyFail,
    long MonthlyTotal,
    long LifetimeTotal,
    long LifetimePass,
    long LifetimeFail,
    long LastLotNo,
    string LastResult);

public sealed record ProbeCounterSnapshot(
    string PartNumber,
    long ReplacementThreshold,
    long Counter);

public sealed record PartModelRelationSnapshot(
    long PartId,
    string PartNumber,
    long ModelId,
    string ModelName,
    string FilePath,
    bool IsDefault,
    DateTime? FirstUsedAt,
    DateTime? LastUsedAt);

public sealed record DatabaseMigrationReport(
    int SchemaVersion,
    long LegacyTests,
    long MigratedTests,
    long MigratedFaults,
    long MalformedFaultJson,
    long Parts,
    long Models,
    long ConfigSnapshots,
    long DuplicateCycles,
    long ProductionPass,
    long ProductionFail);

public sealed record LegacyImportFile(
    string Path,
    long Length,
    DateTime LastWriteTimeUtc,
    bool PassedFile);

public sealed record LegacyImportResult(
    string Path,
    int SourceRecords,
    int ImportedRecords,
    int ExistingRecords,
    bool Unchanged);
