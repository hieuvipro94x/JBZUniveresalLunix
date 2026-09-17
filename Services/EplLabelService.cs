using System.IO;
using System.Globalization;
using System.Text;
using JBZUniveresalLunix.Models;

namespace JBZUniveresalLunix.Services;

public static class EplLabelService
{
    /// <summary>
    /// Label PASS theo đúng thứ tự dữ liệu tìm thấy trong mẫu ALL6.xls:
    /// PartNumber / Eco / PartName / yyMMdd+LOT+WH / PartNumber+yyMMdd+LOT.
    /// Format mặc định KS91 và kích thước 90 x 15 mm.
    /// </summary>
    public static string BuildPassLabel(LabelPrintData data, LabelSettings settings)
    {
        LabelIdentity identity = BuildIdentity(data);

        return Build(
            data.PartNumber,
            data.Eco,
            data.PartName,
            identity.SerialText,
            identity.BarcodeValue,
            settings.FormatName,
            settings.WidthMm,
            settings.HeightMm);
    }

    public static string BuildPassLabel(LabelPrintRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Payload;
    }

    public static LabelIdentity BuildIdentity(
        LabelPrintData data,
        bool includeAlcLotSuffix = true)
    {
        string date = data.TestedAt.ToString("yyMMdd");
        string displayLot = FormatLotNo(data.LotNo, data.Alc, includeAlcLotSuffix: false);
        string barcodeLot = FormatLotNo(data.LotNo, data.Alc, includeAlcLotSuffix);
        return new LabelIdentity(
            $"{date}{displayLot}WH",
            $"{data.PartNumber}{date}{barcodeLot}");
    }

    internal static string FormatLotNo(long lotNo, string? alc, bool includeAlcLotSuffix)
    {
        // TEM_TO/TEM_BE dùng LOT tối thiểu bốn chữ số. D4 không cắt các LOT
        // lớn hơn 9999 và hậu tố ALC vẫn được ghép riêng sau bốn số LOT.
        string lot = lotNo.ToString("D4", CultureInfo.InvariantCulture);
        if (!includeAlcLotSuffix || string.IsNullOrWhiteSpace(alc))
            return lot;

        string[] parts = alc.Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length == 3 &&
            parts[0].Length > 0 &&
            parts[1].Length > 0 &&
            (parts[2] == "1" || parts[2] == "2"))
        {
            return lot + parts[2];
        }

        return lot;
    }

    public static string Build90x15(
        string partNumber,
        string vehicle,
        string productName,
        string serialText,
        string barcodeText,
        string formatName = "KS91") =>
        Build(partNumber, vehicle, productName, serialText, barcodeText, formatName, 90, 15);

    public static string Build(
        string partNumber,
        string eco,
        string partName,
        string serialText,
        string barcodeText,
        string formatName,
        int widthMm,
        int heightMm)
    {
        static string Safe(string? value) =>
            (value ?? string.Empty)
                .Replace("\"", "'")
                .Replace("\r", " ")
                .Replace("\n", " ");

        widthMm = Math.Clamp(widthMm, 20, 200);
        heightMm = Math.Clamp(heightMm, 10, 150);

        // Máy EPL phổ biến dùng 203 dpi ~= 8 dot/mm.
        int widthDots = widthMm * 8;
        int heightDots = heightMm * 8;

        return $"""
N
q{widthDots}
Q{heightDots},24
R0,0
ON
D7
S3
FK"*"
FK"{Safe(formatName)}"
FS"{Safe(formatName)}"
V00,15,N,"DATA1"
V01,15,N,"DATA2"
V02,15,N,"DATA3"
V03,15,N,"DATA4"
V04,25,N,"DATA5"
b123,14,D,h4,V04
A610,26,0,1,1,1,N,V00
A610,46,0,1,1,1,N,V01
A610,62,0,1,1,1,N,V02
A610,80,0,1,1,1,N,V03
ZT
FE
FR"{Safe(formatName)}"
?
{Safe(partNumber)}
{Safe(eco)}
{Safe(partName)}
{Safe(serialText)}
{Safe(barcodeText)}
P1
""";
    }

    public static void SavePreview(string path, string epl)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);

        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(fullPath, epl, Encoding.ASCII);
    }
}
