using System.IO;
using System.Globalization;
using System.Text;
using System.Text.Json;
using JBZUniversalTester.Models;

namespace JBZUniversalTester.Services;

public static class ProductionConfigService
{
    private static readonly string[] LegacyTimingKeys =
    [
        nameof(ProductionSettings.IoScanIntervalMs),
        nameof(ProductionSettings.ShortCircuitConfirmMs),
        nameof(ProductionSettings.WrongConnectionConfirmMs),
        nameof(ProductionSettings.ProductSettleTimeMs),
        nameof(ProductionSettings.JigContactUnstableWindowMs),
        nameof(ProductionSettings.ShortConfirmMs)
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string ConfigDirectory => RuntimePaths.AppDirectory;
    public static string ConfigPath => RuntimePaths.ConfigFile;
    public static string JsonPath => RuntimePaths.LegacyProductionJson;
    public static string LegacyCfgPath => RuntimePaths.LegacyConfigFile;

    public static ProductionSettings Load()
    {
        Directory.CreateDirectory(ConfigDirectory);
        ProductionSettings settings;

        try
        {
            if (File.Exists(ConfigPath))
            {
                settings = LoadEnglishCfg(ConfigPath);
            }
            else if (File.Exists(JsonPath))
            {
                string json = File.ReadAllText(JsonPath, Encoding.UTF8);
                settings = JsonSerializer.Deserialize<ProductionSettings>(json, JsonOptions)
                           ?? new ProductionSettings();

                // Migration cũ: Lot chuỗi -> LotNo.
                if (!json.Contains("\"LotNo\"", StringComparison.OrdinalIgnoreCase) &&
                    long.TryParse(settings.Lot, NumberStyles.Integer, CultureInfo.InvariantCulture, out long legacyLot) &&
                    legacyLot >= 0)
                {
                    settings.LotNo = legacyLot;
                }

                // V15.2 migration: cấu hình cũ chỉ có StampDelay="R1,R2".
                // Chỉ migrate khi JSON chưa có các field relay tách riêng.
                if (!json.Contains("\"Relay1JigPulseMs\"", StringComparison.OrdinalIgnoreCase) &&
                    StampDelayParser.TryParse(settings.StampDelay, out int oldR1, out int oldR2))
                {
                    settings.Relay1JigPulseMs = oldR1;
                    settings.Relay2MarkingPulseMs = oldR2;
                }

                if (!json.Contains("\"PassMarkingToJigDelayMs\"", StringComparison.OrdinalIgnoreCase))
                    settings.PassMarkingToJigDelayMs = 430;
            }
            else if (File.Exists(LegacyCfgPath))
            {
                settings = LoadEnglishCfg(LegacyCfgPath);
            }
            else
            {
                settings = new ProductionSettings();
            }
        }
        catch (JsonException ex)
        {
            BackupInvalidConfig(JsonPath);
            AsyncFileLogService.Current.Error($"production.settings.json invalid: {ex.Message}");
            settings = new ProductionSettings();
        }
        catch (Exception ex)
        {
            AsyncFileLogService.Current.Error($"Load production settings failed: {ex.Message}");
            settings = new ProductionSettings();
        }

        Normalize(settings);
        return settings;
    }

    public static void ReloadInto(ProductionSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ProductionSettings source = Load();

        foreach (var property in typeof(ProductionSettings).GetProperties())
        {
            if (property.CanRead && property.CanWrite)
                property.SetValue(target, property.GetValue(source));
        }
    }

    public static void Save(ProductionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(ConfigDirectory);
        Normalize(settings);

        SaveLegacyCfg(settings, ConfigPath);
    }

    /// <summary>
    /// V12: UniversalTester.cfg dùng tên key tiếng Anh 100% và ghi ĐẦY ĐỦ
    /// mọi trường trên màn Cài đặt. Tên method được giữ để code cũ vẫn gọi được.
    /// </summary>
    public static void SaveLegacyCfg(ProductionSettings settings, string path)
    {
        Normalize(settings);
        var lines = new List<string>
        {
            $"[CardCount]{settings.CardCount}",
            $"[ExpansionCardCount]{settings.ExpansionCardCount}",
            $"[StartCardNumber]{settings.StartCardNumber}",
            $"[IoConfirm1]{settings.IoConfirm1}",
            $"[IoConfirmN]{settings.IoConfirmN}",
            $"[UsbDelay]{settings.UsbDelay}",
            $"[MasterFaultRequiredCount]{settings.MasterFaultRequiredCount}",
            $"[WaterproofSerialPort]{settings.WaterproofSerialPort}",
            $"[WaterProofPortName]{settings.WaterProofMachine.PortName}",
            $"[WaterProofBaudRate]{settings.WaterProofMachine.BaudRate}",
            $"[WaterProofAutoConnect]{Bool(settings.WaterProofMachine.AutoConnect)}",
            $"[WaterProofReadTimeoutMs]{settings.WaterProofMachine.ReadTimeoutMs}",
            $"[WaterProofWriteTimeoutMs]{settings.WaterProofMachine.WriteTimeoutMs}",

            $"[LotNo]{settings.LotNo}",
            $"[LotNoDate]{settings.LotNoDate}",
            $"[Lot]{settings.Lot}",
            $"[DeviceName]{settings.DeviceName}",
            $"[DeviceNumber]{settings.DeviceNumber}",
            $"[OperatorCompany]{settings.OperatorCompany}",
            $"[ProductionLine]{settings.ProductionLine}",
            $"[TemperatureTolerance]{F(settings.TemperatureTolerance)}",
            $"[MinimumErrorLogValue]{settings.MinimumErrorLogValue}",
            $"[AutoSaveErrors]{Bool(settings.AutoSaveErrors)}",
            $"[EnableSystemLogs]{Bool(settings.EnableSystemLogs)}",
            $"[ProbeReplacementThreshold]{settings.ProbeReplacementThreshold}",
            $"[Relay1JigPulseMs]{settings.Relay1JigPulseMs}",
            $"[Relay2MarkingPulseMs]{settings.Relay2MarkingPulseMs}",
            $"[JigEjectRelayEnabled]{Bool(settings.JigEjectRelayEnabled)}",
            $"[PassMarkingRelayEnabled]{Bool(settings.PassMarkingRelayEnabled)}",
            $"[PassJigRelayFirst]{Bool(settings.PassJigRelayFirst)}",
            $"[RelayWiringMode]{settings.RelayWiringMode}",
            $"[FaultJigRelayNumber]{settings.FaultJigRelayNumber}",
            $"[PassMarkingToJigDelayMs]{settings.PassMarkingToJigDelayMs}",
            $"[StampDelayMs]{settings.Relay1JigPulseMs},{settings.Relay2MarkingPulseMs}", // compatibility
            $"[OversizeWaitSeconds]{settings.OversizeWaitSeconds}",
            $"[ShieldDelayMs]{settings.ShieldDelay}",
            $"[ResistanceDelayMs]{settings.ResistanceDelayMs}",
            $"[ItemHeight]{settings.ItemHeight}",
            $"[ScrollDelayMs]{settings.ScrollDelay}",
            $"[PageDelayMs]{settings.PageDelay}",
            $"[ShowTitle]{Bool(settings.ShowTitle)}",
            $"[ShowConnector]{Bool(settings.ShowConnector)}",

            $"[LastThtPath]{settings.LastThtPath}",
            $"[LastThtPartKey]{settings.LastThtPartKey}",
            $"[AutoPrintLabelOnPass]{Bool(settings.AutoPrintLabelOnPass)}",
            $"[HistoryDirectory]{settings.HistoryDirectory}",

            $"[LabelPrinterName]{settings.Label.PrinterName}",
            $"[LabelPrinterCom]{settings.Label.PrinterCom}",
            $"[LabelWidthMm]{settings.Label.WidthMm}",
            $"[LabelHeightMm]{settings.Label.HeightMm}",
            $"[LabelFormatName]{settings.Label.FormatName}",
            $"[LabelBaudRate]{settings.Label.BaudRate}",
            $"[LabelWriteTimeoutMs]{settings.Label.WriteTimeoutMs}",
            $"[LabelCopies]{settings.Label.Copies}",
            $"[LabelTemplateType]{settings.Label.TemplateType}",
            $"[LabelTemplatePath]{settings.Label.TemplatePath}",
            $"[LabelTemplateTEMTOBase64]{settings.Label.LargeTemplateOverrideBase64}",
            $"[LabelTemplateTEMBEBase64]{settings.Label.SmallTemplateOverrideBase64}",
            $"[LabelTemplateTEMBEQRBase64]{settings.Label.SmallQrTemplateOverrideBase64}",
            $"[LabelEncodingName]{settings.Label.EncodingName}",
            $"[LabelRawDestination]{settings.Label.RawDestination}",
            $"[LabelExternalHelperPath]{settings.Label.ExternalHelperPath}",
            $"[LabelExternalHelperArgument]{settings.Label.ExternalHelperArgument}",
            $"[LabelExternalPrintFile]{settings.Label.ExternalPrintFile}"
        };

        foreach ((string modelKey, int requiredCount) in settings.MasterFaultCountsByModel
                     .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            string encodedKey = Uri.EscapeDataString(modelKey);
            lines.Add($"[MasterFault.{encodedKey}]{requiredCount}");
        }

        foreach ((string productKey, ProductLotSettings lot) in settings.LotSettingsByProduct
                     .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            string encodedKey = Uri.EscapeDataString(productKey);
            lines.Add(
                $"[ProductLot.{encodedKey}]{Math.Max(0, lot.LotNo)};{lot.LotNoDate};" +
                $"{Math.Max(0, lot.StartLotNo)}");
        }

        foreach ((string modelKey, WaterProofModelSettings profile) in settings.WaterProofProfilesByModel
                     .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            string encodedKey = Uri.EscapeDataString(modelKey);
            lines.Add(
                $"[WaterProof.Model.{encodedKey}]" +
                $"{Bool(profile.Enabled)};{Bool(profile.Channel1Enabled)};{Bool(profile.Channel2Enabled)};" +
                $"{Bool(profile.Channel3Enabled)};{F(profile.PressMin)};{F(profile.LeakLimit)};" +
                $"{profile.PressTimeMs};{profile.WaitTimeMs};" +
                $"{Uri.EscapeDataString(profile.Channel1Connector)};" +
                $"{Uri.EscapeDataString(profile.Channel2Connector)};" +
                $"{Uri.EscapeDataString(profile.Channel3Connector)}");
        }

        foreach ((string modelKey, ResistanceChannelSetting[] profile) in settings.ResistanceProfilesByModel
                     .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            string encodedKey = Uri.EscapeDataString(modelKey);
            foreach (ResistanceChannelSetting resistance in ResistanceMeasurementPlan.Normalize(profile))
            {
                lines.Add(
                    $"[Resistance.Model.{encodedKey}.{resistance.Name}]" +
                    $"{Bool(resistance.Enabled)};{resistance.Channel};" +
                    $"{F(resistance.MinOhm)};{F(resistance.MaxOhm)}");
            }
        }

        foreach (ResistanceChannelSetting resistance in settings.ResistanceChannels)
        {
            lines.Add(
                $"[Resistance.{resistance.Name}]" +
                $"{Bool(resistance.Enabled)};{resistance.Channel};" +
                $"{F(resistance.MinOhm)};{F(resistance.MaxOhm)}");
        }

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        AtomicWrite(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
    }

    public static string GetMasterModelKey(ProductModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!string.IsNullOrWhiteSpace(model.PartNumber))
            return model.PartNumber.Trim();
        if (!string.IsNullOrWhiteSpace(model.ModelName))
            return model.ModelName.Trim();
        return GetMasterModelKeyFromPath(model.SourcePath);
    }

    public static string GetLotProductKey(ProductModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return GetLotProductKey(model.PartNumber, model.SourcePath, model.ModelName);
    }

    public static string GetLotProductKey(
        string? partNumber,
        string? sourcePath,
        string? modelName = null)
    {
        if (!string.IsNullOrWhiteSpace(partNumber))
            return partNumber.Trim();
        if (!string.IsNullOrWhiteSpace(modelName))
            return modelName.Trim();
        return GetMasterModelKeyFromPath(sourcePath);
    }

    public static ProductLotSettings GetOrCreateProductLot(
        ProductionSettings settings,
        string productKey,
        bool migrateCurrentLot)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Normalize(settings);
        string key = string.IsNullOrWhiteSpace(productKey) ? "DEFAULT" : productKey.Trim();
        if (!settings.LotSettingsByProduct.TryGetValue(key, out ProductLotSettings? lot))
        {
            lot = new ProductLotSettings
            {
                StartLotNo = migrateCurrentLot ? Math.Max(0, settings.LotNo) : 0,
                LotNo = migrateCurrentLot ? Math.Max(0, settings.LotNo) : 0,
                LotNoDate = migrateCurrentLot ? settings.LotNoDate : string.Empty
            };
            settings.LotSettingsByProduct[key] = lot;
        }
        return lot;
    }

    public static void SetProductLot(
        ProductionSettings settings,
        string productKey,
        long startLotNo,
        string lotNoDate)
    {
        ProductLotSettings lot = GetOrCreateProductLot(settings, productKey, migrateCurrentLot: false);
        long normalizedStart = Math.Max(0, startLotNo);
        long previousStart = Math.Max(0, lot.StartLotNo);
        string normalizedDate = (lotNoDate ?? string.Empty).Trim();
        if (normalizedStart != previousStart)
        {
            // Đổi base trong cùng ngày giữ số PASS/LOT đã chạy; nếu cấu hình
            // được mở sang ngày mới thì bắt đầu lại đúng base mới.
            bool sameProductionDate = string.Equals(
                lot.LotNoDate,
                normalizedDate,
                StringComparison.Ordinal);
            long progress = sameProductionDate
                ? Math.Max(0, lot.LotNo - previousStart)
                : 0;
            lot.StartLotNo = normalizedStart;
            lot.LotNo = normalizedStart > long.MaxValue - progress
                ? long.MaxValue
                : normalizedStart + progress;
            lot.LotNoDate = normalizedDate;
        }
        settings.LotNo = lot.LotNo;
        settings.LotNoDate = lot.LotNoDate;
    }

    public static string GetMasterModelKeyFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "DEFAULT";
        string file = Path.GetFileNameWithoutExtension(path.Trim());
        return string.IsNullOrWhiteSpace(file) ? "DEFAULT" : file.Trim();
    }

    public static int GetMasterFaultRequiredCount(ProductionSettings settings, ProductModel model)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(model);
        Normalize(settings);

        string primaryKey = GetMasterModelKey(model);
        if (settings.MasterFaultCountsByModel.TryGetValue(primaryKey, out int count))
            return Math.Clamp(count, 0, 99);

        string pathKey = GetMasterModelKeyFromPath(model.SourcePath);
        if (settings.MasterFaultCountsByModel.TryGetValue(pathKey, out count))
            return Math.Clamp(count, 0, 99);

        if (!string.IsNullOrWhiteSpace(model.ModelName) &&
            settings.MasterFaultCountsByModel.TryGetValue(model.ModelName.Trim(), out count))
            return Math.Clamp(count, 0, 99);

        return settings.MasterFaultRequiredCount;
    }

    public static int GetMasterFaultRequiredCountForPath(ProductionSettings settings, string? path)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Normalize(settings);
        string key = GetMasterModelKeyFromPath(path);
        return settings.MasterFaultCountsByModel.TryGetValue(key, out int count)
            ? Math.Clamp(count, 0, 99)
            : settings.MasterFaultRequiredCount;
    }

    public static void SetMasterFaultRequiredCountForPath(ProductionSettings settings, string? path, int count)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Normalize(settings);
        int normalized = Math.Clamp(count, 0, 99);
        string key = GetMasterModelKeyFromPath(path);
        if (string.Equals(key, "DEFAULT", StringComparison.OrdinalIgnoreCase))
            settings.MasterFaultRequiredCount = normalized;
        else
            settings.MasterFaultCountsByModel[key] = normalized;
    }

    public static WaterProofModelSettings GetWaterProofProfileForPath(
        ProductionSettings settings,
        string? path)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Normalize(settings);
        string key = GetMasterModelKeyFromPath(path);
        return settings.WaterProofProfilesByModel.TryGetValue(key, out WaterProofModelSettings? profile)
            ? profile.Clone()
            : new WaterProofModelSettings();
    }

    public static void SetWaterProofProfileForPath(
        ProductionSettings settings,
        string? path,
        WaterProofModelSettings profile)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(profile);
        Normalize(settings);
        string key = GetMasterModelKeyFromPath(path);
        settings.WaterProofProfilesByModel[key] = NormalizeWaterProofProfile(profile.Clone());
    }

    public static ResistanceChannelSetting[] GetResistanceProfileForPath(
        ProductionSettings settings,
        string? path)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Normalize(settings);
        string key = GetMasterModelKeyFromPath(path);
        ResistanceChannelSetting[] source = settings.ResistanceProfilesByModel.TryGetValue(key, out ResistanceChannelSetting[]? profile)
            ? profile
            : settings.ResistanceChannels;
        return ResistanceMeasurementPlan.Normalize(source)
            .Select(CloneResistanceChannel)
            .ToArray();
    }

    public static void SetResistanceProfileForPath(
        ProductionSettings settings,
        string? path,
        IEnumerable<ResistanceChannelSetting> profile)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(profile);
        Normalize(settings);
        string key = GetMasterModelKeyFromPath(path);
        ResistanceChannelSetting[] normalized = ResistanceMeasurementPlan.Normalize(profile.ToArray())
            .Select(CloneResistanceChannel)
            .ToArray();
        settings.ResistanceProfilesByModel[key] = normalized;
        settings.ResistanceChannels = normalized.Select(CloneResistanceChannel).ToArray();
    }

    private static ResistanceChannelSetting CloneResistanceChannel(ResistanceChannelSetting item) => new()
    {
        Enabled = item.Enabled,
        Name = item.Name,
        Channel = item.Channel,
        MinOhm = item.MinOhm,
        MaxOhm = item.MaxOhm
    };

    public static void EnsureSavedOnStartup(ProductionSettings settings)
    {
        try
        {
            if (!File.Exists(ConfigPath) || File.ReadLines(ConfigPath, Encoding.UTF8)
                    .Select(ParseCfgLine)
                    .Any(item => IsInternalOnlyConfigKey(item.Key)))
            {
                Save(settings);
            }
        }
        catch (Exception ex)
        {
            // Không chặn startup khi thư mục chỉ đọc, nhưng vẫn phải lưu dấu vết lỗi.
            AsyncFileLogService.Current.Error($"Sanitize production config failed: {ex.Message}");
        }
    }

    private static ProductionSettings LoadEnglishCfg(string path)
    {
        var settings = new ProductionSettings();
        var map = ReadCfgLines(path)
            .Select(ParseCfgLine)
            .Where(x => x.Key.Length > 0)
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);

        // Migration từ CFG gốc Htdrv: đọc cả key tiếng Hàn đã thấy trong file cũ,
        // sau lần Save tiếp theo toàn bộ file sẽ được ghi lại bằng key tiếng Anh.
        // V12.9 migration: [카드 수] của Htdrv là byte xx của START_SCAN
        // (trace command=4 -> diagnostic 256 I/O), không phải card vật lý 32 I/O.
        // Nếu file mới có ExpansionCardCount thì ưu tiên key mới; nếu không dùng
        // CardCount/카드 수 làm số scan-unit 64 I/O.
        int legacyScanCount = IAny(map, settings.CardCount, "CardCount", "카드 수");
        int expansionModules = I(map, "ExpansionCardCount", legacyScanCount);
        settings.ExpansionCardCount = Math.Clamp(
            expansionModules,
            1,
            BoardCapacity.MaxExpansionCardCount);
        settings.CardCount = settings.ExpansionCardCount;
        settings.IoConfirm1 = IAny(map, settings.IoConfirm1, "IoConfirm1", "IO1 확인");
        settings.IoConfirmN = IAny(map, settings.IoConfirmN, "IoConfirmN", "IOn 확인");
        settings.UsbDelay = IAny(map, settings.UsbDelay, "UsbDelay", "USB 지연");
        settings.StartCardNumber = I(map, "StartCardNumber", settings.StartCardNumber);
        settings.MasterFaultRequiredCount = I(map, "MasterFaultRequiredCount", settings.MasterFaultRequiredCount);
        settings.WaterproofSerialPort = I(map, "WaterproofSerialPort", settings.WaterproofSerialPort);
        settings.WaterProofMachine.PortName = S(map, "WaterProofPortName", settings.WaterProofMachine.PortName);
        settings.WaterProofMachine.BaudRate = I(map, "WaterProofBaudRate", settings.WaterProofMachine.BaudRate);
        settings.WaterProofMachine.AutoConnect = B(map, "WaterProofAutoConnect", settings.WaterProofMachine.AutoConnect);
        settings.WaterProofMachine.ReadTimeoutMs = I(map, "WaterProofReadTimeoutMs", settings.WaterProofMachine.ReadTimeoutMs);
        settings.WaterProofMachine.WriteTimeoutMs = I(map, "WaterProofWriteTimeoutMs", settings.WaterProofMachine.WriteTimeoutMs);

        foreach ((string key, string value) in map)
        {
            const string prefix = "MasterFault.";
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
            {
                continue;
            }

            string modelKey = Uri.UnescapeDataString(key[prefix.Length..]);
            if (!string.IsNullOrWhiteSpace(modelKey))
                settings.MasterFaultCountsByModel[modelKey] = count;
        }

        foreach ((string key, string value) in map)
        {
            const string prefix = "ProductLot.";
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            string productKey = Uri.UnescapeDataString(key[prefix.Length..]);
            string[] parts = value.Split(';');
            if (string.IsNullOrWhiteSpace(productKey) || parts.Length == 0 ||
                !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long lotNo))
            {
                continue;
            }

            settings.LotSettingsByProduct[productKey] = new ProductLotSettings
            {
                LotNo = Math.Max(0, lotNo),
                LotNoDate = parts.Length > 1 ? parts[1].Trim() : string.Empty,
                StartLotNo = parts.Length > 2 &&
                             long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long startLotNo)
                    ? Math.Max(0, startLotNo)
                    : Math.Max(0, lotNo)
            };
        }

        settings.LotNo = L(map, "LotNo", settings.LotNo);
        settings.LotNoDate = S(map, "LotNoDate", settings.LotNoDate);
        settings.Lot = S(map, "Lot", settings.Lot);
        settings.DeviceName = S(map, "DeviceName", settings.DeviceName);
        settings.DeviceNumber = S(map, "DeviceNumber", settings.DeviceNumber);
        settings.OperatorCompany = S(map, "OperatorCompany", settings.OperatorCompany);
        settings.ProductionLine = S(map, "ProductionLine", settings.ProductionLine);
        settings.TemperatureTolerance = D(map, "TemperatureTolerance", settings.TemperatureTolerance);
        settings.MinimumErrorLogValue = I(map, "MinimumErrorLogValue", settings.MinimumErrorLogValue);
        settings.AutoSaveErrors = B(map, "AutoSaveErrors", settings.AutoSaveErrors);
        settings.EnableSystemLogs = B(map, "EnableSystemLogs", settings.EnableSystemLogs);

        settings.IoScanIntervalMs = I(map, "IoScanIntervalMs", settings.IoScanIntervalMs);
        settings.OpenCircuitConfirmMs = I(map, "OpenCircuitConfirmMs", settings.OpenCircuitConfirmMs);
        settings.ShortConfirmMs = I(map, "ShortConfirmMs", settings.ShortConfirmMs);
        settings.ShortCircuitConfirmMs = I(map, "ShortCircuitConfirmMs", settings.ShortCircuitConfirmMs);
        settings.WrongConnectionConfirmMs = I(map, "WrongConnectionConfirmMs", settings.WrongConnectionConfirmMs);
        settings.ProductSettleTimeMs = I(map, "ProductSettleTimeMs", settings.ProductSettleTimeMs);
        settings.JigContactUnstableWindowMs = I(map, "JigContactUnstableWindowMs", settings.JigContactUnstableWindowMs);
        settings.ProbeReplacementThreshold = L(map, "ProbeReplacementThreshold", settings.ProbeReplacementThreshold);
        settings.StampDelay = SAny(map, settings.StampDelay, "StampDelayMs", "스탬프 지연(msec)");
        bool hasSplitRelay = map.ContainsKey("Relay1JigPulseMs") || map.ContainsKey("Relay2MarkingPulseMs");
        if (hasSplitRelay)
        {
            settings.Relay1JigPulseMs = I(map, "Relay1JigPulseMs", settings.Relay1JigPulseMs);
            settings.Relay2MarkingPulseMs = I(map, "Relay2MarkingPulseMs", settings.Relay2MarkingPulseMs);
        }
        else if (StampDelayParser.TryParse(settings.StampDelay, out int legacyR1, out int legacyR2))
        {
            settings.Relay1JigPulseMs = legacyR1;
            settings.Relay2MarkingPulseMs = legacyR2;
        }
        settings.PassMarkingToJigDelayMs = I(map, "PassMarkingToJigDelayMs", settings.PassMarkingToJigDelayMs);
        settings.JigEjectRelayEnabled = B(map, "JigEjectRelayEnabled", settings.JigEjectRelayEnabled);
        settings.PassMarkingRelayEnabled = B(map, "PassMarkingRelayEnabled", settings.PassMarkingRelayEnabled);
        settings.PassJigRelayFirst = B(map, "PassJigRelayFirst", settings.PassJigRelayFirst);
        settings.FaultJigRelayNumber = I(map, "FaultJigRelayNumber", settings.FaultJigRelayNumber);
        settings.RelayWiringMode = map.ContainsKey("RelayWiringMode")
            ? I(map, "RelayWiringMode", settings.RelayWiringMode)
            : settings.PassJigRelayFirst || settings.FaultJigRelayNumber == 2
                ? 1
                : 0;
        settings.OversizeWaitSeconds = I(map, "OversizeWaitSeconds", settings.OversizeWaitSeconds);
        settings.ShieldDelay = I(map, "ShieldDelayMs", settings.ShieldDelay);
        settings.ResistanceDelayMs = I(map, "ResistanceDelayMs", settings.ResistanceDelayMs);
        settings.ItemHeight = I(map, "ItemHeight", settings.ItemHeight);
        settings.ScrollDelay = I(map, "ScrollDelayMs", settings.ScrollDelay);
        settings.PageDelay = I(map, "PageDelayMs", settings.PageDelay);
        settings.ShowTitle = B(map, "ShowTitle", settings.ShowTitle);
        settings.ShowConnector = B(map, "ShowConnector", settings.ShowConnector);

        settings.LastThtPath = S(map, "LastThtPath", settings.LastThtPath);
        settings.LastThtPartKey = S(map, "LastThtPartKey", settings.LastThtPartKey);
        settings.AutoPrintLabelOnPass = B(map, "AutoPrintLabelOnPass", settings.AutoPrintLabelOnPass);
        settings.HistoryDirectory = S(map, "HistoryDirectory", settings.HistoryDirectory);

        settings.Label.PrinterName = S(map, "LabelPrinterName", settings.Label.PrinterName);
        settings.Label.PrinterCom = S(map, "LabelPrinterCom", settings.Label.PrinterCom);
        settings.Label.WidthMm = I(map, "LabelWidthMm", settings.Label.WidthMm);
        settings.Label.HeightMm = I(map, "LabelHeightMm", settings.Label.HeightMm);
        settings.Label.FormatName = S(map, "LabelFormatName", settings.Label.FormatName);
        settings.Label.BaudRate = I(map, "LabelBaudRate", settings.Label.BaudRate);
        settings.Label.WriteTimeoutMs = I(map, "LabelWriteTimeoutMs", settings.Label.WriteTimeoutMs);
        settings.Label.Copies = I(map, "LabelCopies", settings.Label.Copies);
        settings.Label.TemplateType = S(map, "LabelTemplateType", settings.Label.TemplateType);
        settings.Label.TemplatePath = S(map, "LabelTemplatePath", settings.Label.TemplatePath);
        settings.Label.LargeTemplateOverrideBase64 = S(map, "LabelTemplateTEMTOBase64", settings.Label.LargeTemplateOverrideBase64);
        settings.Label.SmallTemplateOverrideBase64 = S(map, "LabelTemplateTEMBEBase64", settings.Label.SmallTemplateOverrideBase64);
        settings.Label.SmallQrTemplateOverrideBase64 = S(map, "LabelTemplateTEMBEQRBase64", settings.Label.SmallQrTemplateOverrideBase64);
        settings.Label.EncodingName = S(map, "LabelEncodingName", settings.Label.EncodingName);
        settings.Label.RawDestination = S(map, "LabelRawDestination", settings.Label.RawDestination);
        settings.Label.ExternalHelperPath = S(map, "LabelExternalHelperPath", settings.Label.ExternalHelperPath);
        settings.Label.ExternalHelperArgument = S(map, "LabelExternalHelperArgument", settings.Label.ExternalHelperArgument);
        settings.Label.ExternalPrintFile = S(map, "LabelExternalPrintFile", settings.Label.ExternalPrintFile);

        foreach ((string key, string value) in map)
        {
            const string prefix = "WaterProof.Model.";
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            string modelKey = Uri.UnescapeDataString(key[prefix.Length..]);
            if (string.IsNullOrWhiteSpace(modelKey))
                continue;

            string[] parts = value.Split(';');
            var profile = new WaterProofModelSettings();
            if (parts.Length > 0) profile.Enabled = ParseBool(parts[0], profile.Enabled);
            if (parts.Length > 1) profile.Channel1Enabled = ParseBool(parts[1], profile.Channel1Enabled);
            if (parts.Length > 2) profile.Channel2Enabled = ParseBool(parts[2], profile.Channel2Enabled);
            if (parts.Length > 3) profile.Channel3Enabled = ParseBool(parts[3], profile.Channel3Enabled);
            if (parts.Length > 4 && double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out double pressMin)) profile.PressMin = pressMin;
            if (parts.Length > 5 && double.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out double leakLimit)) profile.LeakLimit = leakLimit;
            if (parts.Length > 6 && int.TryParse(parts[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pressTime)) profile.PressTimeMs = pressTime;
            if (parts.Length > 7 && int.TryParse(parts[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out int waitTime)) profile.WaitTimeMs = waitTime;
            if (parts.Length > 8) profile.Channel1Connector = Uri.UnescapeDataString(parts[8]);
            if (parts.Length > 9) profile.Channel2Connector = Uri.UnescapeDataString(parts[9]);
            if (parts.Length > 10) profile.Channel3Connector = Uri.UnescapeDataString(parts[10]);
            settings.WaterProofProfilesByModel[modelKey] = NormalizeWaterProofProfile(profile);
        }

        foreach (ResistanceChannelSetting channel in settings.ResistanceChannels)
        {
            if (!map.TryGetValue($"Resistance.{channel.Name}", out string? text) ||
                string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            string[] parts = text.Split(';');
            if (parts.Length > 0) channel.Enabled = ParseBool(parts[0], channel.Enabled);
            if (parts.Length > 1 && int.TryParse(parts[1], out int c)) channel.Channel = c;
            if (parts.Length > 2 && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double min)) channel.MinOhm = min;
            if (parts.Length > 3 && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double max)) channel.MaxOhm = max;
        }

        foreach ((string key, string value) in map)
        {
            const string prefix = "Resistance.Model.";
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            string remainder = key[prefix.Length..];
            int separator = remainder.LastIndexOf('.');
            if (separator <= 0 || separator >= remainder.Length - 1)
                continue;
            string modelKey = Uri.UnescapeDataString(remainder[..separator]);
            string channelName = remainder[(separator + 1)..];
            ResistanceChannelSetting[] profile = settings.ResistanceProfilesByModel.TryGetValue(modelKey, out ResistanceChannelSetting[]? existing)
                ? existing
                : ResistanceMeasurementPlan.Normalize([]);
            ResistanceChannelSetting? channel = profile.FirstOrDefault(item =>
                string.Equals(item.Name, channelName, StringComparison.OrdinalIgnoreCase));
            if (channel is null)
                continue;
            string[] parts = value.Split(';');
            if (parts.Length > 0) channel.Enabled = ParseBool(parts[0], channel.Enabled);
            if (parts.Length > 1 && int.TryParse(parts[1], out int c)) channel.Channel = c;
            if (parts.Length > 2 && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double min)) channel.MinOhm = min;
            if (parts.Length > 3 && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double max)) channel.MaxOhm = max;
            settings.ResistanceProfilesByModel[modelKey] = profile;
        }

        return settings;
    }

    private static void Normalize(ProductionSettings settings)
    {
        if (!Enum.IsDefined(typeof(BoardMode), settings.BoardMode))
            settings.BoardMode = BoardMode.Auto;

        settings.Label ??= new LabelSettings();
        settings.ResistanceChannels ??= [];
        settings.WaterProofMachine ??= new WaterProofMachineSettings();
        settings.WaterProofProfilesByModel ??= new Dictionary<string, WaterProofModelSettings>(StringComparer.OrdinalIgnoreCase);
        settings.ResistanceProfilesByModel ??= new Dictionary<string, ResistanceChannelSetting[]>(StringComparer.OrdinalIgnoreCase);
        settings.MasterFaultCountsByModel ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        settings.LotSettingsByProduct ??= new Dictionary<string, ProductLotSettings>(StringComparer.OrdinalIgnoreCase);
        if (settings.MasterFaultCountsByModel.Comparer != StringComparer.OrdinalIgnoreCase)
        {
            settings.MasterFaultCountsByModel = new Dictionary<string, int>(
                settings.MasterFaultCountsByModel,
                StringComparer.OrdinalIgnoreCase);
        }
        if (settings.WaterProofProfilesByModel.Comparer != StringComparer.OrdinalIgnoreCase)
        {
            settings.WaterProofProfilesByModel = new Dictionary<string, WaterProofModelSettings>(
                settings.WaterProofProfilesByModel,
                StringComparer.OrdinalIgnoreCase);
        if (settings.ResistanceProfilesByModel.Comparer != StringComparer.OrdinalIgnoreCase)
            settings.ResistanceProfilesByModel = new Dictionary<string, ResistanceChannelSetting[]>(
                settings.ResistanceProfilesByModel, StringComparer.OrdinalIgnoreCase);
        }
        if (settings.LotSettingsByProduct.Comparer != StringComparer.OrdinalIgnoreCase)
        {
            settings.LotSettingsByProduct = new Dictionary<string, ProductLotSettings>(
                settings.LotSettingsByProduct,
                StringComparer.OrdinalIgnoreCase);
        }

        // V12.9.5: Master luôn tự động trong Production. Không còn đường manual song song.
        settings.AutoMasterSequence = true;
        settings.MasterFaultRequiredCount = Math.Clamp(settings.MasterFaultRequiredCount, 0, 99);

        foreach (string key in settings.MasterFaultCountsByModel.Keys.ToArray())
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                settings.MasterFaultCountsByModel.Remove(key);
                continue;
            }
            settings.MasterFaultCountsByModel[key] = Math.Clamp(settings.MasterFaultCountsByModel[key], 0, 99);
        }

        if (settings.LotNo < 0) settings.LotNo = 0;
        settings.LotNoDate = (settings.LotNoDate ?? string.Empty).Trim();
        foreach (string key in settings.LotSettingsByProduct.Keys.ToArray())
        {
            ProductLotSettings? lot = settings.LotSettingsByProduct[key];
            if (string.IsNullOrWhiteSpace(key) || lot is null)
            {
                settings.LotSettingsByProduct.Remove(key);
                continue;
            }
            if (lot.StartLotNo < 0)
                lot.StartLotNo = lot.LotNo;
            lot.StartLotNo = Math.Max(0, lot.StartLotNo);
            lot.LotNo = Math.Max(0, lot.LotNo);
            lot.LotNoDate = (lot.LotNoDate ?? string.Empty).Trim();
        }
        ProductionTimingPolicy.Normalize(settings);
        settings.UsbDelay = Math.Clamp(settings.UsbDelay, 1, 16);
        settings.IoConfirm1 = Math.Clamp(settings.IoConfirm1, 0, 127);
        settings.IoConfirmN = Math.Clamp(settings.IoConfirmN, 0, 31);
        settings.ExpansionCardCount = Math.Clamp(
            settings.ExpansionCardCount,
            1,
            BoardCapacity.MaxExpansionCardCount);

        settings.StartCardNumber = Math.Clamp(
            settings.StartCardNumber,
            1,
            BoardCapacity.MaxExpansionCardCount);
        settings.ExpansionCardCount = Math.Min(
            settings.ExpansionCardCount,
            BoardCapacity.MaxExpansionCardCount - settings.StartCardNumber + 1);

        BoardCapacity capacity = BoardCapacity.FromSettings(settings);
        settings.CardCount = capacity.ScanCardCount;
        settings.WaterproofSerialPort = Math.Clamp(settings.WaterproofSerialPort, 0, 999);
        settings.WaterProofMachine.PortName = (settings.WaterProofMachine.PortName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(settings.WaterProofMachine.PortName) && settings.WaterproofSerialPort > 0)
            settings.WaterProofMachine.PortName = $"COM{settings.WaterproofSerialPort}";
        // Máy Leak dùng giao thức cố định 115200 8N1; không cho cấu hình cũ
        // hoặc dữ liệu nhập tay làm runtime mở sai tốc độ.
        settings.WaterProofMachine.BaudRate = WaterProofMachineSettings.DefaultBaudRate;
        settings.WaterProofMachine.ReadTimeoutMs = Math.Clamp(settings.WaterProofMachine.ReadTimeoutMs, 100, 30_000);
        settings.WaterProofMachine.WriteTimeoutMs = Math.Clamp(settings.WaterProofMachine.WriteTimeoutMs, 100, 30_000);
        if (settings.WaterProofMachine.PortName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(settings.WaterProofMachine.PortName[3..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int legacyCom))
        {
            settings.WaterproofSerialPort = Math.Clamp(legacyCom, 0, 999);
        }

        foreach (string key in settings.WaterProofProfilesByModel.Keys.ToArray())
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                settings.WaterProofProfilesByModel.Remove(key);
                continue;
            }
            settings.WaterProofProfilesByModel[key] = NormalizeWaterProofProfile(
                settings.WaterProofProfilesByModel[key] ?? new WaterProofModelSettings());
        }

        // V15.2: ba thông số relay độc lập. 50..5000 ms tránh pulse bằng 0 hoặc giữ relay quá lâu do nhập nhầm.
        settings.Relay1JigPulseMs = Math.Clamp(settings.Relay1JigPulseMs, 50, 5_000);
        settings.Relay2MarkingPulseMs = Math.Clamp(settings.Relay2MarkingPulseMs, 50, 5_000);
        settings.RelayWiringMode = Math.Clamp(settings.RelayWiringMode, 0, 1);
        // Hai khóa cũ tiếp tục được ghi để bản cũ đọc cấu hình mới an toàn.
        settings.PassJigRelayFirst = settings.RelayWiringMode == 1;
        settings.FaultJigRelayNumber = settings.RelayWiringMode == 1 ? 2 : 1;
        settings.PassMarkingToJigDelayMs = Math.Clamp(settings.PassMarkingToJigDelayMs, 0, 5_000);
        settings.StampDelay = $"{settings.Relay1JigPulseMs},{settings.Relay2MarkingPulseMs}"; // compatibility only

        settings.OversizeWaitSeconds = Math.Clamp(settings.OversizeWaitSeconds, 0, 86_400);
        settings.ShieldDelay = Math.Clamp(settings.ShieldDelay, 0, 60_000);
        settings.ResistanceDelayMs = Math.Clamp(settings.ResistanceDelayMs, 0, 60_000);
        settings.ItemHeight = Math.Clamp(settings.ItemHeight, 24, 80);
        settings.ScrollDelay = Math.Clamp(settings.ScrollDelay, 0, 5000);
        settings.PageDelay = Math.Clamp(settings.PageDelay, 0, 5000);
        settings.MinimumErrorLogValue = Math.Max(0, settings.MinimumErrorLogValue);

        settings.HistoryDirectory = string.IsNullOrWhiteSpace(settings.HistoryDirectory)
            ? "Data"
            : settings.HistoryDirectory.Trim();

        settings.Label.WidthMm = Math.Clamp(settings.Label.WidthMm, 20, 200);
        settings.Label.HeightMm = Math.Clamp(settings.Label.HeightMm, 10, 150);
        settings.Label.FormatName = string.IsNullOrWhiteSpace(settings.Label.FormatName) ? "KS91" : settings.Label.FormatName.Trim();
        settings.Label.BaudRate = Math.Clamp(settings.Label.BaudRate, 1200, 921600);
        settings.Label.WriteTimeoutMs = Math.Clamp(settings.Label.WriteTimeoutMs, 500, 30_000);
        settings.Label.Copies = 1;
        settings.Label.TemplateType = LabelProfileResolver.NormalizeTemplateType(settings.Label.TemplateType);
        settings.Label.TemplatePath = (settings.Label.TemplatePath ?? string.Empty).Trim();
        settings.Label.LargeTemplateOverrideBase64 = (settings.Label.LargeTemplateOverrideBase64 ?? string.Empty).Trim();
        settings.Label.SmallTemplateOverrideBase64 = (settings.Label.SmallTemplateOverrideBase64 ?? string.Empty).Trim();
        settings.Label.SmallQrTemplateOverrideBase64 = (settings.Label.SmallQrTemplateOverrideBase64 ?? string.Empty).Trim();
        settings.Label.EncodingName = string.IsNullOrWhiteSpace(settings.Label.EncodingName)
            ? "us-ascii"
            : settings.Label.EncodingName.Trim();
        settings.Label.RawDestination = (settings.Label.RawDestination ?? string.Empty).Trim();
        settings.Label.ExternalHelperPath = (settings.Label.ExternalHelperPath ?? string.Empty).Trim();
        settings.Label.ExternalHelperArgument = (settings.Label.ExternalHelperArgument ?? string.Empty).Trim();
        settings.Label.ExternalPrintFile = string.IsNullOrWhiteSpace(settings.Label.ExternalPrintFile)
            ? "print.txt"
            : settings.Label.ExternalPrintFile.Trim();

        EnsureResistanceChannels(settings);
    }

    private static WaterProofModelSettings NormalizeWaterProofProfile(WaterProofModelSettings profile)
    {
        profile.Channel1Connector = (profile.Channel1Connector ?? string.Empty).Trim();
        profile.Channel2Connector = (profile.Channel2Connector ?? string.Empty).Trim();
        profile.Channel3Connector = (profile.Channel3Connector ?? string.Empty).Trim();
        profile.PressMin = Math.Max(0, profile.PressMin);
        profile.LeakLimit = Math.Max(0, profile.LeakLimit);
        profile.PressTimeMs = Math.Clamp(profile.PressTimeMs, 1, 300_000);
        profile.WaitTimeMs = Math.Clamp(profile.WaitTimeMs, 1, 300_000);
        return profile;
    }

    private static void EnsureResistanceChannels(ProductionSettings settings)
    {
        settings.ResistanceChannels = ResistanceMeasurementPlan.Normalize(
            settings.ResistanceChannels,
            message => AsyncFileLogService.Current.Error($"Resistance configuration: {message}"));
    }

    private static (string Key, string Value) ParseCfgLine(string raw)
    {
        string line = raw.Trim();
        if (!line.StartsWith('[')) return (string.Empty, string.Empty);
        int close = line.IndexOf(']');
        if (close <= 1) return (string.Empty, string.Empty);
        return (line[1..close].Trim(), line[(close + 1)..].Trim());
    }

    private static bool IsInternalOnlyConfigKey(string key) =>
        key.StartsWith("App.", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("Version", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("BoardMode", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("UseTestPointer", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("ManualModeEnabled", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("AutoMasterSequence", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("SettingsPassword", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("DiscardPassword", StringComparison.OrdinalIgnoreCase);

    private static string S(Dictionary<string, string> map, string key, string fallback) => map.TryGetValue(key, out string? value) ? value : fallback;
    private static string SAny(Dictionary<string, string> map, string fallback, params string[] keys)
    {
        foreach (string key in keys)
            if (map.TryGetValue(key, out string? value)) return value;
        return fallback;
    }
    private static int I(Dictionary<string, string> map, string key, int fallback) => map.TryGetValue(key, out string? value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : fallback;
    private static int IAny(Dictionary<string, string> map, int fallback, params string[] keys)
    {
        foreach (string key in keys)
            if (map.TryGetValue(key, out string? value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) return n;
        return fallback;
    }
    private static long L(Dictionary<string, string> map, string key, long fallback) => map.TryGetValue(key, out string? value) && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) ? n : fallback;
    private static double D(Dictionary<string, string> map, string key, double fallback) => map.TryGetValue(key, out string? value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) ? n : fallback;
    private static bool B(Dictionary<string, string> map, string key, bool fallback) => map.TryGetValue(key, out string? value) ? ParseBool(value, fallback) : fallback;
    private static bool ParseBool(string value, bool fallback) => value.Trim() switch { "1" => true, "0" => false, _ when bool.TryParse(value, out bool b) => b, _ => fallback };
    private static int Bool(bool value) => value ? 1 : 0;
    private static string F(double value) => value.ToString(CultureInfo.InvariantCulture);

    private static void BackupInvalidConfig(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;

            string directory = Path.GetDirectoryName(path) ?? ConfigDirectory;
            string name = Path.GetFileNameWithoutExtension(path);
            string extension = Path.GetExtension(path);
            string backup = Path.Combine(
                directory,
                $"{name}.invalid_{DateTime.Now:yyyyMMdd_HHmmss}{extension}");
            File.Copy(path, backup, overwrite: true);
        }
        catch
        {
        }
    }

    private static void AtomicWrite(string path, string text)
    {
        string tmp = path + ".tmp";
        try
        {
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(text);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                try { File.Replace(tmp, path, null, true); }
                catch (PlatformNotSupportedException) { File.Move(tmp, path, overwrite: true); }
                catch (IOException) { File.Move(tmp, path, overwrite: true); }
            }
            else
            {
                File.Move(tmp, path);
            }
        }
        finally
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
        }
    }

    internal static string[] ReadCfgLines(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        string text;
        try
        {
            text = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            text = Encoding.GetEncoding(949).GetString(bytes);
        }

        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
    }
}
