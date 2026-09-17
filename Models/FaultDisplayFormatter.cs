using System.Globalization;
using System.Text.Json;

namespace JBZUniveresalLunix.Models;

public sealed record FaultDisplayLine(string Label, string Value);

public sealed record OperatorFaultDisplay(
    string Title,
    IReadOnlyList<FaultDisplayLine> Lines);

public sealed record CustomerFaultDisplay(
    string FaultType,
    string FaultLocation,
    string Standard,
    string Actual,
    string Deviation,
    string Assessment);

/// <summary>
/// Converts canonical structured fault data into operator Vietnamese or
/// customer-facing technical English. It never parses a localized sentence to
/// recover fault semantics; FaultCode/FaultDetail remain the source of truth.
/// </summary>
public static class FaultDisplayFormatter
{
    public static string OperatorInstruction(ProductFaultType type) => type switch
    {
        ProductFaultType.OpenCircuit => "KIỂM TRA LỖI HỞ MẠCH",
        ProductFaultType.WrongWiring => "KIỂM TRA LỖI SAI DÂY",
        ProductFaultType.ShortCircuit => "KIỂM TRA LỖI CHẬP MẠCH",
        ProductFaultType.ResistanceOutOfRange => "KIỂM TRA LỖI ĐIỆN TRỞ",
        ProductFaultType.WaterProofLeak => "KIỂM TRA LỖI KÍN NƯỚC",
        ProductFaultType.SystemDeviceError => "LỖI HỆ THỐNG KIỂM TRA",
        _ => "KIỂM TRA SẢN PHẨM"
    };

    public static string OperatorFaultType(ProductFaultType type) =>
        OperatorFaultType(FaultTypeCatalog.Code(type));

    public static string OperatorFaultType(string? code, string? legacyName = null) =>
        NormalizeSemanticCode(code, legacyName) switch
        {
            "OPEN_CIRCUIT" => "HỞ MẠCH",
            "SHORT_CIRCUIT" => "CHẬP MẠCH",
            "WRONG_POSITION" => "SAI VỊ TRÍ",
            "WRONG_WIRE_COLOR" => "SAI MÀU DÂY",
            "TERMINAL_MISPOSITION" => "TERMINAL SAI VỊ TRÍ",
            "CROSSED_TERMINALS" => "ĐẢO VỊ TRÍ TERMINAL",
            "WRONG_WIRING" or "WRONG_CONNECTION" => "SAI KẾT NỐI",
            "RESISTANCE_OUT_OF_RANGE" => "ĐIỆN TRỞ KHÔNG ĐẠT",
            "WATERPROOF_LEAK" => "KÍN NƯỚC KHÔNG ĐẠT",
            "VOLTAGE_OUT_OF_RANGE" => "ĐIỆN ÁP KHÔNG ĐẠT",
            "CURRENT_OUT_OF_RANGE" => "DÒNG ĐIỆN KHÔNG ĐẠT",
            "SYSTEM_DEVICE_ERROR" => "LỖI THIẾT BỊ / HỆ THỐNG",
            _ => "LỖI CHƯA PHÂN LOẠI"
        };

    public static string CustomerFaultType(ProductFaultType type) =>
        CustomerFaultType(FaultTypeCatalog.Code(type));

    public static string CustomerFaultType(string? code, string? legacyName = null) =>
        NormalizeSemanticCode(code, legacyName) switch
        {
            "OPEN_CIRCUIT" => "OPEN CIRCUIT",
            "SHORT_CIRCUIT" => "SHORT CIRCUIT",
            "WRONG_POSITION" => "INCORRECT WIRE POSITION",
            "WRONG_WIRE_COLOR" => "INCORRECT WIRE COLOR",
            "TERMINAL_MISPOSITION" => "TERMINAL MISPOSITION",
            "CROSSED_TERMINALS" => "CROSSED TERMINALS",
            "WRONG_WIRING" or "WRONG_CONNECTION" => "INCORRECT CONNECTION",
            "RESISTANCE_OUT_OF_RANGE" => "RESISTANCE OUT OF SPECIFICATION",
            "WATERPROOF_LEAK" => "WATERPROOF / LEAK TEST FAILURE",
            "VOLTAGE_OUT_OF_RANGE" => "VOLTAGE OUT OF SPECIFICATION",
            "CURRENT_OUT_OF_RANGE" => "CURRENT OUT OF SPECIFICATION",
            "SYSTEM_DEVICE_ERROR" => "TEST SYSTEM ERROR",
            _ => "UNCLASSIFIED FAULT"
        };

    public static OperatorFaultDisplay FormatOperator(FaultDetail fault)
    {
        ArgumentNullException.ThrowIfNull(fault);
        var lines = new List<FaultDisplayLine>();

        switch (fault.Type)
        {
            case ProductFaultType.OpenCircuit:
                Add(lines, "Kết nối tiêu chuẩn", StandardConnection(fault, vietnamese: true));
                Add(lines, "Màu dây tiêu chuẩn", FormatColor(fault.WireColor, vietnamese: true));
                Add(lines, "Thực tế", "KHÔNG CÓ KẾT NỐI");
                break;

            case ProductFaultType.WrongWiring:
                Add(lines, "Dây", fault.WireName);
                Add(lines, "Màu tiêu chuẩn", FormatColor(fault.WireColor, vietnamese: true));
                Add(lines, "Vị trí tiêu chuẩn", StandardConnection(fault, vietnamese: true));
                Add(lines, "Vị trí thực tế", ActualConnection(fault, vietnamese: true));
                break;

            case ProductFaultType.ShortCircuit:
                Add(lines, "Phát hiện kết nối ngoài tiêu chuẩn", ActualConnection(fault, vietnamese: true));
                Add(lines, "Tiêu chuẩn", "KHÔNG ĐƯỢC CÓ KẾT NỐI");
                Add(lines, "Thực tế", "CÓ KẾT NỐI");
                break;

            case ProductFaultType.ResistanceOutOfRange:
                AddResistanceOperatorLines(lines, fault);
                break;

            case ProductFaultType.WaterProofLeak:
                Add(lines, "Kết quả kín nước", fault.Message);
                break;

            default:
                Add(lines, "Vị trí", ActualConnection(fault, vietnamese: true));
                Add(lines, "Chi tiết", fault.Message);
                break;
        }

        if (lines.Count == 0)
            Add(lines, "Chi tiết", string.IsNullOrWhiteSpace(fault.Message) ? "KHÔNG CÓ DỮ LIỆU CHI TIẾT" : fault.Message);

        return new OperatorFaultDisplay(OperatorInstruction(fault.Type), lines);
    }

    public static CustomerFaultDisplay FormatCustomer(FaultDetail fault)
    {
        ArgumentNullException.ThrowIfNull(fault);

        string standard = string.Empty;
        string actual = string.Empty;
        string deviation = string.Empty;
        string assessment = CustomerFaultType(fault.Type);
        string location = StandardConnection(fault, vietnamese: false);

        switch (fault.Type)
        {
            case ProductFaultType.OpenCircuit:
                standard = JoinParts(
                    Prefix("Standard Connection", location),
                    Prefix("Standard Wire Color", FormatColor(fault.WireColor, vietnamese: false)));
                actual = "NO CONTINUITY";
                break;

            case ProductFaultType.WrongWiring:
                standard = JoinParts(
                    Prefix("Standard Position", StandardConnection(fault, vietnamese: false)),
                    Prefix("Standard Wire Color", FormatColor(fault.WireColor, vietnamese: false)));
                actual = Prefix("Actual Position", ActualConnection(fault, vietnamese: false));
                location = string.IsNullOrWhiteSpace(fault.WireName)
                    ? ActualConnection(fault, vietnamese: false)
                    : fault.WireName;
                break;

            case ProductFaultType.ShortCircuit:
                location = ActualConnection(fault, vietnamese: false);
                standard = "NO CONNECTION PERMITTED";
                actual = "CONTINUITY DETECTED";
                assessment = "UNEXPECTED CONNECTION";
                break;

            case ProductFaultType.ResistanceOutOfRange:
                location = fault.WireName;
                BuildResistanceDisplay(
                    fault,
                    out string limits,
                    out string measured,
                    out deviation,
                    out string operatorAssessment,
                    out assessment);
                standard = limits;
                actual = measured;
                break;

            case ProductFaultType.WaterProofLeak:
                location = fault.WireName;
                standard = "WATERPROOF / LEAK SPECIFICATION";
                actual = fault.Message;
                assessment = "LEAK TEST FAILED";
                break;

            default:
                location = ActualConnection(fault, vietnamese: false);
                actual = fault.Type == ProductFaultType.SystemDeviceError
                    ? "DEVICE OR TEST SYSTEM ERROR"
                    : string.Empty;
                break;
        }

        return new CustomerFaultDisplay(
            CustomerFaultType(fault.Type),
            location,
            standard,
            actual,
            deviation,
            assessment);
    }

    public static CustomerFaultDisplay FormatCustomer(TestHistoryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Passed ||
            (string.IsNullOrWhiteSpace(record.FaultCode) &&
             string.IsNullOrWhiteSpace(record.FaultType) &&
             string.IsNullOrWhiteSpace(record.FaultDetailsJson)))
        {
            return new CustomerFaultDisplay(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        }

        FaultDetail? structured = DeserializeFaults(record.FaultDetailsJson)
            .OrderBy(fault => FaultTypeCatalog.Priority(fault.Type))
            .FirstOrDefault();

        if (structured is not null)
            return FormatCustomer(structured);

        var fallback = new FaultDetail
        {
            Type = ParseProductFaultType(record.FaultCode),
            ExpectedSourceIo = record.ExpectedSourceIo,
            ExpectedTargetIo = record.ExpectedTargetIo,
            ActualSourceIo = record.ActualSourceIo,
            ActualTargetIo = record.ActualTargetIo,
            MeasuredResistance = record.MeasuredResistance,
            ResistanceMin = record.ResistanceMin,
            ResistanceMax = record.ResistanceMax
        };
        CustomerFaultDisplay display = FormatCustomer(fallback);
        return display with
        {
            FaultType = CustomerFaultType(record.FaultCode, record.FaultType)
        };
    }

    public static string CustomerSummary(FaultDetail fault) =>
        CustomerSummary(FormatCustomer(fault));

    public static string CustomerSummary(CustomerFaultDisplay display) =>
        JoinParts(
            Prefix("Fault Location", display.FaultLocation),
            Prefix("Standard", display.Standard),
            Prefix("Actual", display.Actual),
            Prefix("Deviation", display.Deviation),
            Prefix("Assessment", display.Assessment));

    public static IReadOnlyList<FaultDetail> DeserializeFaults(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<FaultDetail[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
        catch (NotSupportedException)
        {
            return [];
        }
    }

    private static ProductFaultType ParseProductFaultType(string? code) =>
        NormalizeSemanticCode(code, null) switch
        {
            "OPEN_CIRCUIT" => ProductFaultType.OpenCircuit,
            "SHORT_CIRCUIT" => ProductFaultType.ShortCircuit,
            "WRONG_WIRING" or "WRONG_CONNECTION" => ProductFaultType.WrongWiring,
            "RESISTANCE_OUT_OF_RANGE" => ProductFaultType.ResistanceOutOfRange,
            "WATERPROOF_LEAK" => ProductFaultType.WaterProofLeak,
            "SYSTEM_DEVICE_ERROR" => ProductFaultType.SystemDeviceError,
            _ => ProductFaultType.None
        };

    private static string NormalizeSemanticCode(string? code, string? legacyName)
    {
        string value = string.IsNullOrWhiteSpace(code) ? legacyName?.Trim() ?? string.Empty : code.Trim();
        return value.ToUpperInvariant() switch
        {
            "OPENCIRCUIT" or "OPEN CIRCUIT" or "DÂY CHƯA KẾT NỐI" or "HỞ MẠCH" => "OPEN_CIRCUIT",
            "SHORTCIRCUIT" or "SHORT CIRCUIT" or "CHẬP MẠCH" => "SHORT_CIRCUIT",
            "WRONGPOSITION" or "WRONG POSITION" or "INCORRECT WIRE POSITION" or "SAI VỊ TRÍ" => "WRONG_POSITION",
            "WRONGWIRECOLOR" or "WRONG WIRE COLOR" or "INCORRECT WIRE COLOR" or "SAI MÀU DÂY" => "WRONG_WIRE_COLOR",
            "TERMINALMISPOSITION" or "TERMINAL MISPOSITION" or "TERMINAL SAI VỊ TRÍ" => "TERMINAL_MISPOSITION",
            "CROSSEDTERMINALS" or "CROSSED TERMINALS" or "ĐẢO VỊ TRÍ TERMINAL" => "CROSSED_TERMINALS",
            "WRONGWIRING" or "WRONGCONNECTION" or "WRONG WIRING" or "WRONG CONNECTION" or "INCORRECT CONNECTION" or "ĐẤU SAI" or "SAI KẾT NỐI" => "WRONG_WIRING",
            "RESISTANCEOUTOFRANGE" or "RESISTANCE OUT OF SPECIFICATION" or "ĐIỆN TRỞ KHÔNG ĐẠT" => "RESISTANCE_OUT_OF_RANGE",
            "VOLTAGEOUTOFRANGE" or "VOLTAGE OUT OF SPECIFICATION" or "ĐIỆN ÁP KHÔNG ĐẠT" => "VOLTAGE_OUT_OF_RANGE",
            "CURRENTOUTOFRANGE" or "CURRENT OUT OF SPECIFICATION" or "DÒNG ĐIỆN KHÔNG ĐẠT" => "CURRENT_OUT_OF_RANGE",
            "SYSTEMDEVICEERROR" or "TEST SYSTEM ERROR" or "LỖI THIẾT BỊ / HỆ THỐNG" => "SYSTEM_DEVICE_ERROR",
            string normalized => normalized
        };
    }

    private static void AddResistanceOperatorLines(List<FaultDisplayLine> lines, FaultDetail fault)
    {
        BuildResistanceDisplay(
            fault,
            out string limits,
            out string measured,
            out string deviation,
            out string operatorAssessment,
            out _);
        Add(lines, "Vị trí đo", fault.WireName);
        Add(lines, "Giới hạn tiêu chuẩn", limits);
        Add(lines, "Giá trị đo", measured);
        Add(lines, "Sai lệch so với giới hạn", deviation);
        Add(lines, "Kết luận", operatorAssessment);
    }

    private static void BuildResistanceDisplay(
        FaultDetail fault,
        out string limits,
        out string measured,
        out string deviation,
        out string operatorAssessment,
        out string customerAssessment)
    {
        double? value = fault.MeasuredResistance;
        double? min = fault.ResistanceMin;
        double? max = fault.ResistanceMax;
        ResistanceScale scale = ChooseResistanceScale(value, min, max);

        bool isOpen = fault.Message.Contains("OPEN", StringComparison.OrdinalIgnoreCase);
        limits = min is double lower && max is double upper
            ? $"{FormatResistance(lower, scale)} – {FormatResistance(upper, scale)}"
            : string.Empty;
        measured = value is double actual ? FormatResistance(actual, scale) : isOpen ? "OPEN" : "NO VALUE";
        deviation = string.Empty;
        operatorAssessment = isOpen ? "OPEN" : "KHÔNG XÁC ĐỊNH";
        customerAssessment = isOpen ? "OPEN" : "VALUE UNAVAILABLE";

        if (value is not double measuredValue || min is not double minimum || max is not double maximum)
            return;

        double difference;
        if (measuredValue < minimum)
        {
            difference = measuredValue - minimum;
            operatorAssessment = "THẤP HƠN GIỚI HẠN";
            customerAssessment = "BELOW LOWER LIMIT";
        }
        else if (measuredValue > maximum)
        {
            difference = measuredValue - maximum;
            operatorAssessment = "CAO HƠN GIỚI HẠN";
            customerAssessment = "ABOVE UPPER LIMIT";
        }
        else
        {
            difference = 0;
            operatorAssessment = "TRONG GIỚI HẠN";
            customerAssessment = "WITHIN LIMITS";
        }

        deviation = FormatSignedResistance(difference, scale);
    }

    private static ResistanceScale ChooseResistanceScale(params double?[] values)
    {
        double maximum = values.Where(value => value.HasValue).Select(value => Math.Abs(value!.Value)).DefaultIfEmpty(0).Max();
        return maximum >= 1_000_000
            ? new ResistanceScale(1_000_000, "MΩ", "0.000")
            : maximum >= 1_000
                ? new ResistanceScale(1_000, "kΩ", "0.000")
                : new ResistanceScale(1, "Ω", "0.00");
    }

    private static string FormatResistance(double value, ResistanceScale scale) =>
        (value / scale.Divisor).ToString(scale.Format, CultureInfo.InvariantCulture) + " " + scale.Unit;

    private static string FormatSignedResistance(double value, ResistanceScale scale)
    {
        string formatted = (value / scale.Divisor)
            .ToString("+" + scale.Format + ";-" + scale.Format + ";0", CultureInfo.InvariantCulture);
        if (formatted.Contains('.', StringComparison.Ordinal))
            formatted = formatted.TrimEnd('0').TrimEnd('.');
        return formatted + " " + scale.Unit;
    }

    private static string StandardConnection(FaultDetail fault, bool vietnamese)
    {
        string source = FormatPosition(
            fault.ConnectorFrom,
            fault.PinFrom,
            fault.ExpectedSourceIo,
            vietnamese);
        string target = FormatPosition(
            fault.ConnectorTo,
            fault.PinTo,
            fault.ExpectedTargetIo,
            vietnamese);
        string connection = JoinConnection(source, target);
        if (!string.IsNullOrWhiteSpace(connection))
            return connection;

        return FormatRelatedIos(fault.RelatedIos, vietnamese);
    }

    private static string ActualConnection(FaultDetail fault, bool vietnamese)
    {
        string source = FormatPosition(
            fault.ActualConnectorFrom,
            fault.ActualPinFrom,
            fault.ActualSourceIo,
            vietnamese);
        string target = FormatPosition(
            fault.ActualConnectorTo,
            fault.ActualPinTo,
            fault.ActualTargetIo,
            vietnamese);
        string connection = JoinConnection(source, target);
        if (!string.IsNullOrWhiteSpace(connection))
            return connection;

        return FormatRelatedIos(fault.RelatedIos, vietnamese);
    }

    private static string FormatPosition(string? connector, string? pin, int? io, bool vietnamese)
    {
        string connectorText = connector?.Trim() ?? string.Empty;
        string pinText = pin?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(connectorText) && !string.IsNullOrWhiteSpace(pinText))
            return vietnamese
                ? $"{connectorText} - Chân {pinText}"
                : $"{connectorText} - Pin {pinText}";
        if (!string.IsNullOrWhiteSpace(connectorText))
            return connectorText;
        return io is int number && number > 0 ? $"IO {number}" : string.Empty;
    }

    private static string FormatRelatedIos(IEnumerable<int> ios, bool vietnamese) =>
        string.Join(" ↔ ", ios.Where(io => io > 0).Distinct().Select(io => $"IO {io}"));

    private static string JoinConnection(string source, string target)
    {
        if (string.IsNullOrWhiteSpace(source))
            return target;
        if (string.IsNullOrWhiteSpace(target))
            return source;
        return $"{source} ↔ {target}";
    }

    private static string FormatColor(string? rawColor, bool vietnamese)
    {
        string raw = rawColor?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        return raw.ToUpperInvariant() switch
        {
            "RED" or "RD" => vietnamese ? "ĐỎ" : "RED",
            "BLUE" or "BU" => vietnamese ? "XANH DƯƠNG" : "BLUE",
            "GREEN" or "GN" => vietnamese ? "XANH LÁ" : "GREEN",
            "YELLOW" or "YE" => vietnamese ? "VÀNG" : "YELLOW",
            "BLACK" or "BK" => vietnamese ? "ĐEN" : "BLACK",
            "WHITE" or "WH" => vietnamese ? "TRẮNG" : "WHITE",
            "BROWN" or "BN" => vietnamese ? "NÂU" : "BROWN",
            "ORANGE" or "OG" => vietnamese ? "CAM" : "ORANGE",
            "GRAY" or "GREY" or "GY" => vietnamese ? "XÁM" : "GRAY",
            "VIOLET" or "PURPLE" or "VT" => vietnamese ? "TÍM" : "VIOLET",
            _ => raw
        };
    }

    private static void Add(List<FaultDisplayLine> lines, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            lines.Add(new FaultDisplayLine(label, value.Trim()));
    }

    private static string Prefix(string label, string value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : $"{label}: {value}";

    private static string JoinParts(params string[] parts) =>
        string.Join("; ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));

    private readonly record struct ResistanceScale(double Divisor, string Unit, string Format);
}
