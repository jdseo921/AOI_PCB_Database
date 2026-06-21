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

        File.WriteAllBytes(pdfPath, BuildPdfBytes(pages));
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

    private static byte[] BuildPdfBytes(IReadOnlyList<string[]> pages)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
        };

        var pageObjectNumbers = Enumerable.Range(0, pages.Count).Select(index => 3 + index * 2).ToArray();
        objects.Add($"<< /Type /Pages /Kids [{string.Join(" ", pageObjectNumbers.Select(number => $"{number} 0 R"))}] /Count {pages.Count} >>");

        for (var index = 0; index < pages.Count; index++)
        {
            var pageObject = 3 + index * 2;
            var contentObject = pageObject + 1;
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageWidth} {PageHeight}] /Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> /Contents {contentObject} 0 R >>");
            var stream = BuildPageStream(pages[index]);
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

    private static string BuildPageStream(IEnumerable<string> lines)
    {
        var sb = new StringBuilder();
        sb.AppendLine("BT");
        sb.AppendLine("/F1 10 Tf");
        sb.AppendLine($"{LeftMargin} {TopY} Td");
        sb.AppendLine($"{LineHeight} TL");
        foreach (var line in lines)
            sb.AppendLine($"({EscapePdfText(line)}) Tj T*");
        sb.Append("ET");
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

    private static string NormalizeText(string text)
        => new(text.Select(ch => ch is >= ' ' and <= '~' ? ch : ' ').ToArray());

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
