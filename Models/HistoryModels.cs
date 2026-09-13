namespace JBZUniversalTester.Models;

public sealed class TestHistoryRecord
{
    private CustomerFaultDisplay? _customerFault;

    public long Id { get; set; }
    public DateTime Started { get; set; }
    public DateTime Finished { get; set; }
    public DateTime? InstallStartedAt { get; set; }
    public DateTime? TestStartedAt { get; set; }
    public DateTime? ResultAt { get; set; }
    public DateTime? RemovalStartedAt { get; set; }
    public DateTime? RemovedAt { get; set; }
    public string InspectionType { get; set; } = HistoryInspectionType.Product;
    public string PartName { get; set; } = string.Empty;
    public string PartNumber { get; set; } = string.Empty;
    public string VehicleType { get; set; } = string.Empty;
    public string Eco { get; set; } = string.Empty;
    public string Nco { get; set; } = string.Empty;
    public string Alc { get; set; } = string.Empty;
    public long LotNo { get; set; }
    public long ProductionCounter { get; set; }
    public string Result { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public string ModelName { get; set; } = string.Empty;
    public string ModelFile { get; set; } = string.Empty;
    public string HtdrvName { get; set; } = string.Empty;
    public string LotText { get; set; } = string.Empty;
    public string InspectionTrace { get; set; } = string.Empty;
    public int OpenCount { get; set; }
    public int WrongCount { get; set; }
    public int ShortCount { get; set; }
    public string Resistance { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string DeviceNumber { get; set; } = string.Empty;
    public string OperatorCompany { get; set; } = string.Empty;
    public string ProductionLine { get; set; } = string.Empty;
    public string FaultType { get; set; } = string.Empty;
    public string FaultCode { get; set; } = string.Empty;
    public int? ExpectedSourceIo { get; set; }
    public int? ExpectedTargetIo { get; set; }
    public int? ActualSourceIo { get; set; }
    public int? ActualTargetIo { get; set; }
    public string FaultDetailsJson { get; set; } = string.Empty;
    public string FaultSummary { get; set; } = string.Empty;
    public double? MeasuredResistance { get; set; }
    public double? ResistanceMin { get; set; }
    public double? ResistanceMax { get; set; }
    public string CycleId { get; set; } = string.Empty;
    public string LabelSerial { get; set; } = string.Empty;
    public string BarcodeValue { get; set; } = string.Empty;
    public string LabelProfile { get; set; } = string.Empty;
    public string LabelTemplateType { get; set; } = string.Empty;
    public string LabelPayload { get; set; } = string.Empty;
    public string PrintStatus { get; set; } = LabelPrintStatus.NotRequested.ToString();
    public DateTime? PrintTimestamp { get; set; }
    public string Printer { get; set; } = string.Empty;
    public int LabelCopies { get; set; }
    public int ReprintCount { get; set; }
    public string PrintMessage { get; set; } = string.Empty;

    public TestHistoryRecord ClonePersistenceSnapshot() => (TestHistoryRecord)MemberwiseClone();

    // Cột Ngày/Thời gian của History biểu diễn thời điểm BẮT ĐẦU TEST.
    // Việc sắp xếp/query/cursor trong TestHistoryStore phải dùng cùng timestamp này.
    public string DateText => EffectiveTestStartedAt.ToString("yyyy/MM/dd");
    public string TimeText => EffectiveTestStartedAt.ToString("HH:mm:ss");
    public string PassedText => Passed ? "PASS" : "FAIL";
    public string LabelTypeText => !string.IsNullOrWhiteSpace(LabelTemplateType)
        ? LabelTemplateType
        : LabelProfile;
    // BarcodeValue chỉ được chốt sau khi sản phẩm PASS và máy in xác nhận thành công.
    // LabelPayload là toàn bộ lệnh máy in, không được đưa vào cột 바코드출력.
    public string BarcodeOutputText => BarcodeValue ?? string.Empty;
    public string ExportModelFileName => System.IO.Path.GetFileName(ModelFile ?? string.Empty);
    public string ExportLotText => LotText ?? string.Empty;
    public bool IsProductionRecord => HistoryInspectionType.IsProduct(InspectionType);
    public bool IsMasterRecord => HistoryInspectionType.IsMaster(InspectionType);
    public string InspectionTypeText => HistoryInspectionType.KoreanName(InspectionType);
    public string ExportProgressText => Passed ? "1/1" : "0/1";
    public string ExportResultText => Passed ? "합격" : "불량";
    public long? ExportAcceptedLotNo => IsProductionRecord && Passed && LotNo > 0 ? LotNo : null;
    public long? ExportSequenceNo => IsProductionRecord && Passed
        ? ExportAcceptedLotNo ?? (ProductionCounter > 0 ? ProductionCounter : null)
        : null;
    public string ExportBarcodeInputText => string.Empty;
    public string ExportBarcodeText =>
        IsProductionRecord &&
        Passed &&
        string.Equals(PrintStatus, LabelPrintStatus.Printed.ToString(), StringComparison.OrdinalIgnoreCase)
            ? BarcodeOutputText
            : string.Empty;
    public string ExportPercentText => string.Empty;
    public string ExportIncomingInspectionText => string.Empty;
    public string ExportResistanceText => KoreanHistoryFormatter.FormatResistanceSummary(Resistance);
    public DateTime EffectiveInstallStartedAt => InstallStartedAt ?? Started;
    public DateTime EffectiveTestStartedAt => TestStartedAt ?? Started;
    public DateTime EffectiveResultAt => ResultAt ?? Finished;
    public double? InstallDurationSeconds => DurationSeconds(EffectiveInstallStartedAt, TestStartedAt);
    public double? TestDurationSeconds => DurationSeconds(TestStartedAt, EffectiveResultAt);
    public double? RemovalDurationSeconds => DurationSeconds(RemovalStartedAt, RemovedAt);
    public string InstallDurationText => FormatDuration(InstallDurationSeconds);
    public string TestDurationText => FormatDuration(TestDurationSeconds);
    public string RemovalDurationText => FormatDuration(RemovalDurationSeconds);
    public string ExportTestLogText
    {
        get
        {
            var text = new System.Text.StringBuilder();
            if (InstallDurationSeconds is double installSeconds)
            {
                text.Append("장착 ")
                    .Append(EffectiveInstallStartedAt.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture))
                    .Append('~')
                    .Append(EffectiveTestStartedAt.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture))
                    .Append('(')
                    .Append(installSeconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))
                    .Append("초) ");
            }

            text.Append(EffectiveTestStartedAt.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture))
                .Append(" 검사시작 ");

            string trace = InspectionTrace?.Trim() ?? string.Empty;
            if (trace.Length == 0)
            {
                bool continuityPassed = Passed ||
                    FaultCode?.Contains("RESISTANCE", StringComparison.OrdinalIgnoreCase) == true ||
                    FaultCode?.Contains("WATERPROOF", StringComparison.OrdinalIgnoreCase) == true ||
                    FaultType?.Contains("ĐIỆN TRỞ", StringComparison.OrdinalIgnoreCase) == true ||
                    FaultType?.Contains("KÍN NƯỚC", StringComparison.OrdinalIgnoreCase) == true;
                trace = $"{EffectiveResultAt:HH:mm:ss} 회로검사:{(continuityPassed ? "PASS" : "FAIL")}";
            }
            text.Append(trace);

            if (!Passed || InspectionType == HistoryInspectionType.MasterBad)
            {
                string fault = KoreanHistoryFormatter.FormatFaults(this);
                if (!string.IsNullOrWhiteSpace(fault))
                    text.Append(" [").Append(fault).Append(']');
            }

            if (RemovalStartedAt is DateTime removalStarted)
            {
                text.Append(" 탈거 ")
                    .Append(removalStarted.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture))
                    .Append('~')
                    .Append(RemovedAt is DateTime removed
                        ? removed.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
                        : "미확인");
                if (RemovalDurationSeconds is double removalSeconds)
                {
                    text.Append('(')
                        .Append(removalSeconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))
                        .Append("초)");
                }
            }

            return text.ToString();
        }
    }
    public string ExportContentText
    {
        get
        {
            var parts = new List<string>();
            if (!Passed || InspectionType == HistoryInspectionType.MasterBad)
            {
                string fault = KoreanHistoryFormatter.FormatFaults(this);
                if (!string.IsNullOrWhiteSpace(fault))
                    parts.Add(fault);
            }
            if (!string.IsNullOrWhiteSpace(LabelTypeText))
                parts.Add($"[라벨]{LabelTypeText.Trim()}");
            if (!string.IsNullOrWhiteSpace(PrintStatus))
                parts.Add($"[인쇄]{KoreanPrintStatus(PrintStatus)}");
            if (!string.IsNullOrWhiteSpace(DeviceName))
                parts.Add($"[검사기]{DeviceName.Trim()}");
            return string.Join(' ', parts);
        }
    }
    public string ExpectedConnectionText => ExpectedSourceIo is int source && ExpectedTargetIo is int target
        ? $"IO {source} → IO {target}"
        : string.Empty;
    public string ActualConnectionText => ActualSourceIo is int source && ActualTargetIo is int target
        ? $"IO {source} → IO {target}"
        : string.Empty;

    public CustomerFaultDisplay CustomerFault =>
        _customerFault ??= FaultDisplayFormatter.FormatCustomer(this);

    private static double? DurationSeconds(DateTime? start, DateTime? end)
    {
        if (start is not DateTime from || end is not DateTime to || to < from)
            return null;

        return Math.Round((to - from).TotalSeconds, 3, MidpointRounding.AwayFromZero);
    }

    private static string FormatDuration(double? seconds) =>
        seconds?.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    private static string KoreanPrintStatus(string? value) => value?.Trim() switch
    {
        nameof(LabelPrintStatus.Printed) => "완료",
        nameof(LabelPrintStatus.Failed) => "실패",
        nameof(LabelPrintStatus.Pending) => "진행중",
        nameof(LabelPrintStatus.NotRequested) => "미요청",
        _ => "상태불명"
    };

}

public static class HistoryInspectionType
{
    public const string Product = "PRODUCT";
    public const string LeakRetest = "LEAK_RETEST";
    public const string MasterGood = "MASTER_GOOD";
    public const string MasterBad = "MASTER_BAD";

    public static bool IsProduct(string? value) =>
        string.Equals(value, Product, StringComparison.OrdinalIgnoreCase);

    public static bool IsLeakRetest(string? value) =>
        string.Equals(value, LeakRetest, StringComparison.OrdinalIgnoreCase);

    public static bool IsMaster(string? value) =>
        string.Equals(value, MasterGood, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, MasterBad, StringComparison.OrdinalIgnoreCase);

    public static string KoreanName(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        MasterGood => "마스터 합격품",
        MasterBad => "마스터 불량품",
        LeakRetest => "LEAK RETEST",
        _ => "제품"
    };
}

public sealed record HistorySearchCriteria(
    DateTime? From,
    DateTime? To,
    long? LotNo,
    string PartKeyword,
    string Result,
    int MaxRows = 200,
    int Offset = 0,
    string InspectionType = "",
    string FaultType = "",
    int? Io = null,
    string WireName = "",
    string CycleId = "",
    string AppVersion = "",
    DateTime? AfterHistoryAt = null,
    long? AfterId = null);

public sealed record HistorySummary(
    long Total,
    long ProductTotal,
    long ProductPass,
    long ProductFail,
    long MasterTotal,
    long LeakRetestTotal)
{
    public double ProductPassRate => ProductTotal == 0
        ? 0
        : ProductPass * 100d / ProductTotal;
}

public sealed record HistoryPartOption(string Keyword, string Display)
{
    public override string ToString() => Display;
}

public sealed record LabelPrintData(
    string PartName,
    string PartNumber,
    string Eco,
    string Nco,
    string Alc,
    long LotNo,
    DateTime TestedAt,
    string VehicleType = "",
    string CustomerCode = "",
    string CycleId = "",
    string Barcode = "",
    string BarcodePrint = "");

public enum LabelPrintStatus
{
    NotRequested,
    Pending,
    Printed,
    Failed,
    Unknown
}

/// <summary>
/// Immutable snapshot created at PASS confirmation. Printing must never read the
/// mutable current model or Settings UI after this request has been created.
/// </summary>
public sealed record LabelPrintRequest(
    string CycleId,
    LabelPrintData Data,
    string Payload,
    LabelProfile Profile,
    string ModelName,
    string ModelFile,
    string PrinterName,
    string PrinterCom,
    int WidthMm,
    int HeightMm,
    string FormatName,
    int BaudRate,
    int WriteTimeoutMs,
    int Copies,
    string RawDestination,
    string ExternalHelperPath,
    string ExternalHelperArgument,
    string ExternalPrintFile)
{
    public string Printer => !string.IsNullOrWhiteSpace(PrinterCom)
        ? PrinterCom.Trim()
        : PrinterName.Trim();

    public static LabelPrintRequest Capture(
        TestHistoryRecord history,
        ProductModel model,
        LabelSettings settings)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(settings);

        var data = new LabelPrintData(
            model.ProductName,
            model.PartNumber,
            model.Eco,
            model.Nco,
            model.Alc,
            history.LotNo,
            history.Finished,
            model.VehicleType,
            model.CustomerCode,
            history.CycleId);

        string templateType =
            JBZUniversalTester.Services.LabelProfileResolver.NormalizeTemplateType(settings.TemplateType);
        bool isSmallLabel = templateType == LabelSettings.SmallTemplate;
        bool isSmallQrLabel = templateType == LabelSettings.SmallQrTemplate;
        LabelIdentity identity = JBZUniversalTester.Services.EplLabelService.BuildIdentity(
            data,
            includeAlcLotSuffix: !isSmallLabel && !isSmallQrLabel);
        data = data with { Barcode = identity.BarcodeValue, BarcodePrint = identity.BarcodeValue };
        IReadOnlyDictionary<string, string> variables =
            JBZUniversalTester.Services.LabelVariableResolver.Resolve(model, data, settings);

        if (isSmallLabel)
        {
            string barcode = variables["SMALL_LABEL_BARCODE"];
            data = data with { Barcode = barcode, BarcodePrint = barcode };
            variables = JBZUniversalTester.Services.LabelVariableResolver.Resolve(model, data, settings);
        }
        else if (isSmallQrLabel)
        {
            string barcode = variables["SMALL_QR_BARCODE"];
            data = data with { Barcode = barcode, BarcodePrint = barcode };
            variables = JBZUniversalTester.Services.LabelVariableResolver.Resolve(model, data, settings);
        }
        else if (!string.IsNullOrWhiteSpace(model.LabelTemplate.BarcodeTemplate))
        {
            Dictionary<string, string> barcodeVariables = variables.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
            if (barcodeVariables.TryGetValue("LOT_NO_BARCODE", out string? barcodeLot))
            {
                // BarcodeTemplate là ngữ cảnh mã vạch riêng. Giữ tương thích
                // template cũ dùng LOT/LOTNO/SEQUENCE bằng cách chỉ đưa LOT có
                // hậu tố ALC vào lần render này; payload chữ vẫn dùng LOT gốc.
                barcodeVariables["LOT"] = barcodeLot;
                barcodeVariables["LOT_NO"] = barcodeLot;
                barcodeVariables["SEQUENCE"] = barcodeLot;
            }

            string barcode = JBZUniversalTester.Services.LabelTemplateRenderer.Render(
                model.LabelTemplate.BarcodeTemplate,
                barcodeVariables,
                model.PartNumber,
                model.LabelTemplate.ProfileId);
            data = data with { Barcode = barcode, BarcodePrint = barcode };
            variables = JBZUniversalTester.Services.LabelVariableResolver.Resolve(model, data, settings);
        }

        LabelProfile profile = JBZUniversalTester.Services.LabelProfileResolver.Resolve(model, settings);
        string template = JBZUniversalTester.Services.LabelTemplateProvider.Load(profile, model.LabelTemplate.RawTemplate);
        string payload = JBZUniversalTester.Services.LabelTemplateRenderer.Render(
            profile, template, variables, model.PartNumber);

        // Máy in EPL đời cũ có thể nhận byte (đèn COM nháy) nhưng vẫn giữ job
        // trong bộ đệm nếu phần dữ liệu biến/P1 chỉ kết thúc bằng LF. File mẫu
        // 60-15 của Htdrv dùng CRLF đồng nhất. P1 cũng phải có CRLF kết thúc;
        // nếu thiếu, máy in giữ job QR đến khi job kế tiếp gửi thêm byte.
        // Chỉ chuẩn hóa profile QR đã xác minh bị trộn CRLF/LF; không thay đổi
        // payload của TEM_BE, TEM_TO hoặc template tùy chỉnh khác.
        if (isSmallQrLabel)
            payload = JBZUniversalTester.Services.LabelTemplateRenderer.NormalizeEplJob(payload);

        if (isSmallLabel)
        {
            JBZUniversalTester.Services.AsyncFileLogService.Current.Application(
                $"[LABEL] Type={LabelSettings.SmallTemplate} PartNumber={data.PartNumber} " +
                $"YearCode={variables["YEAR_CODE"]} MonthCode={variables["MONTH_CODE"]} " +
                $"DayCode={variables["DAY_CODE"]} Lot={variables["LOT_NO"]} Barcode={data.Barcode}",
                JBZUniversalTester.Services.AppLogLevel.Diagnostic);
        }
        else if (isSmallQrLabel)
        {
            JBZUniversalTester.Services.AsyncFileLogService.Current.Application(
                $"[LABEL] Type={LabelSettings.SmallQrTemplate} PartNumber={data.PartNumber} " +
                $"Date={variables["DATE_YYMMDD"]} Lot={variables["LOT_NO"]} QR={data.Barcode}",
                JBZUniversalTester.Services.AppLogLevel.Diagnostic);
        }

        return new LabelPrintRequest(
            history.CycleId,
            data,
            payload,
            profile,
            history.ModelName,
            history.ModelFile,
            settings.PrinterName ?? string.Empty,
            settings.PrinterCom ?? string.Empty,
            Math.Clamp(settings.WidthMm, 20, 200),
            Math.Clamp(settings.HeightMm, 10, 150),
            settings.FormatName ?? string.Empty,
            Math.Clamp(settings.BaudRate, 1200, 921600),
            Math.Clamp(settings.WriteTimeoutMs, 500, 30_000),
            profile.Copies,
            settings.RawDestination ?? string.Empty,
            profile.ExternalHelperPath,
            profile.ExternalHelperArgument,
            profile.ExternalPrintFile);
    }
}

public sealed record LabelIdentity(string SerialText, string BarcodeValue);

public sealed record LabelPrintTransportResult(bool Printed, string Message);

public sealed record LabelPrinterConnectionResult(bool Connected, string Message);
