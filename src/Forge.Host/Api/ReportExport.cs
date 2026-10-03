using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using Forge.Planning.Reporting;
using NodaTime;

namespace Forge.Host.Api;

/// <summary>
/// Report exports. Times are written in the site's time zone. Every row keeps its source records in
/// the last column, so an exported figure can still be traced. XLSX is written directly as Office
/// Open XML (a data sheet with a filter, and a summary sheet), with no third-party library.
/// </summary>
public static class ReportExport
{
    private const string SourceTitle = "Source records";

    public static byte[] Csv(ReportResult report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var zone = Zone(report);
        var sb = new StringBuilder();

        // The report's context first (spec §65: exports keep the period, filters and generation time), then a blank line and the table.
        foreach (var (label, value) in new[]
        {
            ("Report", report.Title),
            ("Period", $"{report.From:yyyy-MM-dd} to {report.To:yyyy-MM-dd} ({report.TimeZone})"),
            ("Filters", Filters(report.Parameters)),
            ("Generated", $"{Text(report.GeneratedAt, zone)} by {report.GeneratedBy}"),
        })
        {
            sb.Append(Quote(label)).Append(',').Append(Quote(value)).Append("\r\n");
        }

        sb.Append("\r\n");
        sb.AppendJoin(',', report.Columns.Select(c => Quote(c.Title)).Append(Quote(SourceTitle))).Append("\r\n");
        foreach (var row in report.Rows)
        {
            sb.AppendJoin(',', row.Cells.Select(c => Quote(Text(c, zone))).Append(Quote(string.Join(' ', row.Trace)))).Append("\r\n");
        }

        // A BOM so spreadsheet programs read the file as UTF-8.
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    public static byte[] Xlsx(ReportResult report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var zone = Zone(report);
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Entry(zip, "[Content_Types].xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                <Default Extension="xml" ContentType="application/xml"/>
                <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                <Override PartName="/xl/worksheets/sheet2.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
                </Types>
                """);
            Entry(zip, "_rels/.rels",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """);
            Entry(zip, "xl/workbook.xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                <sheets><sheet name="Data" sheetId="1" r:id="rId1"/><sheet name="Summary" sheetId="2" r:id="rId2"/></sheets>
                </workbook>
                """);
            Entry(zip, "xl/_rels/workbook.xml.rels",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet2.xml"/>
                <Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
                </Relationships>
                """);

            // Styles: 0 default, 1 bold, 2 date, 3 date-time, 4 number.
            Entry(zip, "xl/styles.xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                <numFmts count="2"><numFmt numFmtId="164" formatCode="yyyy-mm-dd"/><numFmt numFmtId="165" formatCode="yyyy-mm-dd hh:mm"/></numFmts>
                <fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts>
                <fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills>
                <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
                <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
                <cellXfs count="5">
                <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
                <xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/>
                <xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
                <xf numFmtId="165" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
                <xf numFmtId="4" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
                </cellXfs>
                </styleSheet>
                """);

            var data = new StringBuilder();
            data.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");
            data.Append("""<sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews><sheetData>""");
            data.Append(Row(1, report.Columns.Select(c => Str(c.Title, 1)).Append(Str(SourceTitle, 1))));
            var r = 2;
            foreach (var row in report.Rows)
            {
                data.Append(Row(r++, row.Cells.Select(c => Cell(c, zone)).Append(Str(string.Join(' ', row.Trace), 0))));
            }

            data.Append(CultureInfo.InvariantCulture, $"""</sheetData><autoFilter ref="A1:{ColumnName(report.Columns.Count + 1)}{Math.Max(1, report.Rows.Count + 1)}"/></worksheet>""");
            Entry(zip, "xl/worksheets/sheet1.xml", data.ToString());

            var summary = new StringBuilder();
            summary.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");
            var lines = new List<string[]>
            {
                new[] { report.Title },
                new[] { "Period", $"{report.From:yyyy-MM-dd} to {report.To:yyyy-MM-dd} ({report.TimeZone})" },
                new[] { "Generated", $"{Text(report.GeneratedAt, zone)} by {report.GeneratedBy}" },
                new[] { "Filters", Filters(report.Parameters) },
                new[] { "Basis", report.Basis },
                new[] { "Sources", string.Join(", ", report.Sources) },
                Array.Empty<string>(),
            };
            lines.AddRange(report.Summary.Select(s => new[] { s }));
            var s = 1;
            foreach (var line in lines)
            {
                var title = s == 1;
                summary.Append(Row(s++, line.Select((v, i) => Str(v, title || (i == 0 && line.Length > 1) ? 1 : 0)).ToList()));
            }

            var sums = report.Columns.Where(c => c.Kind is ReportColumnKind.Number or ReportColumnKind.Hours).ToList();
            if (report.Groups.Count > 0 || sums.Count > 0)
            {
                s++;
                var head = new List<string> { report.GroupBy is { } g ? report.Columns.First(c => c.Key == g).Title : string.Empty, "Rows" };
                head.AddRange(sums.Select(c => c.Title));
                if (report.Ratio is not null)
                {
                    head.Add(report.Ratio.Title + " %");
                }

                summary.Append(Row(s++, head.Select(h => Str(h, 1))));
                foreach (var group in report.Groups.Append(report.Total))
                {
                    var cells = new List<string> { Str(group.Key, group == report.Total ? 1 : 0), Num(group.Rows) };
                    cells.AddRange(sums.Select(c => Num(group.Sums.GetValueOrDefault(c.Key))));
                    if (report.Ratio is not null)
                    {
                        cells.Add(group.Ratio is { } pct ? Num(pct) : Str(string.Empty, 0));
                    }

                    summary.Append(Row(s++, cells));
                }
            }

            summary.Append("</sheetData></worksheet>");
            Entry(zip, "xl/worksheets/sheet2.xml", summary.ToString());
        }

        return stream.ToArray();
    }

    internal static string Filters(ReportParameters p)
    {
        var parts = new List<string>();
        void Add(string name, object? value)
        {
            if (value is not null && value.ToString() is { Length: > 0 } text)
            {
                parts.Add($"{name}: {text}");
            }
        }

        Add("last days", p.LastDays);
        Add("program", p.ProgramId);
        Add("work center", p.WorkCenter);
        Add("machine", p.Machine);
        Add("part", p.PartNumber);
        Add("shift", p.Shift);
        Add("status", p.Status);
        Add("contains", p.Search);
        Add("grouped by", p.GroupBy);
        return parts.Count == 0 ? "none" : string.Join("; ", parts);
    }

    private static DateTimeZone Zone(ReportResult report) =>
        DateTimeZoneProviders.Tzdb.GetZoneOrNull(report.TimeZone) ?? DateTimeZone.Utc;

    private static LocalDateTime Local(DateTimeOffset at, DateTimeZone zone) => Instant.FromDateTimeOffset(at).InZone(zone).LocalDateTime;

    private static string Text(object? value, DateTimeZone zone) => value switch
    {
        null => string.Empty,
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeOffset t => Local(t, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>CSV field: text that a spreadsheet would run as a formula is prefixed with an apostrophe.</summary>
    private static string Quote(string value)
    {
        if (value.Length > 0 && "=+-@".Contains(value[0], StringComparison.Ordinal) && !decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }

    private static string Cell(object? value, DateTimeZone zone) => value switch
    {
        null => "<c/>",
        decimal d => Num(d),
        int i => Num(i),
        DateOnly d => $"""<c s="2"><v>{(d.DayNumber - new DateOnly(1899, 12, 30).DayNumber).ToString(CultureInfo.InvariantCulture)}</v></c>""",
        DateTimeOffset t => $"""<c s="3"><v>{Serial(Local(t, zone)).ToString("0.########", CultureInfo.InvariantCulture)}</v></c>""",
        _ => Str(Text(value, zone), 0),
    };

    private static double Serial(LocalDateTime local) =>
        (local.ToDateTimeUnspecified() - new DateTime(1899, 12, 30, 0, 0, 0, DateTimeKind.Unspecified)).TotalDays;

    private static string Num(decimal d) => $"""<c><v>{d.ToString(CultureInfo.InvariantCulture)}</v></c>""";

    private static string Str(string value, int style)
    {
        var attribute = style == 0 ? string.Empty : " s=\"" + style.ToString(CultureInfo.InvariantCulture) + "\"";
        return $"""<c t="inlineStr"{attribute}><is><t xml:space="preserve">{SecurityElement.Escape(Clean(value))}</t></is></c>""";
    }

    /// <summary>Characters XML 1.0 cannot carry are dropped.</summary>
    private static string Clean(string value) => new(value.Where(ch => ch is '\t' or '\n' or '\r' || ch >= ' ').ToArray());

    private static string Row(int number, IEnumerable<string> cells) =>
        $"""<row r="{number.ToString(CultureInfo.InvariantCulture)}">{string.Concat(cells)}</row>""";

    private static string ColumnName(int number)
    {
        var name = string.Empty;
        while (number > 0)
        {
            var m = (number - 1) % 26;
            name = (char)('A' + m) + name;
            number = (number - 1) / 26;
        }

        return name;
    }

    private static void Entry(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
