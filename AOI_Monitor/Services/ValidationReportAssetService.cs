using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AOI_Monitor.Services;

public sealed record ReportImageReference(string RelativePath, string Caption);

public sealed record ValidationReportAssetResult(
    IReadOnlyList<ReportImageReference> Images,
    IReadOnlyList<string> Warnings);

public static class ValidationReportAssetService
{
    public static ValidationReportAssetResult ExportSampleAnnotatedImages(
        IReadOnlyCollection<BatchTestRow> rows,
        string outputFolder,
        string relativeFolder,
        int maxCount = 8,
        CancellationToken token = default)
    {
        Directory.CreateDirectory(outputFolder);
        var images = new List<ReportImageReference>();
        var warnings = new List<string>();
        var orderedRows = rows
            .OrderByDescending(row => row.IsFailed)
            .ThenBy(row => row.Image, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var row in orderedRows)
        {
            token.ThrowIfCancellationRequested();
            if (images.Count >= maxCount)
                break;

            if (string.IsNullOrWhiteSpace(row.ImagePath) || !File.Exists(row.ImagePath))
            {
                if (row.IsFailed)
                    warnings.Add($"Sample annotated image skipped for failed row '{row.Image}' because the source image is missing.");
                continue;
            }

            try
            {
                var fileName = $"{images.Count + 1:00}_{SanitizeFileName(Path.GetFileNameWithoutExtension(row.Image))}_annotated.png";
                var path = Path.Combine(outputFolder, fileName);
                SavePng(CreateAnnotatedBitmap(row), path);
                images.Add(new ReportImageReference(
                    CombineRelativePath(relativeFolder, fileName),
                    $"{row.Image} - {row.EngineResult} / {row.PassFail}"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
            {
                warnings.Add($"Sample annotated image skipped for '{row.Image}': {FriendlyFileError(row.ImagePath, ex)}");
            }
        }

        if (rows.Count > 0 && images.Count == 0)
            warnings.Add("No sample annotated images could be generated for the validation report.");

        return new ValidationReportAssetResult(images, warnings);
    }

    private static RenderTargetBitmap CreateAnnotatedBitmap(BatchTestRow row)
    {
        var source = LoadBitmap(row.ImagePath);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));

            var roi = BuildRoi(row, source.PixelWidth, source.PixelHeight);
            var color = row.IsFailed
                ? Colors.Red
                : string.Equals(row.EngineResult, "OK", StringComparison.OrdinalIgnoreCase)
                    ? Colors.LimeGreen
                    : Colors.Orange;
            var brush = new SolidColorBrush(color);
            var pen = new Pen(brush, Math.Max(3, source.PixelWidth / 300.0));
            dc.DrawRectangle(null, pen, roi);

            var label = $"{row.EngineResult} / {row.PassFail}";
            var text = new FormattedText(
                label,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                Math.Max(16, source.PixelWidth / 32.0),
                brush,
                1.0);

            var textOrigin = new Point(Math.Max(0, roi.X), Math.Max(0, roi.Y - text.Height - 6));
            dc.DrawRectangle(Brushes.Black, null, new Rect(textOrigin.X, textOrigin.Y, text.Width + 10, text.Height + 6));
            dc.DrawText(text, new Point(textOrigin.X + 5, textOrigin.Y + 3));
        }

        var target = new RenderTargetBitmap(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static Rect BuildRoi(BatchTestRow row, int width, int height)
    {
        var roiWidth = row.RoiWidth <= 0 ? 0.18 : row.RoiWidth;
        var roiHeight = row.RoiHeight <= 0 ? 0.18 : row.RoiHeight;
        var x = Clamp(row.RoiX, 0, 1);
        var y = Clamp(row.RoiY, 0, 1);
        var w = Math.Max(2, Math.Min(roiWidth, 1 - x) * width);
        var h = Math.Max(2, Math.Min(roiHeight, 1 - y) * height);
        return new Rect(x * width, y * height, w, h);
    }

    private static BitmapSource LoadBitmap(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(path, UriKind.Absolute);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static string CombineRelativePath(string folder, string fileName)
    {
        return string.IsNullOrWhiteSpace(folder)
            ? fileName.Replace('\\', '/')
            : Path.Combine(folder, fileName).Replace('\\', '/');
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var sanitized = new string(chars).Trim('_', ' ');
        return string.IsNullOrWhiteSpace(sanitized) ? "sample" : sanitized;
    }

    private static string FriendlyFileError(string path, Exception ex)
    {
        return ex switch
        {
            UnauthorizedAccessException => $"access denied or locked file: {path}",
            NotSupportedException => $"unsupported image: {path}",
            InvalidOperationException => $"image could not be decoded: {path}",
            _ => $"{ex.Message} ({path})",
        };
    }

    private static double Clamp(double value, double min, double max)
        => value < min ? min : value > max ? max : value;
}
