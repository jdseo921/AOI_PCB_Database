using System.Globalization;
using System.IO;
using System.Text;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public sealed class CustomerValidationReportContext
{
    public string ProjectName { get; init; } = "AOI Monitor Stage 1 Validation";
    public string StationId { get; init; } = "AOI-LIB-01";
    public string UserId { get; init; } = "UNKNOWN";
    public string UserRole { get; init; } = "UNKNOWN";
    public string RunId { get; init; } = "Not available";
    public DateTime TestTimestamp { get; init; } = DateTime.Now;
    public string BoardModel { get; init; } = "Not provided";
    public string LotId { get; init; } = "Not provided";
    public string ModelId { get; init; } = "Not selected";
    public string ModelSha256 { get; init; } = "Not available";
    public string ModelValidationStatus { get; init; } = "Not Tested";
    public string EngineName { get; init; } = "Pixel Difference Prototype Engine";
    public string ModelVersion { get; init; } = "PIXEL_DIFF_0.1";
    public string ModelFileName { get; init; } = "Not configured";
    public double ConfidenceThreshold { get; init; }
    public string DatasetFolder { get; init; } = "Not available";
    public string GroundTruthFile { get; init; } = "Not available";
    public BatchMetrics Metrics { get; init; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    public BatchPerformanceSummary PerformanceSummary { get; init; } = new(0, 0, 0, 0, 0);
    public string AcceptanceStatus { get; init; } = "CONDITIONAL";
    public IReadOnlyList<string> AcceptanceMessages { get; init; } = Array.Empty<string>();
    public ValidationAcceptanceCriteria AcceptanceCriteria { get; init; } = new();
    public IReadOnlyCollection<BatchTestRow> Rows { get; init; } = Array.Empty<BatchTestRow>();
    public IReadOnlyList<ReportImageReference> SampleAnnotatedImages { get; init; } = Array.Empty<ReportImageReference>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PrototypeLimitations { get; init; } = DefaultPrototypeLimitations;
    public FalseCallRecommendationSummary? FalseCallRecommendation { get; init; }
    public ValidationBreakdownSummary BreakdownSummary { get; init; } = new();
    public DatasetQualitySummary DatasetQualitySummary { get; init; } = new();
    public CameraAcceptanceSummary CameraAcceptanceSummary { get; init; } = new();
    public RobotAcceptanceSummary RobotAcceptanceSummary { get; init; } = new();
    public MesReadinessSummary MesReadinessSummary { get; init; } = new();

    public static IReadOnlyList<string> DefaultPrototypeLimitations { get; } =
    [
        "Stage 1 evidence is generated from local files and the local SQLite database.",
        "The Pixel Difference Prototype Engine is deterministic workflow evidence, not a trained production ML model.",
        "ONNX inference is claimed only when a configured local model loads and inference succeeds; no trained model is bundled by default.",
        "Folder Camera Simulation frames may be used for workflow validation. Live AOI camera, 3D camera, lighting, production robot, PLC, production MES, and ERP integrations remain planned for later stages.",
        "This report supports customer review of the Stage 1 proof of concept and should be paired with the agreed validation protocol before production acceptance.",
    ];
}

public static class CustomerValidationReportService
{
    public static string BuildHtml(CustomerValidationReportContext context)
    {
        var rows = context.Rows.ToArray();
        var failed = rows.Where(row => string.Equals(row.PassFail, "FAIL", StringComparison.OrdinalIgnoreCase)).ToArray();
        var passedCount = rows.Count(row => string.Equals(row.PassFail, "PASS", StringComparison.OrdinalIgnoreCase));
        var generatedAt = DateTime.Now;

        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"utf-8\">");
        sb.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.AppendLine("  <title>Stage 1 Customer Validation Report</title>");
        sb.AppendLine("  <style>");
        sb.AppendLine("    body { font-family: Segoe UI, Arial, sans-serif; margin: 32px; color: #1d252c; line-height: 1.45; }");
        sb.AppendLine("    h1 { margin: 0 0 6px; font-size: 30px; } h2 { margin-top: 30px; border-bottom: 2px solid #d7e0e6; padding-bottom: 6px; }");
        sb.AppendLine("    .subtitle { color: #50606a; margin-bottom: 24px; }");
        sb.AppendLine("    .notice { border-left: 5px solid #d9951b; background: #fff8e8; padding: 12px 14px; margin: 18px 0; }");
        sb.AppendLine("    .metric-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(160px, 1fr)); gap: 10px; margin: 14px 0 24px; }");
        sb.AppendLine("    .metric { border: 1px solid #c9d3da; padding: 10px 12px; background: #f7fafb; }");
        sb.AppendLine("    .metric b { display: block; font-size: 22px; margin-top: 4px; }");
        sb.AppendLine("    table { border-collapse: collapse; width: 100%; margin: 12px 0 22px; }");
        sb.AppendLine("    th, td { border: 1px solid #b8c1c8; padding: 7px 9px; text-align: left; vertical-align: top; }");
        sb.AppendLine("    th { background: #edf2f5; }");
        sb.AppendLine("    .details td:first-child { width: 230px; font-weight: 700; background: #f7fafb; }");
        sb.AppendLine("    .failed td { background: #fff0f1; }");
        sb.AppendLine("    .gallery { display: grid; grid-template-columns: repeat(auto-fit, minmax(260px, 1fr)); gap: 14px; }");
        sb.AppendLine("    figure { margin: 0; border: 1px solid #c9d3da; padding: 8px; background: #f8fafb; }");
        sb.AppendLine("    figure img { max-width: 100%; height: auto; display: block; }");
        sb.AppendLine("    figcaption { margin-top: 6px; color: #50606a; font-size: 12px; }");
        sb.AppendLine("    .signature td { height: 56px; }");
        sb.AppendLine("    @media print { body { margin: 18mm; } .notice { break-inside: avoid; } figure { break-inside: avoid; } }");
        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("  <h1>Stage 1 Customer Validation Report</h1>");
        sb.AppendLine($"  <div class=\"subtitle\">Project: {Html(context.ProjectName)} | Generated: {Html(generatedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))}</div>");
        sb.AppendLine("  <div class=\"notice\"><strong>Prototype boundary:</strong> This report documents Stage 1 offline validation evidence. It does not claim live camera, production robot, lighting, production MES/ERP, or ONNX ML Model operation unless those items are explicitly configured and verified in the evidence below.</div>");

        sb.AppendLine("  <h2>Run Information</h2>");
        sb.AppendLine("  <table class=\"details\">");
        AppendDetailRow(sb, "Project name", context.ProjectName);
        AppendDetailRow(sb, "Station ID", context.StationId);
        AppendDetailRow(sb, "Run ID", context.RunId);
        AppendDetailRow(sb, "Test timestamp", context.TestTimestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        AppendDetailRow(sb, "Operator / user ID", context.UserId);
        AppendDetailRow(sb, "Operator / user role", context.UserRole);
        AppendDetailRow(sb, "Board model", context.BoardModel);
        AppendDetailRow(sb, "Lot ID", context.LotId);
        AppendDetailRow(sb, "Engine name", context.EngineName);
        AppendDetailRow(sb, "Model ID", context.ModelId);
        AppendDetailRow(sb, "Model version", context.ModelVersion);
        AppendDetailRow(sb, "Model SHA-256", context.ModelSha256);
        AppendDetailRow(sb, "Model validation status", context.ModelValidationStatus);
        AppendDetailRow(sb, "Model file name", context.ModelFileName);
        AppendDetailRow(sb, "Confidence threshold", FormatThreshold(context.ConfidenceThreshold));
        AppendDetailRow(sb, "Dataset folder", context.DatasetFolder);
        AppendDetailRow(sb, "Ground truth file", context.GroundTruthFile);
        AppendDetailRow(sb, "Acceptance status", context.AcceptanceStatus);
        sb.AppendLine("  </table>");

        sb.AppendLine("  <h2>Acceptance Summary</h2>");
        sb.AppendLine("  <table class=\"details\">");
        AppendDetailRow(sb, "Status", context.AcceptanceStatus);
        AppendDetailRow(sb, "Minimum accuracy", FormatPercent(context.AcceptanceCriteria.MinimumAccuracy));
        AppendDetailRow(sb, "Minimum precision", FormatPercent(context.AcceptanceCriteria.MinimumPrecision));
        AppendDetailRow(sb, "Minimum recall", FormatPercent(context.AcceptanceCriteria.MinimumRecall));
        AppendDetailRow(sb, "Maximum false call rate", FormatPercent(context.AcceptanceCriteria.MaximumFalseCallRate));
        AppendDetailRow(sb, "Maximum images over 1 second", context.AcceptanceCriteria.MaximumImagesOverOneSecond.ToString(CultureInfo.InvariantCulture));
        AppendDetailRow(sb, "Formal manifest required", context.AcceptanceCriteria.RequireFormalManifest ? "Yes" : "No");
        sb.AppendLine("  </table>");
        if (context.AcceptanceMessages.Count == 0)
            sb.AppendLine("  <p>All configured acceptance checks passed.</p>");
        else
            AppendList(sb, context.AcceptanceMessages);

        sb.AppendLine("  <h2>Validation Summary</h2>");
        sb.AppendLine("  <div class=\"metric-grid\">");
        AppendMetric(sb, "Total images", rows.Length.ToString(CultureInfo.InvariantCulture));
        AppendMetric(sb, "Passed count", passedCount.ToString(CultureInfo.InvariantCulture));
        AppendMetric(sb, "Failed count", failed.Length.ToString(CultureInfo.InvariantCulture));
        AppendMetric(sb, "OK count", context.Metrics.OkCount.ToString(CultureInfo.InvariantCulture));
        AppendMetric(sb, "NG count", context.Metrics.NgCount.ToString(CultureInfo.InvariantCulture));
        AppendMetric(sb, "REVIEW count", context.Metrics.ReviewCount.ToString(CultureInfo.InvariantCulture));
        AppendMetric(sb, "False call count", context.Metrics.FalseCall.ToString(CultureInfo.InvariantCulture));
        AppendMetric(sb, "Possible escape count", context.Metrics.PossibleEscape.ToString(CultureInfo.InvariantCulture));
        AppendMetric(sb, "Accuracy", FormatPercent(context.Metrics.Accuracy));
        AppendMetric(sb, "Precision", FormatPercent(context.Metrics.Precision));
        AppendMetric(sb, "Recall", FormatPercent(context.Metrics.Recall));
        AppendMetric(sb, "False call rate", FormatPercent(context.Metrics.FalseCallRate));
        sb.AppendLine("  </div>");

        sb.AppendLine("  <h2>Inspection Performance</h2>");
        sb.AppendLine("  <div class=\"metric-grid\">");
        AppendMetric(sb, "Average time per image", FormatMilliseconds(context.PerformanceSummary.AverageMilliseconds));
        AppendMetric(sb, "Max time", FormatMilliseconds(context.PerformanceSummary.MaxMilliseconds));
        AppendMetric(sb, "Min time", FormatMilliseconds(context.PerformanceSummary.MinMilliseconds));
        AppendMetric(sb, "Count over 1 second", context.PerformanceSummary.CountOverOneSecond.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("  </div>");
        if (context.PerformanceSummary.CountOverOneSecond > 0)
            sb.AppendLine("  <div class=\"notice\"><strong>Performance warning:</strong> One or more images exceeded the 1 second per-image visualization target.</div>");

        AppendDatasetQualitySection(sb, context.DatasetQualitySummary);
        AppendCameraAcceptanceSection(sb, context.CameraAcceptanceSummary);
        AppendRobotAcceptanceSection(sb, context.RobotAcceptanceSummary);
        AppendMesReadinessSection(sb, context.MesReadinessSummary);
        AppendFalseCallLaborImpact(sb, context.FalseCallRecommendation);
        AppendBreakdownSection(sb, context.BreakdownSummary);

        sb.AppendLine("  <h2>Confusion Matrix</h2>");
        sb.AppendLine("  <table><tr><th></th><th>Predicted NG</th><th>Predicted OK</th></tr>");
        sb.AppendLine($"  <tr><th>Actual NG</th><td>TP: {context.Metrics.TruePositive}</td><td>FN: {context.Metrics.FalseNegative}</td></tr>");
        sb.AppendLine($"  <tr><th>Actual OK</th><td>FP: {context.Metrics.FalsePositive}</td><td>TN: {context.Metrics.TrueNegative}</td></tr></table>");

        sb.AppendLine("  <h2>Failed Sample Table</h2>");
        AppendFailedRowsTable(sb, failed);

        sb.AppendLine("  <h2>Sample Annotated Images</h2>");
        if (context.SampleAnnotatedImages.Count == 0)
        {
            sb.AppendLine("  <p>No sample annotated images were available for this report.</p>");
        }
        else
        {
            sb.AppendLine("  <div class=\"gallery\">");
            foreach (var image in context.SampleAnnotatedImages)
            {
                sb.AppendLine("    <figure>");
                sb.AppendLine($"      <img src=\"{HtmlAttribute(image.RelativePath)}\" alt=\"{HtmlAttribute(image.Caption)}\">");
                sb.AppendLine($"      <figcaption>{Html(image.Caption)}</figcaption>");
                sb.AppendLine("    </figure>");
            }

            sb.AppendLine("  </div>");
        }

        sb.AppendLine("  <h2>Prototype Limitations</h2>");
        AppendList(sb, context.PrototypeLimitations);

        sb.AppendLine("  <h2>Warnings / Missing Optional Evidence</h2>");
        if (context.Warnings.Count == 0)
            sb.AppendLine("  <p>No warnings were recorded for this report.</p>");
        else
            AppendList(sb, context.Warnings);

        sb.AppendLine("  <h2>PDF Export Instructions</h2>");
        sb.AppendLine("  <p>A PDF library is not bundled in this PoC build. To create a PDF, open this HTML file in a browser, choose Print, select Save as PDF, and save the generated PDF beside this report.</p>");

        sb.AppendLine("  <h2>Signature / Approval</h2>");
        sb.AppendLine("  <table class=\"signature\"><tr><th>Approval step</th><th>Name</th><th>Signature</th><th>Date</th></tr>");
        sb.AppendLine($"  <tr><td>Prepared by<br><small>Export operator: {Html(context.UserId)} [{Html(context.UserRole)}]</small></td><td></td><td></td><td></td></tr>");
        sb.AppendLine("  <tr><td>Reviewed by</td><td></td><td></td><td></td></tr>");
        sb.AppendLine("  <tr><td>Customer confirmation</td><td></td><td></td><td></td></tr>");
        sb.AppendLine("  </table>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }

    public static string BuildMarkdown(CustomerValidationReportContext context)
    {
        var rows = context.Rows.ToArray();
        var failed = rows.Where(row => string.Equals(row.PassFail, "FAIL", StringComparison.OrdinalIgnoreCase)).ToArray();
        var passedCount = rows.Count(row => string.Equals(row.PassFail, "PASS", StringComparison.OrdinalIgnoreCase));
        var sb = new StringBuilder();

        sb.AppendLine("# Stage 1 Customer Validation Report");
        sb.AppendLine();
        sb.AppendLine($"Project name: {EscapeMarkdown(context.ProjectName)}");
        sb.AppendLine($"Station ID: {EscapeMarkdown(context.StationId)}");
        sb.AppendLine($"Run ID: {EscapeMarkdown(context.RunId)}");
        sb.AppendLine($"Test timestamp: {context.TestTimestamp:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Operator / user ID: {EscapeMarkdown(context.UserId)}");
        sb.AppendLine($"Operator / user role: {EscapeMarkdown(context.UserRole)}");
        sb.AppendLine($"Board model: {EscapeMarkdown(context.BoardModel)}");
        sb.AppendLine($"Lot ID: {EscapeMarkdown(context.LotId)}");
        sb.AppendLine($"Engine name: {EscapeMarkdown(context.EngineName)}");
        sb.AppendLine($"Model ID: {EscapeMarkdown(context.ModelId)}");
        sb.AppendLine($"Model version: {EscapeMarkdown(context.ModelVersion)}");
        sb.AppendLine($"Model SHA-256: {EscapeMarkdown(context.ModelSha256)}");
        sb.AppendLine($"Model validation status: {EscapeMarkdown(context.ModelValidationStatus)}");
        sb.AppendLine($"Model file name: {EscapeMarkdown(context.ModelFileName)}");
        sb.AppendLine($"Confidence threshold: {FormatThreshold(context.ConfidenceThreshold)}");
        sb.AppendLine($"Dataset folder: {EscapeMarkdown(context.DatasetFolder)}");
        sb.AppendLine($"Ground truth file: {EscapeMarkdown(context.GroundTruthFile)}");
        sb.AppendLine($"Acceptance status: {EscapeMarkdown(context.AcceptanceStatus)}");
        sb.AppendLine();
        sb.AppendLine("## Acceptance Summary");
        sb.AppendLine();
        sb.AppendLine($"- Minimum accuracy: {FormatPercent(context.AcceptanceCriteria.MinimumAccuracy)}");
        sb.AppendLine($"- Minimum precision: {FormatPercent(context.AcceptanceCriteria.MinimumPrecision)}");
        sb.AppendLine($"- Minimum recall: {FormatPercent(context.AcceptanceCriteria.MinimumRecall)}");
        sb.AppendLine($"- Maximum false call rate: {FormatPercent(context.AcceptanceCriteria.MaximumFalseCallRate)}");
        sb.AppendLine($"- Maximum images over 1 second: {context.AcceptanceCriteria.MaximumImagesOverOneSecond}");
        sb.AppendLine($"- Formal manifest required: {(context.AcceptanceCriteria.RequireFormalManifest ? "Yes" : "No")}");
        if (context.AcceptanceMessages.Count == 0)
        {
            sb.AppendLine("- All configured acceptance checks passed.");
        }
        else
        {
            foreach (var message in context.AcceptanceMessages)
                sb.AppendLine($"- {EscapeMarkdown(message)}");
        }

        sb.AppendLine();
        sb.AppendLine("## Validation Summary");
        sb.AppendLine();
        sb.AppendLine($"- Total images: {rows.Length}");
        sb.AppendLine($"- Passed count: {passedCount}");
        sb.AppendLine($"- Failed count: {failed.Length}");
        sb.AppendLine($"- OK count: {context.Metrics.OkCount}");
        sb.AppendLine($"- NG count: {context.Metrics.NgCount}");
        sb.AppendLine($"- REVIEW count: {context.Metrics.ReviewCount}");
        sb.AppendLine($"- False call count: {context.Metrics.FalseCall}");
        sb.AppendLine($"- Possible escape count: {context.Metrics.PossibleEscape}");
        sb.AppendLine($"- Accuracy: {FormatPercent(context.Metrics.Accuracy)}");
        sb.AppendLine($"- Precision: {FormatPercent(context.Metrics.Precision)}");
        sb.AppendLine($"- Recall: {FormatPercent(context.Metrics.Recall)}");
        sb.AppendLine($"- False call rate: {FormatPercent(context.Metrics.FalseCallRate)}");
        sb.AppendLine();
        sb.AppendLine("## Inspection Performance");
        sb.AppendLine();
        sb.AppendLine($"- Average time per image: {FormatMilliseconds(context.PerformanceSummary.AverageMilliseconds)}");
        sb.AppendLine($"- Max time: {FormatMilliseconds(context.PerformanceSummary.MaxMilliseconds)}");
        sb.AppendLine($"- Min time: {FormatMilliseconds(context.PerformanceSummary.MinMilliseconds)}");
        sb.AppendLine($"- Count over 1 second: {context.PerformanceSummary.CountOverOneSecond}");
        AppendMarkdownDatasetQualitySection(sb, context.DatasetQualitySummary);
        AppendMarkdownCameraAcceptanceSection(sb, context.CameraAcceptanceSummary);
        AppendMarkdownRobotAcceptanceSection(sb, context.RobotAcceptanceSummary);
        AppendMarkdownMesReadinessSection(sb, context.MesReadinessSummary);
        AppendMarkdownFalseCallLaborImpact(sb, context.FalseCallRecommendation);
        AppendMarkdownBreakdownSection(sb, context.BreakdownSummary);
        sb.AppendLine();
        sb.AppendLine("## Confusion Matrix");
        sb.AppendLine();
        sb.AppendLine("| Actual / Predicted | NG | OK |");
        sb.AppendLine("|---|---:|---:|");
        sb.AppendLine($"| NG | TP {context.Metrics.TruePositive} | FN {context.Metrics.FalseNegative} |");
        sb.AppendLine($"| OK | FP {context.Metrics.FalsePositive} | TN {context.Metrics.TrueNegative} |");
        sb.AppendLine();
        sb.AppendLine("## Failed Sample Table");
        sb.AppendLine();
        AppendMarkdownFailedRows(sb, failed);
        sb.AppendLine();
        sb.AppendLine("## Sample Annotated Images");
        sb.AppendLine();
        if (context.SampleAnnotatedImages.Count == 0)
        {
            sb.AppendLine("No sample annotated images were available for this report.");
        }
        else
        {
            foreach (var image in context.SampleAnnotatedImages)
                sb.AppendLine($"![{EscapeMarkdown(image.Caption)}]({image.RelativePath})");
        }

        sb.AppendLine();
        sb.AppendLine("## Prototype Limitations");
        sb.AppendLine();
        foreach (var limitation in context.PrototypeLimitations)
            sb.AppendLine($"- {EscapeMarkdown(limitation)}");

        sb.AppendLine();
        sb.AppendLine("## Warnings / Missing Optional Evidence");
        sb.AppendLine();
        if (context.Warnings.Count == 0)
            sb.AppendLine("No warnings were recorded for this report.");
        else
            foreach (var warning in context.Warnings)
                sb.AppendLine($"- {EscapeMarkdown(warning)}");

        sb.AppendLine();
        sb.AppendLine("## Signature / Approval");
        sb.AppendLine();
        sb.AppendLine("| Approval step | Name | Signature | Date |");
        sb.AppendLine("|---|---|---|---|");
        sb.AppendLine($"| Prepared by (export operator: {EscapeMarkdown(context.UserId)} [{EscapeMarkdown(context.UserRole)}]) |  |  |  |");
        sb.AppendLine("| Reviewed by |  |  |  |");
        sb.AppendLine("| Customer confirmation |  |  |  |");

        return sb.ToString();
    }

    public static string BuildPrintToPdfInstructions(string htmlReportPath)
    {
        var reportName = Path.GetFileName(htmlReportPath);
        return $"""
        Stage 1 Customer Validation Report - Print to PDF

        HTML report:
        {htmlReportPath}

        This PoC build exports a browser-readable HTML report plus these print-to-PDF instructions instead of bundling a PDF rendering dependency.

        To create a PDF:
        1. Open {reportName} in Microsoft Edge, Chrome, or another browser.
        2. Press Ctrl+P.
        3. Select "Save as PDF" as the printer/destination.
        4. Use landscape orientation if the failed-sample table is wide.
        5. Save the PDF in the same folder as the HTML report so image references and package evidence remain together.

        """;
    }

    public static string SummarizeDistinct(IEnumerable<string> values)
    {
        var distinct = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();

        return distinct.Length == 0 ? "Not provided" : string.Join(", ", distinct);
    }

    private static void AppendDetailRow(StringBuilder sb, string label, string value)
    {
        sb.AppendLine($"    <tr><td>{Html(label)}</td><td>{Html(value)}</td></tr>");
    }

    private static void AppendMetric(StringBuilder sb, string label, string value)
    {
        sb.AppendLine($"    <div class=\"metric\">{Html(label)}<b>{Html(value)}</b></div>");
    }

    private static void AppendFailedRowsTable(StringBuilder sb, IReadOnlyCollection<BatchTestRow> failed)
    {
        sb.AppendLine("  <table><tr><th>Image</th><th>Ground Truth</th><th>Engine Result</th><th>Score</th><th>Total Time</th><th>Defect Type</th><th>ROI ID</th><th>ROI Type</th><th>Recipe</th><th>Side</th><th>RefDes</th><th>Lot ID</th><th>Board Model</th><th>Notes</th><th>Image Path</th></tr>");
        if (failed.Count == 0)
        {
            sb.AppendLine("  <tr><td colspan=\"15\">No failed samples were recorded.</td></tr>");
        }
        else
        {
            foreach (var row in failed)
            {
                sb.AppendLine($"  <tr class=\"failed\"><td>{Html(row.Image)}</td><td>{Html(row.GroundTruth)}</td><td>{Html(row.EngineResult)}</td><td>{Html(row.ScoreDisplay)}</td><td>{Html(row.TotalTimeDisplay)}</td><td>{Html(row.DefectType)}</td><td>{Html(row.RoiId)}</td><td>{Html(row.RoiType)}</td><td>{Html(RecipeDisplay(row))}</td><td>{Html(row.Side)}</td><td>{Html(row.RefDes)}</td><td>{Html(row.LotId)}</td><td>{Html(row.BoardModel)}</td><td>{Html(row.Notes)}</td><td>{Html(row.ImagePath)}</td></tr>");
            }
        }

        sb.AppendLine("  </table>");
    }

    private static void AppendMarkdownFailedRows(StringBuilder sb, IReadOnlyCollection<BatchTestRow> failed)
    {
        if (failed.Count == 0)
        {
            sb.AppendLine("No failed samples were recorded.");
            return;
        }

        sb.AppendLine("| Image | Ground Truth | Engine Result | Score | Total Time | Defect Type | ROI ID | ROI Type | Recipe | Side | RefDes | Lot ID | Board Model | Notes | Image Path |");
        sb.AppendLine("|---|---|---|---:|---:|---|---|---|---|---|---|---|---|---|---|");
        foreach (var row in failed)
        {
            sb.AppendLine($"| {EscapeMarkdown(row.Image)} | {EscapeMarkdown(row.GroundTruth)} | {EscapeMarkdown(row.EngineResult)} | {EscapeMarkdown(row.ScoreDisplay)} | {EscapeMarkdown(row.TotalTimeDisplay)} | {EscapeMarkdown(row.DefectType)} | {EscapeMarkdown(row.RoiId)} | {EscapeMarkdown(row.RoiType)} | {EscapeMarkdown(RecipeDisplay(row))} | {EscapeMarkdown(row.Side)} | {EscapeMarkdown(row.RefDes)} | {EscapeMarkdown(row.LotId)} | {EscapeMarkdown(row.BoardModel)} | {EscapeMarkdown(row.Notes)} | {EscapeMarkdown(row.ImagePath)} |");
        }
    }

    private static string RecipeDisplay(BatchTestRow row)
        => string.IsNullOrWhiteSpace(row.RecipeName) && string.IsNullOrWhiteSpace(row.RecipeRevision)
            ? string.Empty
            : $"{row.RecipeName} {row.RecipeRevision}".Trim();

    private static void AppendList(StringBuilder sb, IReadOnlyList<string> items)
    {
        sb.AppendLine("  <ul>");
        foreach (var item in items)
            sb.AppendLine($"    <li>{Html(item)}</li>");
        sb.AppendLine("  </ul>");
    }

    private static void AppendFalseCallLaborImpact(StringBuilder sb, FalseCallRecommendationSummary? summary)
    {
        sb.AppendLine("  <h2>False Call Reduction / Labor Impact</h2>");
        if (summary is null || string.Equals(summary.Status, "Not available", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine("  <p>No false-call reduction recommendation was available for this package.</p>");
            return;
        }

        sb.AppendLine("  <table class=\"details\">");
        AppendDetailRow(sb, "Recommendation status", summary.Status);
        AppendDetailRow(sb, "Operating mode", summary.Mode);
        AppendDetailRow(sb, "Selected threshold", summary.SelectedThreshold.ToString("F3", CultureInfo.InvariantCulture));
        AppendDetailRow(sb, "False call rate", FormatPercent(summary.FalseCallRate));
        AppendDetailRow(sb, "Possible escape count", summary.PossibleEscapeCount.ToString(CultureInfo.InvariantCulture));
        AppendDetailRow(sb, "Possible escape rate", FormatPercent(summary.PossibleEscapeRate));
        AppendDetailRow(sb, "Review burden estimate", $"{summary.EstimatedManualReviewMinutes:F1} minutes ({FormatPercent(summary.ReviewRate)} review rate)");
        sb.AppendLine("  </table>");
        if (summary.Limitations.Count > 0)
            AppendList(sb, summary.Limitations);
    }

    private static void AppendMarkdownFalseCallLaborImpact(StringBuilder sb, FalseCallRecommendationSummary? summary)
    {
        sb.AppendLine();
        sb.AppendLine("## False Call Reduction / Labor Impact");
        sb.AppendLine();
        if (summary is null || string.Equals(summary.Status, "Not available", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine("No false-call reduction recommendation was available for this package.");
            return;
        }

        sb.AppendLine($"- Recommendation status: {EscapeMarkdown(summary.Status)}");
        sb.AppendLine($"- Operating mode: {EscapeMarkdown(summary.Mode)}");
        sb.AppendLine($"- Selected threshold: {summary.SelectedThreshold:F3}");
        sb.AppendLine($"- False call rate: {FormatPercent(summary.FalseCallRate)}");
        sb.AppendLine($"- Possible escape count: {summary.PossibleEscapeCount}");
        sb.AppendLine($"- Possible escape rate: {FormatPercent(summary.PossibleEscapeRate)}");
        sb.AppendLine($"- Review burden estimate: {summary.EstimatedManualReviewMinutes:F1} minutes ({FormatPercent(summary.ReviewRate)} review rate)");
        foreach (var limitation in summary.Limitations)
            sb.AppendLine($"- {EscapeMarkdown(limitation)}");
    }

    private static void AppendDatasetQualitySection(StringBuilder sb, DatasetQualitySummary summary)
    {
        sb.AppendLine("  <h2>Dataset Quality Gate</h2>");
        sb.AppendLine("  <table class=\"details\">");
        AppendDetailRow(sb, "Status", summary.Status);
        AppendDetailRow(sb, "Total images", summary.TotalImages.ToString(CultureInfo.InvariantCulture));
        AppendDetailRow(sb, "Known ground truth", summary.KnownGroundTruthImages.ToString(CultureInfo.InvariantCulture));
        AppendDetailRow(sb, "OK / NG images", $"{summary.OkImages} / {summary.NgImages}");
        AppendDetailRow(sb, "Defect classes", summary.DefectClassCount.ToString(CultureInfo.InvariantCulture));
        AppendDetailRow(sb, "Unknown label rate", FormatPercent(summary.UnknownLabelRate));
        AppendDetailRow(sb, "Missing golden rate", FormatPercent(summary.MissingGoldenRate));
        AppendDetailRow(sb, "Duplicate names / hashes", $"{summary.DuplicateImageNames} / {summary.DuplicateFileHashes}");
        sb.AppendLine("  </table>");
        if (summary.BlockingFailures.Count > 0)
        {
            sb.AppendLine("  <div class=\"notice\"><strong>Dataset not sufficient for factory acceptance.</strong></div>");
            AppendList(sb, summary.BlockingFailures);
        }
        if (summary.Warnings.Count > 0)
            AppendList(sb, summary.Warnings);
    }

    private static void AppendMarkdownDatasetQualitySection(StringBuilder sb, DatasetQualitySummary summary)
    {
        sb.AppendLine();
        sb.AppendLine("## Dataset Quality Gate");
        sb.AppendLine();
        sb.AppendLine($"- Status: {EscapeMarkdown(summary.Status)}");
        sb.AppendLine($"- Total images: {summary.TotalImages}");
        sb.AppendLine($"- Known ground truth: {summary.KnownGroundTruthImages}");
        sb.AppendLine($"- OK / NG images: {summary.OkImages} / {summary.NgImages}");
        sb.AppendLine($"- Defect classes: {summary.DefectClassCount}");
        sb.AppendLine($"- Unknown label rate: {FormatPercent(summary.UnknownLabelRate)}");
        sb.AppendLine($"- Missing golden rate: {FormatPercent(summary.MissingGoldenRate)}");
        if (summary.BlockingFailures.Count > 0)
        {
            sb.AppendLine("- Dataset not sufficient for factory acceptance.");
            foreach (var failure in summary.BlockingFailures)
                sb.AppendLine($"- {EscapeMarkdown(failure)}");
        }
        foreach (var warning in summary.Warnings)
            sb.AppendLine($"- {EscapeMarkdown(warning)}");
    }

    private static void AppendCameraAcceptanceSection(StringBuilder sb, CameraAcceptanceSummary summary)
    {
        sb.AppendLine("  <h2>Stage 2 Camera Acceptance / Factory Readiness</h2>");
        sb.AppendLine("  <table class=\"details\">");
        AppendDetailRow(sb, "Factory readiness", summary.Status);
        AppendDetailRow(sb, "Acceptance run", summary.AcceptanceStatus);
        AppendDetailRow(sb, "Real hardware", summary.IsRealHardware ? "Yes" : "No");
        AppendDetailRow(sb, "Adapter", string.IsNullOrWhiteSpace(summary.AdapterName) ? "Not available" : summary.AdapterName);
        AppendDetailRow(sb, "Frames received / requested", $"{summary.TotalReceivedFrames} / {summary.TotalRequestedFrames}");
        AppendDetailRow(sb, "Dropped / trigger / timeout", $"{summary.DroppedFrameCount} / {summary.TriggerFailureCount} / {summary.TimeoutCount}");
        sb.AppendLine("  </table>");
        if (!summary.IsRealHardware)
            sb.AppendLine("  <div class=\"notice\"><strong>Real camera acceptance is NOT VALIDATED.</strong> Fake, null, and folder adapters do not prove GigE/USB3 production camera readiness.</div>");
        if (summary.Messages.Count > 0)
            AppendList(sb, summary.Messages.Take(6).ToArray());
    }

    private static void AppendMarkdownCameraAcceptanceSection(StringBuilder sb, CameraAcceptanceSummary summary)
    {
        sb.AppendLine();
        sb.AppendLine("## Stage 2 Camera Acceptance / Factory Readiness");
        sb.AppendLine();
        sb.AppendLine($"- Factory readiness: {EscapeMarkdown(summary.Status)}");
        sb.AppendLine($"- Acceptance run: {EscapeMarkdown(summary.AcceptanceStatus)}");
        sb.AppendLine($"- Real hardware: {(summary.IsRealHardware ? "Yes" : "No")}");
        sb.AppendLine($"- Adapter: {EscapeMarkdown(string.IsNullOrWhiteSpace(summary.AdapterName) ? "Not available" : summary.AdapterName)}");
        sb.AppendLine($"- Frames received / requested: {summary.TotalReceivedFrames} / {summary.TotalRequestedFrames}");
        sb.AppendLine($"- Dropped / trigger / timeout: {summary.DroppedFrameCount} / {summary.TriggerFailureCount} / {summary.TimeoutCount}");
        if (!summary.IsRealHardware)
            sb.AppendLine("- Real camera acceptance is NOT VALIDATED. Fake, null, and folder adapters do not prove GigE/USB3 production camera readiness.");
        foreach (var message in summary.Messages.Take(6))
            sb.AppendLine($"- {EscapeMarkdown(message)}");
    }

    private static void AppendRobotAcceptanceSection(StringBuilder sb, RobotAcceptanceSummary summary)
    {
        sb.AppendLine("  <h2>Robot Cell Acceptance / Factory Readiness</h2>");
        sb.AppendLine("  <table class=\"details\">");
        AppendDetailRow(sb, "Status", summary.Status);
        AppendDetailRow(sb, "Source kind", summary.SourceKind);
        AppendDetailRow(sb, "Controller", string.IsNullOrWhiteSpace(summary.ControllerName) ? "Not available" : summary.ControllerName);
        AppendDetailRow(sb, "Full cycle time", FormatMilliseconds(summary.FullCycleMs));
        AppendDetailRow(sb, "Emergency stop blocked cycle", summary.EmergencyStopBlocked ? "Yes" : "No");
        AppendDetailRow(sb, "Invalid transition rejected", summary.InvalidTransitionRejected ? "Yes" : "No");
        AppendDetailRow(sb, "Reset returned Idle", summary.ResetReturnedIdle ? "Yes" : "No");
        sb.AppendLine("  </table>");
        if (string.Equals(summary.SourceKind, "Simulated", StringComparison.OrdinalIgnoreCase))
            sb.AppendLine("  <div class=\"notice\"><strong>Simulation evidence only; no production robot movement was validated.</strong></div>");
        sb.AppendLine("  <div class=\"notice\"><strong>No safety-certified emergency-stop validation is claimed by this prototype evidence.</strong></div>");
        if (summary.Messages.Count > 0)
            AppendList(sb, summary.Messages.Take(6).ToArray());
    }

    private static void AppendMarkdownRobotAcceptanceSection(StringBuilder sb, RobotAcceptanceSummary summary)
    {
        sb.AppendLine();
        sb.AppendLine("## Robot Cell Acceptance / Factory Readiness");
        sb.AppendLine();
        sb.AppendLine($"- Status: {EscapeMarkdown(summary.Status)}");
        sb.AppendLine($"- Source kind: {EscapeMarkdown(summary.SourceKind)}");
        sb.AppendLine($"- Controller: {EscapeMarkdown(string.IsNullOrWhiteSpace(summary.ControllerName) ? "Not available" : summary.ControllerName)}");
        sb.AppendLine($"- Full cycle time: {FormatMilliseconds(summary.FullCycleMs)}");
        sb.AppendLine($"- Emergency stop blocked cycle: {(summary.EmergencyStopBlocked ? "Yes" : "No")}");
        sb.AppendLine($"- Invalid transition rejected: {(summary.InvalidTransitionRejected ? "Yes" : "No")}");
        sb.AppendLine($"- Reset returned Idle: {(summary.ResetReturnedIdle ? "Yes" : "No")}");
        if (string.Equals(summary.SourceKind, "Simulated", StringComparison.OrdinalIgnoreCase))
            sb.AppendLine("- Simulation evidence only; no production robot movement was validated.");
        sb.AppendLine("- No safety-certified emergency-stop validation is claimed by this prototype evidence.");
        foreach (var message in summary.Messages.Take(6))
            sb.AppendLine($"- {EscapeMarkdown(message)}");
    }

    private static void AppendMesReadinessSection(StringBuilder sb, MesReadinessSummary summary)
    {
        sb.AppendLine("  <h2>MES REST / Spool Readiness</h2>");
        sb.AppendLine("  <table class=\"details\">");
        AppendDetailRow(sb, "Status", summary.Status);
        AppendDetailRow(sb, "Mode", summary.Mode);
        AppendDetailRow(sb, "Queue pending / failed / sent / abandoned", $"{summary.PendingCount} / {summary.FailedCount} / {summary.SentCount} / {summary.AbandonedCount}");
        sb.AppendLine("  </table>");
        if (summary.PendingCount > 0 || summary.FailedCount > 0)
            sb.AppendLine("  <div class=\"notice\"><strong>MES queue requires attention before factory readiness can be treated as clean.</strong></div>");
        if (string.Equals(summary.Status, "MES Mock Only", StringComparison.OrdinalIgnoreCase))
            sb.AppendLine("  <div class=\"notice\"><strong>Mock MES is not production MES writeback.</strong></div>");
        if (summary.Messages.Count > 0)
            AppendList(sb, summary.Messages.Take(6).ToArray());
    }

    private static void AppendMarkdownMesReadinessSection(StringBuilder sb, MesReadinessSummary summary)
    {
        sb.AppendLine();
        sb.AppendLine("## MES REST / Spool Readiness");
        sb.AppendLine();
        sb.AppendLine($"- Status: {EscapeMarkdown(summary.Status)}");
        sb.AppendLine($"- Mode: {EscapeMarkdown(summary.Mode)}");
        sb.AppendLine($"- Queue pending / failed / sent / abandoned: {summary.PendingCount} / {summary.FailedCount} / {summary.SentCount} / {summary.AbandonedCount}");
        if (summary.PendingCount > 0 || summary.FailedCount > 0)
            sb.AppendLine("- MES queue requires attention before factory readiness can be treated as clean.");
        if (string.Equals(summary.Status, "MES Mock Only", StringComparison.OrdinalIgnoreCase))
            sb.AppendLine("- Mock MES is not production MES writeback.");
        foreach (var message in summary.Messages.Take(6))
            sb.AppendLine($"- {EscapeMarkdown(message)}");
    }

    private static void AppendBreakdownSection(StringBuilder sb, ValidationBreakdownSummary summary)
    {
        sb.AppendLine("  <h2>Validation Breakdown Evidence</h2>");
        sb.AppendLine("  <p>Breakdowns are derived from customer-labeled Stage 1 rows and do not prove production accuracy.</p>");
        AppendBreakdownTable(sb, "Defect Class", summary.DefectClassMetrics);
        AppendBreakdownTable(sb, "Side / View", summary.SideMetrics);
        AppendBreakdownTable(sb, "ROI ID", summary.RoiMetrics);
        AppendBreakdownTable(sb, "ROI Type", summary.RoiTypeMetrics);
        AppendBreakdownTable(sb, "Worst False Call Sources", summary.TopFalseCallContributors);
        AppendBreakdownTable(sb, "Possible Escape Sources", summary.TopPossibleEscapeContributors);
    }

    private static void AppendBreakdownTable(StringBuilder sb, string title, IReadOnlyList<ValidationBreakdownMetric> metrics)
    {
        sb.AppendLine($"  <h3>{Html(title)}</h3>");
        sb.AppendLine("  <table><tr><th>Source</th><th>Total</th><th>TP</th><th>TN</th><th>FP</th><th>FN</th><th>Wrong class</th><th>Wrong side</th><th>Precision</th><th>Recall</th><th>False call rate</th></tr>");
        if (metrics.Count == 0)
        {
            sb.AppendLine("  <tr><td colspan=\"11\">No breakdown rows available.</td></tr>");
        }
        else
        {
            foreach (var metric in metrics.Take(12))
            {
                sb.AppendLine($"  <tr><td>{Html(metric.DisplayName)}</td><td>{metric.Total}</td><td>{metric.TruePositive}</td><td>{metric.TrueNegative}</td><td>{metric.FalsePositive}</td><td>{metric.FalseNegative}</td><td>{metric.WrongDefectClass}</td><td>{metric.WrongSide}</td><td>{FormatPercent(metric.Precision)}</td><td>{FormatPercent(metric.Recall)}</td><td>{FormatPercent(metric.FalseCallRate)}</td></tr>");
            }
        }

        sb.AppendLine("  </table>");
    }

    private static void AppendMarkdownBreakdownSection(StringBuilder sb, ValidationBreakdownSummary summary)
    {
        sb.AppendLine();
        sb.AppendLine("## Validation Breakdown Evidence");
        sb.AppendLine();
        sb.AppendLine("Breakdowns are derived from customer-labeled Stage 1 rows and do not prove production accuracy.");
        AppendMarkdownBreakdownTable(sb, "Defect Class", summary.DefectClassMetrics);
        AppendMarkdownBreakdownTable(sb, "Side / View", summary.SideMetrics);
        AppendMarkdownBreakdownTable(sb, "ROI ID", summary.RoiMetrics);
    }

    private static void AppendMarkdownBreakdownTable(StringBuilder sb, string title, IReadOnlyList<ValidationBreakdownMetric> metrics)
    {
        sb.AppendLine();
        sb.AppendLine($"### {EscapeMarkdown(title)}");
        sb.AppendLine();
        sb.AppendLine("| Source | Total | TP | TN | FP | FN | Wrong class | Wrong side | Precision | Recall | False call |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var metric in metrics.Take(12))
        {
            sb.AppendLine($"| {EscapeMarkdown(metric.DisplayName)} | {metric.Total} | {metric.TruePositive} | {metric.TrueNegative} | {metric.FalsePositive} | {metric.FalseNegative} | {metric.WrongDefectClass} | {metric.WrongSide} | {FormatPercent(metric.Precision)} | {FormatPercent(metric.Recall)} | {FormatPercent(metric.FalseCallRate)} |");
        }
    }

    private static string FormatThreshold(double value)
        => $"{value.ToString("F3", CultureInfo.InvariantCulture)} ({value.ToString("P0", CultureInfo.InvariantCulture)})";

    private static string FormatPercent(double value)
        => double.IsNaN(value) ? "--" : value.ToString("P1", CultureInfo.InvariantCulture);

    private static string FormatMilliseconds(double value)
        => value <= 0 ? "--" : $"{value:F0} ms";

    private static string Html(string? value)
        => (value ?? string.Empty)
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);

    private static string HtmlAttribute(string? value) => Html(value);

    private static string EscapeMarkdown(string? value)
        => (value ?? string.Empty)
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
}
