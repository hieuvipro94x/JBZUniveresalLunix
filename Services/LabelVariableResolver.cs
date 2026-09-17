using System.Globalization;
using System.IO;
using JBZUniveresalLunix.Models;

namespace JBZUniveresalLunix.Services;

public static class LabelVariableResolver
{
    private const string SmallLabelCustomerPrefix = "SQDZ";

    public static IReadOnlyDictionary<string, string> Resolve(
        ProductModel model,
        LabelPrintData data,
        LabelSettings settings)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(settings);

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Lowest priority: machine/profile settings and defaults.
            ["PROFILE"] = settings.FormatName ?? string.Empty,
            ["FORMAT_NAME"] = settings.FormatName ?? string.Empty,

            // Product fields parsed from the current THT.
            ["PART_NUMBER"] = model.PartNumber,
            ["PRODUCT_NAME"] = model.ProductName,
            ["PART_NAME"] = model.ProductName,
            ["VEHICLE_TYPE"] = model.VehicleType,
            ["CUSTOMER_CODE"] = model.CustomerCode,
            ["ECO"] = model.Eco,
            ["NCO"] = model.Nco,
            ["ALC"] = model.Alc,
            ["MODEL_NAME"] = model.ModelName
        };

        // THT variables override ordinary ProductModel/settings values.
        foreach ((string name, string value) in model.LabelVariables)
        {
            if (!string.IsNullOrWhiteSpace(name))
                values[name.Trim()] = value ?? string.Empty;
        }

        string templateType = LabelProfileResolver.NormalizeTemplateType(settings.TemplateType);
        bool isSmallLabel = templateType == LabelSettings.SmallTemplate;
        bool isSmallQrLabel = templateType == LabelSettings.SmallQrTemplate;
        bool usesFourDigitLot = isSmallLabel || isSmallQrLabel;
        long labelLotNo = isSmallQrLabel
            ? ResolveSmallQrSequence(data.LotNo)
            : data.LotNo;
        LabelIdentity identity = EplLabelService.BuildIdentity(
            data,
            includeAlcLotSuffix: !usesFourDigitLot);
        string lot = usesFourDigitLot
            ? labelLotNo.ToString("D4", CultureInfo.InvariantCulture)
            : EplLabelService.FormatLotNo(data.LotNo, data.Alc, includeAlcLotSuffix: false);
        string barcodeLot = usesFourDigitLot
            ? lot
            : EplLabelService.FormatLotNo(data.LotNo, data.Alc, includeAlcLotSuffix: true);
        string barcode = string.IsNullOrWhiteSpace(data.Barcode)
            ? identity.BarcodeValue
            : data.Barcode;
        string barcodePrint = string.IsNullOrWhiteSpace(data.BarcodePrint)
            ? barcode
            : data.BarcodePrint;

        if (isSmallLabel)
        {
            string yearCode = ResolveSmallLabelYearCode(data.TestedAt.Year);
            string monthCode = ResolveSmallLabelMonthCode(data.TestedAt.Month);
            string dayCode = ResolveSmallLabelDayCode(data.TestedAt.Day);
            string smallLabelBarcode =
                $"{data.PartNumber},{SmallLabelCustomerPrefix}{yearCode}{monthCode}{dayCode}{lot}";

            values["YEAR_CODE"] = yearCode;
            values["MONTH_CODE"] = monthCode;
            values["DAY_CODE"] = dayCode;
            values["SMALL_LABEL_BARCODE"] = smallLabelBarcode;
            values["PART_NUMBER"] = data.PartNumber;
            barcode = smallLabelBarcode;
            barcodePrint = smallLabelBarcode;
        }
        else if (isSmallQrLabel)
        {
            string qrBarcode =
                $"{data.PartNumber},{data.TestedAt.ToString("yyMMdd", CultureInfo.InvariantCulture)}{lot}";
            values["SMALL_QR_BARCODE"] = qrBarcode;
            values["PART_NUMBER"] = data.PartNumber;
            barcode = qrBarcode;
            barcodePrint = qrBarcode;
        }

        // Highest priority: immutable data captured for the completed cycle.
        values["LOT"] = lot;
        values["LOT_NO"] = lot;
        values["LOT_NO_BARCODE"] = barcodeLot;
        values["LOT_NO_3"] = labelLotNo.ToString("D3", CultureInfo.InvariantCulture);
        values["SEQUENCE"] = lot;
        values["MODEL_FILE_NAME"] = Path.GetFileName(model.SourcePath ?? string.Empty);
        values["LABEL_MARK"] = "Q";
        values["MONTH_NUMBER"] = data.TestedAt.Month.ToString(CultureInfo.InvariantCulture);
        values["DAY_NUMBER"] = data.TestedAt.Day.ToString(CultureInfo.InvariantCulture);
        values["TEST_DATE"] = data.TestedAt.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        values["TEST_TIME"] = data.TestedAt.ToString("HHmmss", CultureInfo.InvariantCulture);
        values["DATE_YYMMDD"] = data.TestedAt.ToString("yyMMdd", CultureInfo.InvariantCulture);
        values["DATE_YYYYMMDD"] = data.TestedAt.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        values["TIME_HHMMSS"] = data.TestedAt.ToString("HHmmss", CultureInfo.InvariantCulture);
        values["PRINT_DATE"] = data.TestedAt.ToString("yyMMdd", CultureInfo.InvariantCulture);
        values["CYCLE_ID"] = data.CycleId;
        values["SERIAL_TEXT"] = identity.SerialText;
        values["BARCODE_VALUE"] = usesFourDigitLot ? barcode : identity.BarcodeValue;
        values["BARCODE"] = barcode;
        values["BARCODE_PRINT"] = barcodePrint;

        return values;
    }

    private static long ResolveSmallQrSequence(long lotNo)
    {
        // Máy dùng dải LOT nội bộ 2001..2999 để bảo toàn lịch sử sản lượng,
        // nhưng tem QR gốc in số thứ tự ngày 0001..0999 như ảnh thực tế.
        return lotNo is >= 2000 and < 3000
            ? lotNo - 2000
            : lotNo;
    }

    private static string ResolveSmallLabelYearCode(int year)
    {
        if (year is >= 2010 and <= 2035)
            return ((char)('A' + year - 2010)).ToString(CultureInfo.InvariantCulture);

        throw UndefinedDateCode($"Year={year}");
    }

    private static string ResolveSmallLabelMonthCode(int month)
    {
        if (month is >= 1 and <= 9)
            return month.ToString(CultureInfo.InvariantCulture);

        if (month is >= 10 and <= 12)
            return ((char)('A' + month - 10)).ToString(CultureInfo.InvariantCulture);

        throw UndefinedDateCode($"Month={month}");
    }

    private static string ResolveSmallLabelDayCode(int day)
    {
        if (day is >= 1 and <= 9)
            return day.ToString(CultureInfo.InvariantCulture);

        if (day is >= 10 and <= 31)
            return ((char)('A' + day - 10)).ToString(CultureInfo.InvariantCulture);

        throw UndefinedDateCode($"Day={day}");
    }

    private static InvalidDataException UndefinedDateCode(string detail)
    {
        string message = $"LABEL_DATE_CODE_UNDEFINED {detail}";
        AsyncFileLogService.Current.Error($"[LABEL][ERROR] {message}");
        return new InvalidDataException(message);
    }
}
