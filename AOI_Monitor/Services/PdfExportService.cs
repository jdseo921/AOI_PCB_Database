using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace AOI_Monitor.Services;

public static partial class PdfExportService
{
    private const int PageWidth = 612;
    private const int PageHeight = 792;
    private const int LeftMargin = 50;
    private const int TopY = 750;
    private const int LineHeight = 14;
    private const int MaxCharsPerLine = 94;
    private const int MaxLinesPerPage = 50;

    public static string ExportHtmlFileToPdf(string htmlPath, string pdfPath, string? title = null)
    {
        if (string.IsNullOrWhiteSpace(htmlPath))
            throw new ArgumentException("HTML path is required.", nameof(htmlPath));
        if (!File.Exists(htmlPath))
            throw new FileNotFoundException("HTML report was not found.", htmlPath);

        return ExportHtmlToPdf(File.ReadAllText(htmlPath, Encoding.UTF8), pdfPath, title ?? Path.GetFileNameWithoutExtension(htmlPath));
    }

    public static string ExportHtmlToPdf(string html, string pdfPath, string? title = null)
    {
        if (string.IsNullOrWhiteSpace(pdfPath))
            throw new ArgumentException("PDF path is required.", nameof(pdfPath));

        var folder = Path.GetDirectoryName(pdfPath);
        if (!string.IsNullOrWhiteSpace(folder))
            Directory.CreateDirectory(folder);

        var lines = BuildPdfLines(html, title).ToArray();
        var pages = lines.Chunk(MaxLinesPerPage).Select(chunk => chunk.ToArray()).ToArray();
        if (pages.Length == 0)
            pages = new[] { new[] { title ?? "AOI Monitor Report" } };

        var hasUnicode = pages.Any(page => page.Any(line => line.Any(ch => ch > '~')));
        File.WriteAllBytes(pdfPath, BuildPdfBytes(pages, hasUnicode));
        return pdfPath;
    }

    private static IEnumerable<string> BuildPdfLines(string html, string? title)
    {
        if (!string.IsNullOrWhiteSpace(title))
        {
            foreach (var line in Wrap(NormalizeText(title), MaxCharsPerLine))
                yield return line;
            yield return string.Empty;
        }

        var text = HtmlToPlainText(html);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = NormalizeText(rawLine);
            if (line.Length == 0)
            {
                yield return string.Empty;
                continue;
            }

            foreach (var wrapped in Wrap(line, MaxCharsPerLine))
                yield return wrapped;
        }
    }

    private static byte[] BuildPdfBytes(IReadOnlyList<string[]> pages, bool hasUnicode)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            string.Empty, // object 2 (Pages tree) filled once page object numbers are known
        };

        // Object 3 is always the page font resource (/F1). When the document contains non-ASCII text
        // (e.g. Korean), use a Type0 CID-keyed font (Adobe-Korea1, UniKS-UCS2-H) so the text is
        // preserved and rendered by a Korean-capable viewer instead of being dropped to blanks.
        int firstPageObject;
        if (hasUnicode)
        {
            objects.Add("<< /Type /Font /Subtype /Type0 /BaseFont /HYSMyeongJo-Medium /Encoding /UniKS-UCS2-H /DescendantFonts [4 0 R] >>");
            objects.Add("<< /Type /Font /Subtype /CIDFontType0 /BaseFont /HYSMyeongJo-Medium /CIDSystemInfo << /Registry (Adobe) /Ordering (Korea1) /Supplement 1 >> /DW 1000 /FontDescriptor << /Type /FontDescriptor /FontName /HYSMyeongJo-Medium /Flags 6 /FontBBox [-200 -200 1100 900] /ItalicAngle 0 /Ascent 900 /Descent -200 /CapHeight 700 /StemV 80 >> >>");
            firstPageObject = 5;
        }
        else
        {
            objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
            firstPageObject = 4;
        }

        var pageObjectNumbers = Enumerable.Range(0, pages.Count).Select(index => firstPageObject + index * 2).ToArray();
        objects[1] = $"<< /Type /Pages /Kids [{string.Join(" ", pageObjectNumbers.Select(number => $"{number} 0 R"))}] /Count {pages.Count} >>";

        for (var index = 0; index < pages.Count; index++)
        {
            var pageObject = firstPageObject + index * 2;
            var contentObject = pageObject + 1;
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageWidth} {PageHeight}] /Resources << /Font << /F1 3 0 R >> >> /Contents {contentObject} 0 R >>");
            var stream = BuildPageStream(pages[index], hasUnicode);
            var length = Encoding.ASCII.GetByteCount(stream);
            objects.Add($"<< /Length {length} >>\nstream\n{stream}\nendstream");
        }

        var output = new StringBuilder();
        var offsets = new List<int> { 0 };
        output.Append("%PDF-1.4\n%\u00e2\u00e3\u00cf\u00d3\n");
        foreach (var (obj, index) in objects.Select((value, i) => (value, i + 1)))
        {
            offsets.Add(Encoding.ASCII.GetByteCount(output.ToString()));
            output.Append(CultureInvariant($"{index} 0 obj\n"));
            output.Append(obj);
            output.Append("\nendobj\n");
        }

        var xrefOffset = Encoding.ASCII.GetByteCount(output.ToString());
        output.Append($"xref\n0 {objects.Count + 1}\n");
        output.Append("0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1))
            output.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        output.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");

        return Encoding.ASCII.GetBytes(output.ToString());
    }

    private static string BuildPageStream(IEnumerable<string> lines, bool hasUnicode)
    {
        var sb = new StringBuilder();
        sb.AppendLine("BT");
        sb.AppendLine("/F1 10 Tf");
        sb.AppendLine($"{LeftMargin} {TopY} Td");
        sb.AppendLine($"{LineHeight} TL");
        foreach (var line in lines)
        {
            if (hasUnicode)
                sb.AppendLine($"<{ToUcs2HexBigEndian(line)}> Tj T*");
            else
                sb.AppendLine($"({EscapePdfText(line)}) Tj T*");
        }

        sb.Append("ET");
        return sb.ToString();
    }

    // Encodes a line as big-endian UCS-2 hex for the Type0 /UniKS-UCS2-H font path (2 bytes per code unit).
    private static string ToUcs2HexBigEndian(string text)
    {
        var sb = new StringBuilder(text.Length * 4);
        foreach (var ch in text)
            sb.Append(((int)ch).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static string HtmlToPlainText(string html)
    {
        var text = html ?? string.Empty;
        text = NewLineTagRegex().Replace(text, "\n");
        text = TableCellRegex().Replace(text, " | ");
        text = StripTagsRegex().Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        text = RepeatedSpacesRegex().Replace(text, " ");
        text = RepeatedNewLinesRegex().Replace(text, "\n");
        return text.Trim();
    }

    private static IEnumerable<string> Wrap(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            yield return text;
            yield break;
        }

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var line = new StringBuilder();
        foreach (var word in words)
        {
            if (line.Length > 0 && line.Length + word.Length + 1 > maxChars)
            {
                yield return line.ToString();
                line.Clear();
            }

            if (word.Length > maxChars)
            {
                if (line.Length > 0)
                {
                    yield return line.ToString();
                    line.Clear();
                }

                for (var i = 0; i < word.Length; i += maxChars)
                    yield return word.Substring(i, Math.Min(maxChars, word.Length - i));
                continue;
            }

            if (line.Length > 0)
                line.Append(' ');
            line.Append(word);
        }

        if (line.Length > 0)
            yield return line.ToString();
    }

    // Keeps ASCII printable characters and all printable non-ASCII (Latin-1 supplement and CJK such as
    // Korean); only control characters (C0/C1 and DEL) are replaced with spaces. Non-ASCII text is
    // rendered through the Type0 Unicode font path instead of being dropped.
    private static string NormalizeText(string text)
        => new(text.Select(ch => ch < ' ' || (ch >= '\u007F' && ch <= '\u009F') ? ' ' : ch).ToArray());

    private static string EscapePdfText(string text)
        => text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);

    private static string CultureInvariant(FormattableString value)
        => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    [GeneratedRegex(@"<(br|/p|/tr|/h[1-6]|/li|/div|/section)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex NewLineTagRegex();

    [GeneratedRegex(@"</t[dh]>", RegexOptions.IgnoreCase)]
    private static partial Regex TableCellRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex StripTagsRegex();

    [GeneratedRegex(@"[ \t\f\v]+")]
    private static partial Regex RepeatedSpacesRegex();

    [GeneratedRegex(@"\n\s*\n\s*\n+")]
    private static partial Regex RepeatedNewLinesRegex();
}
