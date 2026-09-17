using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Security;
using System.Text;
using JBZUniveresalLunix.Models;

namespace JBZUniveresalLunix.Services;

public static class HistoryExportService
{
    private enum HistoryCellType
    {
        Text,
        Number,
        Date,
        Time
    }

    private sealed record HistoryColumn(
        string Header,
        double Width,
        HistoryCellType Type,
        Func<TestHistoryRecord, object?> GetValue,
        bool Wrap = false);

    // Mẫu ALL gốc có đúng 14 cột. CSV, XLSX và HistoryPage phải giữ cùng
    // thứ tự này; chi tiết các công đoạn chỉ nằm trong cột 검 사 기 록.
    private static readonly HistoryColumn[] Columns =
    [
        new("일 자", 12, HistoryCellType.Date, r => r.EffectiveTestStartedAt),
        new("시 간", 11, HistoryCellType.Time, r => r.EffectiveTestStartedAt),
        new("파 일", 28, HistoryCellType.Text, r => r.ExportModelFileName),
        new("품 명", 20, HistoryCellType.Text, r => r.PartName),
        new("품 번", 20, HistoryCellType.Text, r => r.PartNumber),
        new("차 종", 18, HistoryCellType.Text, r => r.VehicleType),
        new("Lot", 16, HistoryCellType.Text, r => r.ExportLotText),
        new("결 과", 10, HistoryCellType.Text, r => r.ExportResultText),
        new("순 번", 11, HistoryCellType.Number, r => r.ExportSequenceNo),
        new("검 사 기 록", 80, HistoryCellType.Text, r => r.ExportTestLogText, true),
        new("바코드", 34, HistoryCellType.Text, r => r.ExportBarcodeText),
        new("200 %", 10, HistoryCellType.Text, r => r.ExportPercentText),
        new("수입검사", 14, HistoryCellType.Text, r => r.ExportIncomingInspectionText),
        new("프로그램", 30, HistoryCellType.Text, r => r.HtdrvName)
    ];

    public static int ExportCsv(string path, IEnumerable<TestHistoryRecord> records)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding cp949 = Encoding.GetEncoding(
            949,
            EncoderFallback.ReplacementFallback,
            DecoderFallback.ReplacementFallback);
        using var writer = new StreamWriter(path, false, cp949) { NewLine = "\n" };
        writer.WriteLine(string.Join(',', Columns.Select(column => EscapeCsv(column.Header))));

        int count = 0;
        foreach (TestHistoryRecord record in records)
        {
            writer.WriteLine(string.Join(',', Columns.Select(column =>
                EscapeCsv(ToCsvValue(column, record)))));
            count++;
        }
        return count;
    }

    public static int ExportXlsx(string path, IEnumerable<TestHistoryRecord> records)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        if (File.Exists(path))
            File.Delete(path);

        using FileStream file = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using ZipArchive archive = new(file, ZipArchiveMode.Create, leaveOpen: false);

        WriteEntry(archive, "[Content_Types].xml", ContentTypesXml());
        WriteEntry(archive, "_rels/.rels", RootRelsXml());
        WriteEntry(archive, "xl/workbook.xml", WorkbookXml());
        WriteEntry(archive, "xl/_rels/workbook.xml.rels", WorkbookRelsXml());
        WriteEntry(archive, "xl/styles.xml", StylesXml());
        return WriteSheetEntry(archive, records);
    }

    private static string ToCsvValue(HistoryColumn column, TestHistoryRecord record)
    {
        object? value = column.GetValue(record);
        if (value is null)
            return string.Empty;

        return column.Type switch
        {
            HistoryCellType.Date => ((DateTime)value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            HistoryCellType.Time => ((DateTime)value).ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            HistoryCellType.Number => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        };
    }

    private static string EscapeCsv(string? value)
    {
        string text = value ?? string.Empty;
        if (text.Contains('"') || text.Contains(',') || text.Contains('\n') || text.Contains('\r'))
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        return text;
    }

    private static void WriteEntry(ZipArchive archive, string name, string text)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using Stream stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(text);
    }

    private static int WriteSheetEntry(
        ZipArchive archive,
        IEnumerable<TestHistoryRecord> records)
    {
        ZipArchiveEntry entry = archive.CreateEntry(
            "xl/worksheets/sheet1.xml",
            CompressionLevel.Optimal);
        using Stream stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        var sb = new StringBuilder(32_768);
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        sb.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
        sb.Append("<sheetFormatPr defaultRowHeight=\"20\"/>");
        sb.Append("<cols>");
        for (int i = 0; i < Columns.Length; i++)
            sb.Append($"<col min=\"{i + 1}\" max=\"{i + 1}\" width=\"{Columns[i].Width.ToString(CultureInfo.InvariantCulture)}\" customWidth=\"1\"/>");
        sb.Append("</cols>");
        sb.Append("<sheetData>");
        AppendHeaderRow(sb);
        writer.Write(sb);
        sb.Clear();

        int count = 0;
        foreach (TestHistoryRecord record in records)
        {
            AppendDataRow(sb, count + 2, record);
            writer.Write(sb);
            sb.Clear();
            count++;
        }

        sb.Append("</sheetData>");
        if (count >= 1)
        {
            string lastColumn = ColumnName(Columns.Length);
            sb.Append($"<autoFilter ref=\"A1:{lastColumn}{count + 1}\"/>");
        }
        sb.Append("</worksheet>");
        writer.Write(sb);
        return count;
    }

    private static void AppendHeaderRow(StringBuilder sb)
    {
        sb.Append("<row r=\"1\" ht=\"27\" customHeight=\"1\">");
        for (int i = 0; i < Columns.Length; i++)
        {
            string cell = $"{ColumnName(i + 1)}1";
            sb.Append($"<c r=\"{cell}\" t=\"inlineStr\" s=\"1\"><is><t xml:space=\"preserve\">{Xml(Columns[i].Header)}</t></is></c>");
        }
        sb.Append("</row>");
    }

    private static void AppendDataRow(StringBuilder sb, int rowNumber, TestHistoryRecord record)
    {
        sb.Append($"<row r=\"{rowNumber}\" ht=\"30\" customHeight=\"1\">");
        for (int i = 0; i < Columns.Length; i++)
        {
            HistoryColumn column = Columns[i];
            string cell = $"{ColumnName(i + 1)}{rowNumber}";
            object? value = column.GetValue(record);

            if (value is null)
            {
                sb.Append($"<c r=\"{cell}\" s=\"0\"/>");
                continue;
            }

            if (column.Type == HistoryCellType.Date)
            {
                double serial = ((DateTime)value).Date.ToOADate();
                sb.Append($"<c r=\"{cell}\" s=\"2\"><v>{serial.ToString("0.###############", CultureInfo.InvariantCulture)}</v></c>");
            }
            else if (column.Type == HistoryCellType.Time)
            {
                double serial = ((DateTime)value).TimeOfDay.TotalDays;
                sb.Append($"<c r=\"{cell}\" s=\"4\"><v>{serial.ToString("0.###############", CultureInfo.InvariantCulture)}</v></c>");
            }
            else if (column.Type == HistoryCellType.Number)
            {
                string number = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0";
                sb.Append($"<c r=\"{cell}\"><v>{Xml(number)}</v></c>");
            }
            else
            {
                string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                string style = column.Wrap ? " s=\"3\"" : string.Empty;
                sb.Append($"<c r=\"{cell}\" t=\"inlineStr\"{style}><is><t xml:space=\"preserve\">{Xml(text)}</t></is></c>");
            }
        }
        sb.Append("</row>");
    }

    private static string ColumnName(int index)
    {
        var chars = new Stack<char>();
        while (index > 0)
        {
            index--;
            chars.Push((char)('A' + (index % 26)));
            index /= 26;
        }
        return new string(chars.ToArray());
    }

    private static string Xml(string? value) =>
        SecurityElement.Escape(value ?? string.Empty) ?? string.Empty;

    private static string ContentTypesXml() => """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
          <Default Extension="xml" ContentType="application/xml"/>
          <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
          <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
          <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
        </Types>
        """;

    private static string RootRelsXml() => """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
        </Relationships>
        """;

    private static string WorkbookXml() => """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
          <sheets><sheet name="검사" sheetId="1" r:id="rId1"/></sheets>
        </workbook>
        """;

    private static string WorkbookRelsXml() => """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
          <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
        </Relationships>
        """;

    private static string StylesXml() => """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
          <numFmts count="2">
            <numFmt numFmtId="164" formatCode="yyyy-mm-dd"/>
            <numFmt numFmtId="165" formatCode="hh:mm:ss"/>
          </numFmts>
          <fonts count="2">
            <font><sz val="10"/><name val="Calibri"/></font>
            <font><b/><color rgb="FFFFFFFF"/><sz val="10"/><name val="Calibri"/></font>
          </fonts>
          <fills count="3">
            <fill><patternFill patternType="none"/></fill>
            <fill><patternFill patternType="gray125"/></fill>
            <fill><patternFill patternType="solid"><fgColor rgb="FF1F4E78"/><bgColor indexed="64"/></patternFill></fill>
          </fills>
          <borders count="2">
            <border><left/><right/><top/><bottom/><diagonal/></border>
            <border><left style="thin"><color rgb="FFD9E2F3"/></left><right style="thin"><color rgb="FFD9E2F3"/></right><top style="thin"><color rgb="FFD9E2F3"/></top><bottom style="thin"><color rgb="FFD9E2F3"/></bottom><diagonal/></border>
          </borders>
          <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
          <cellXfs count="5">
            <xf numFmtId="0" fontId="0" fillId="0" borderId="1" xfId="0" applyBorder="1" applyAlignment="1"><alignment vertical="center"/></xf>
            <xf numFmtId="0" fontId="1" fillId="2" borderId="1" xfId="0" applyFill="1" applyFont="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center"/></xf>
            <xf numFmtId="164" fontId="0" fillId="0" borderId="1" xfId="0" applyNumberFormat="1" applyBorder="1" applyAlignment="1"><alignment vertical="center"/></xf>
            <xf numFmtId="0" fontId="0" fillId="0" borderId="1" xfId="0" applyBorder="1" applyAlignment="1"><alignment vertical="center" wrapText="1"/></xf>
            <xf numFmtId="165" fontId="0" fillId="0" borderId="1" xfId="0" applyNumberFormat="1" applyBorder="1" applyAlignment="1"><alignment vertical="center"/></xf>
          </cellXfs>
          <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
        </styleSheet>
        """;
}
