using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;

namespace AOI_Monitor.Tools;

public static class LearnFromImagesCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static int Execute(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            WriteUsage(output);
            return args.Length == 0 ? 2 : 0;
        }

        if (!string.Equals(args[0], "learn-from-images", StringComparison.OrdinalIgnoreCase))
        {
            error.WriteLine($"FAIL Unknown command: {args[0]}");
            WriteUsage(error);
            return 2;
        }

        var parse = Parse(args.Skip(1).ToArray());
        if (!parse.Success)
        {
            error.WriteLine($"FAIL {parse.Message}");
            WriteUsage(error);
            return 2;
        }

        try
        {
            AoiDatabase.Initialize();
            var outputFolder = Path.GetFullPath(parse.OutputFolder);
            Directory.CreateDirectory(outputFolder);

            var options = CreateLearningOptions(parse.FalseCallTarget);
            var import = ImageLearningFolderImportService.ImportProjectFolder(
                parse.ProjectFolder,
                parse.OperatorId,
                ImageLearningEvidenceMode.CustomerData,
                BuildProjectName(parse.ProjectFolder),
                parse.BoardModel);
            ValidateImportedProject(import);

            var learning = ImageOnlyPcbLearningService.TrainProject(import.Project.ProjectId, options, parse.OperatorId);
            var inspectionImages = ImageLearningProjectService.GetProjectImages(import.Project.ProjectId, ImageLearningImageRole.Inspection);
            foreach (var image in inspectionImages)
                ImageOnlyPcbLearningService.InspectProjectImage(learning.Model.ModelId, image, options, parse.OperatorId);

            var comparison = ImageLearningFalseCallComparisonService.RunComparison(
                import.Project.ProjectId,
                learning.Model.ModelId,
                options,
                parse.OperatorId);
            var overlays = ImageLearningOverlayExportService.ExportProjectVisualEvidence(
                import.Project.ProjectId,
                learning.Model.ModelId,
                new ImageLearningOverlayExportOptions
                {
                    LearningOptions = options,
                    MaxBatchExamples = 10,
                },
                parse.OperatorId);
            var report = ImageOnlyLearningReportService.ExportReport(
                import.Project.ProjectId,
                learning.Model.ModelId,
                new ImageOnlyLearningReportOptions
                {
                    GeneratePdf = false,
                    LearningOptions = options,
                },
                parse.OperatorId);

            var warnings = BuildWarnings(import, learning, comparison, overlays, report);
            var status = DetermineStatus(import, learning, comparison, overlays);
            var package = CreateEvidencePackage(outputFolder, parse, import, learning, comparison, overlays, report, warnings, status);
            WriteConsoleSummary(output, status, package, import, learning, comparison, warnings);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException or InvalidDataException)
        {
            error.WriteLine($"FAIL Learn-from-images failed: {ex.Message}");
            return 1;
        }
    }

    private static ParseResult Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (IsHelp(key))
                return ParseResult.Fail("Use the command form shown below.");
            if (!key.StartsWith("--", StringComparison.Ordinal))
                return ParseResult.Fail($"Unexpected argument: {key}");
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                return ParseResult.Fail($"Missing value for {key}.");

            values[key[2..]] = args[++i];
        }

        if (!values.TryGetValue("project-folder", out var projectFolder) || string.IsNullOrWhiteSpace(projectFolder))
            return ParseResult.Fail("Missing required option --project-folder.");
        if (!values.TryGetValue("output", out var outputFolder) || string.IsNullOrWhiteSpace(outputFolder))
            return ParseResult.Fail("Missing required option --output.");
        if (!values.TryGetValue("operator", out var operatorId) || string.IsNullOrWhiteSpace(operatorId))
            return ParseResult.Fail("Missing required option --operator.");

        var falseCallTarget = 0.05;
        if (values.TryGetValue("false-call-target", out var targetText) &&
            (!double.TryParse(targetText, NumberStyles.Float, CultureInfo.InvariantCulture, out falseCallTarget) ||
             falseCallTarget < 0.0 ||
             falseCallTarget > 1.0))
        {
            return ParseResult.Fail("Invalid --false-call-target. Use a number from 0.0 to 1.0.");
        }

        values.TryGetValue("board-model", out var boardModel);
        return new ParseResult(
            true,
            string.Empty,
            projectFolder,
            outputFolder,
            operatorId,
            falseCallTarget,
            boardModel ?? string.Empty);
    }

    private static ImageOnlyPcbLearningOptions CreateLearningOptions(double falseCallTarget)
        => new()
        {
            InputWidth = 256,
            InputHeight = 256,
            SafeFallbackWidth = 256,
            SafeFallbackHeight = 256,
            AlignmentSearchRadiusPixels = 12,
            MinimumAnomalyAreaPixels = 20,
            MinimumTolerance = 3.0,
            DefaultLearnedThreshold = 5.0,
            FalseCallTarget = falseCallTarget,
            MaxAllowedPossibleEscapeRate = 0.0,
        };

    private static void ValidateImportedProject(ImageLearningFolderImportResult import)
    {
        var counts = import.CountsByRole;
        var validation = ImageLearningProjectService.ValidateMinimumProjectRequirements(import.Project.ProjectId);
        if (!validation.CanTrain)
        {
            throw new InvalidOperationException(
                "Missing Golden / Reference or OK Learning images. Add at least one PNG/JPG/JPEG image to golden/ or at least 5 OK Learning images to ok_learning/. " +
                BuildWarningSuffix(import.Warnings));
        }

        if (counts.GetValueOrDefault(ImageLearningImageRole.OkValidation) == 0)
        {
            throw new InvalidOperationException(
                "Missing OK Validation images. Add at least one PNG/JPG/JPEG image to ok_validation/ so false calls can be calibrated. " +
                BuildWarningSuffix(import.Warnings));
        }

        if (counts.GetValueOrDefault(ImageLearningImageRole.Inspection) == 0)
        {
            throw new InvalidOperationException(
                "Missing Inspection images. Add at least one PNG/JPG/JPEG image to inspection/ so samples can be inspected after learning. " +
                BuildWarningSuffix(import.Warnings));
        }
    }

    private static LearnFromImagesPackageResult CreateEvidencePackage(
        string outputFolder,
        ParseResult parse,
        ImageLearningFolderImportResult import,
        ImageOnlyPcbLearningResult learning,
        ImageLearningFalseCallComparisonResult comparison,
        ImageLearningOverlayExportResult overlays,
        ImageOnlyLearningReportResult report,
        IReadOnlyList<string> warnings,
        string status)
    {
        CopyDirectoryContents(report.OutputFolder, outputFolder);
        CopyFile(comparison.HtmlReportPath, Path.Combine(outputFolder, "before_after_false_call_report.html"));
        CopyFile(comparison.JsonReportPath, Path.Combine(outputFolder, "before_after_false_call_report.json"));
        CopyFile(comparison.ResultsCsvPath, Path.Combine(outputFolder, "before_after_results.csv"));
        CopyFile(comparison.ThresholdSweepCsvPath, Path.Combine(outputFolder, "threshold_sweep.csv"));
        CopyDirectoryContents(overlays.OutputFolder, Path.Combine(outputFolder, "visual_evidence"));

        var modelArtifactsFolder = Path.Combine(outputFolder, "model_artifacts");
        CopyDirectoryContents(learning.ArtifactFolder, modelArtifactsFolder);
        CopyFile(FindArtifactPath(learning.Model, "learned_reference.png"), Path.Combine(outputFolder, "learned_reference.png"));
        CopyFile(FindArtifactPath(learning.Model, "tolerance_map.png"), Path.Combine(outputFolder, "learned_tolerance_map.png"));

        var inspectionResultsPath = Path.Combine(outputFolder, "inspection_results.csv");
        File.WriteAllText(
            inspectionResultsPath,
            BuildInspectionResultsCsv(import.Project.ProjectId, learning.Model.ModelId),
            Encoding.UTF8);

        var summaryPath = Path.Combine(outputFolder, "learn_from_images_summary.json");
        File.WriteAllText(
            summaryPath,
            JsonSerializer.Serialize(BuildSummaryPayload(parse, import, learning, comparison, report, overlays, warnings, status), JsonOptions),
            Encoding.UTF8);

        var readmePath = Path.Combine(outputFolder, "README_LEARN_FROM_IMAGES.txt");
        File.WriteAllText(
            readmePath,
            BuildReadme(parse, import, learning, comparison, warnings, status),
            Encoding.UTF8);
        var manifestPath = Path.Combine(outputFolder, "package_manifest.json");
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(BuildPackageManifest(outputFolder, import, learning, comparison, report, overlays, warnings, status), JsonOptions),
            Encoding.UTF8);

        var exportStatus = status == "PASS" ? "OK" : "WARN";
        var verified = ExportVerificationService.RecordVerifiedExport(
            "LearnFromImagesEvidencePackage",
            outputFolder,
            exportStatus,
            parse.OperatorId);
        AoiDatabase.RecordAuditEvent(
            "IMAGE_LEARNING_LEARN_FROM_IMAGES_EXPORT",
            $"Learn-from-images evidence exported: project={import.Project.ProjectId}; model={learning.Model.ModelId}; status={verified.ExportStatus}.",
            operatorWithRole: parse.OperatorId,
            relatedEntityType: "ImageLearningProject",
            relatedEntityId: import.Project.ProjectId,
            relatedPath: outputFolder);

        return new LearnFromImagesPackageResult(
            outputFolder,
            Path.Combine(outputFolder, "visual_learning_report.html"),
            summaryPath,
            readmePath,
            inspectionResultsPath,
            verified.ExportStatus);
    }

    private static IReadOnlyList<string> BuildWarnings(
        ImageLearningFolderImportResult import,
        ImageOnlyPcbLearningResult learning,
        ImageLearningFalseCallComparisonResult comparison,
        ImageLearningOverlayExportResult overlays,
        ImageOnlyLearningReportResult report)
    {
        var warnings = new List<string>();
        warnings.AddRange(import.Warnings);
        warnings.AddRange(learning.Warnings);
        warnings.AddRange(comparison.Warnings);
        warnings.AddRange(overlays.Warnings);
        warnings.AddRange(report.Warnings.Where(IsOperationalReportWarning));
        if (import.CountsByRole.GetValueOrDefault(ImageLearningImageRole.NgValidation) == 0)
            warnings.Add("No NG Validation images were provided; possible escapes cannot yet be fully proven.");
        if (comparison.Metrics.PossibleEscapes > 0)
            warnings.Add($"Possible escapes reported: {comparison.Metrics.PossibleEscapes}; review NG Validation examples before relying on this threshold.");

        return warnings
            .Where(warning => !string.IsNullOrWhiteSpace(warning))
            .Select(warning => warning.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(warning => warning, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string DetermineStatus(
        ImageLearningFolderImportResult import,
        ImageOnlyPcbLearningResult learning,
        ImageLearningFalseCallComparisonResult comparison,
        ImageLearningOverlayExportResult overlays)
    {
        if (import.Warnings.Any(IsOperationalWarning) ||
            learning.Warnings.Any(IsOperationalWarning) ||
            comparison.Warnings.Any(IsOperationalWarning) ||
            overlays.Warnings.Any(IsOperationalWarning) ||
            comparison.Metrics.RecommendationStatus == "NO_SAFE_THRESHOLD" ||
            comparison.Metrics.PossibleEscapes > 0 ||
            import.CountsByRole.GetValueOrDefault(ImageLearningImageRole.NgValidation) == 0)
        {
            return "WARN";
        }

        return "PASS";
    }

    private static void WriteConsoleSummary(
        TextWriter output,
        string status,
        LearnFromImagesPackageResult package,
        ImageLearningFolderImportResult import,
        ImageOnlyPcbLearningResult learning,
        ImageLearningFalseCallComparisonResult comparison,
        IReadOnlyList<string> warnings)
    {
        var metrics = comparison.Metrics;
        output.WriteLine($"{status} Image-only learning from customer folders complete.");
        output.WriteLine($"projectId: {import.Project.ProjectId}");
        output.WriteLine($"board model: {import.Project.BoardModel}");
        output.WriteLine($"modelId: {learning.Model.ModelId}");
        output.WriteLine($"images learned: {learning.Model.GoldenCount + learning.Model.OkLearningCount}");
        output.WriteLine($"OK validation images: {metrics.OkValidationCount}");
        output.WriteLine($"false-call rate: {metrics.FalseCallRateAfterLearning:P1} after learning ({metrics.FalseCallRateBeforeLearning:P1} before learning)");
        output.WriteLine(metrics.PossibleEscapeRate.HasValue
            ? $"possible escape status: {metrics.PossibleEscapes} possible escape(s), {metrics.PossibleEscapeRate.Value:P1}"
            : "possible escape status: NG Validation not provided; missed-defect rate cannot yet be proven");
        output.WriteLine($"recommended threshold: {(metrics.RecommendedThreshold.HasValue ? metrics.RecommendedThreshold.Value.ToString("F2", CultureInfo.InvariantCulture) : "No safe threshold")}");
        output.WriteLine($"output report: {package.ReportPath}");
        output.WriteLine($"output folder: {package.OutputFolder}");
        output.WriteLine($"export verification: {package.ExportVerificationStatus}");
        output.WriteLine("manual labels required: none");
        output.WriteLine("camera hardware required: none");

        if (warnings.Count == 0)
        {
            output.WriteLine("warnings: none");
            return;
        }

        output.WriteLine("warnings:");
        foreach (var warning in warnings)
            output.WriteLine($"  - {warning}");
    }

    private static string BuildInspectionResultsCsv(string projectId, string modelId)
    {
        var images = ImageLearningProjectService.GetProjectImages(projectId)
            .ToDictionary(image => image.Id);
        var builder = new StringBuilder();
        builder.AppendLine("resultId,projectImageId,fileName,role,verdict,anomalyScore,regionCount,operatorId,imagePath,decisionReason");
        foreach (var result in AoiDatabase.GetImageLearningInspectionResults(projectId, modelId, limit: 10000)
                     .OrderBy(row => row.CreatedAtUtc)
                     .ThenBy(row => row.Id))
        {
            images.TryGetValue(result.ProjectImageId, out var image);
            builder.Append(Csv(result.ResultId)).Append(',')
                .Append(result.ProjectImageId.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(Csv(image?.FileName ?? Path.GetFileName(result.ImagePath))).Append(',')
                .Append(image?.Role.ToString() ?? ImageLearningImageRole.Inspection.ToString()).Append(',')
                .Append(Csv(result.Verdict)).Append(',')
                .Append(result.AnomalyScore.ToString("G17", CultureInfo.InvariantCulture)).Append(',')
                .Append(result.AnomalyRegions.Count.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(Csv(result.OperatorId)).Append(',')
                .Append(Csv(result.ImagePath)).Append(',')
                .Append(Csv(result.DecisionReason)).AppendLine();
        }

        return builder.ToString();
    }

    private static object BuildSummaryPayload(
        ParseResult parse,
        ImageLearningFolderImportResult import,
        ImageOnlyPcbLearningResult learning,
        ImageLearningFalseCallComparisonResult comparison,
        ImageOnlyLearningReportResult report,
        ImageLearningOverlayExportResult overlays,
        IReadOnlyList<string> warnings,
        string status)
        => new
        {
            schemaVersion = "learn-from-images.v1",
            generatedAtUtc = DateTime.UtcNow,
            status,
            command = "learn-from-images",
            project = new
            {
                import.Project.ProjectId,
                import.Project.ProjectName,
                import.Project.BoardModel,
                import.Project.EvidenceMode,
            },
            input = new
            {
                projectFolder = Path.GetFullPath(parse.ProjectFolder),
                falseCallTarget = parse.FalseCallTarget,
                boardModel = parse.BoardModel,
            },
            countsByRole = import.CountsByRole,
            model = new
            {
                learning.Model.ModelId,
                learning.Model.ModelVersion,
                learning.Model.GoldenCount,
                learning.Model.OkLearningCount,
                learning.Model.OkValidationCount,
                learning.Model.LearnedThreshold,
                learning.Model.FalseCallTarget,
                learning.Model.FalseCallRate,
                learning.Model.PossibleEscapeRate,
            },
            metrics = comparison.Metrics,
            outputs = new
            {
                visualLearningReport = report.HtmlPath,
                visualEvidenceManifest = overlays.ManifestPath,
                falseCallComparison = comparison.HtmlReportPath,
            },
            imageOnlyBoundary = new[]
            {
                "No defect labels, bounding boxes, per-defect variables, model files, or camera hardware were required.",
                "Image-only Stage 1 learning.",
                "Not live camera validation.",
                "Formal acceptance requires customer/evaluator dataset.",
            },
            warnings,
        };

    private static object BuildPackageManifest(
        string outputFolder,
        ImageLearningFolderImportResult import,
        ImageOnlyPcbLearningResult learning,
        ImageLearningFalseCallComparisonResult comparison,
        ImageOnlyLearningReportResult report,
        ImageLearningOverlayExportResult overlays,
        IReadOnlyList<string> warnings,
        string status)
    {
        var manifestPath = Path.Combine(outputFolder, "package_manifest.json");
        var includedFiles = Directory
            .EnumerateFiles(outputFolder, "*", SearchOption.AllDirectories)
            .Where(path => !path.Equals(manifestPath, StringComparison.OrdinalIgnoreCase))
            .Select(path => new
            {
                relativePath = Path.GetRelativePath(outputFolder, path).Replace('\\', '/'),
                sizeBytes = new FileInfo(path).Length,
            })
            .OrderBy(item => item.relativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new
        {
            schemaVersion = "learn-from-images-package.v1",
            packageId = $"LFI-{DateTime.UtcNow:yyyyMMddHHmmssfff}",
            generatedAtUtc = DateTime.UtcNow,
            status,
            includedFiles,
            project = new
            {
                import.Project.ProjectId,
                import.Project.ProjectName,
                import.Project.BoardModel,
                import.Project.EvidenceMode,
            },
            model = new
            {
                learning.Model.ModelId,
                learning.Model.ModelVersion,
                learning.Model.LearnedThreshold,
                learning.Model.FalseCallTarget,
            },
            metrics = comparison.Metrics,
            outputs = new
            {
                report = report.HtmlPath,
                visualEvidenceManifest = overlays.ManifestPath,
                falseCallComparison = comparison.HtmlReportPath,
            },
            imageOnlyBoundary = new[]
            {
                "No defect labels, bounding boxes, per-defect variables, model files, or camera hardware were required.",
                "Image-only Stage 1 learning.",
                "Not live camera validation.",
                "Formal acceptance requires customer/evaluator dataset.",
            },
            warnings,
        };
    }

    private static string BuildReadme(
        ParseResult parse,
        ImageLearningFolderImportResult import,
        ImageOnlyPcbLearningResult learning,
        ImageLearningFalseCallComparisonResult comparison,
        IReadOnlyList<string> warnings,
        string status)
    {
        var metrics = comparison.Metrics;
        var builder = new StringBuilder();
        builder.AppendLine("AOI Monitor learn-from-images evidence package");
        builder.AppendLine();
        builder.AppendLine($"Status: {status}");
        builder.AppendLine($"Project ID: {import.Project.ProjectId}");
        builder.AppendLine($"Board model: {import.Project.BoardModel}");
        builder.AppendLine($"Source folder: {Path.GetFullPath(parse.ProjectFolder)}");
        builder.AppendLine($"Model ID: {learning.Model.ModelId}");
        builder.AppendLine($"Images learned: {learning.Model.GoldenCount + learning.Model.OkLearningCount}");
        builder.AppendLine($"OK validation images: {metrics.OkValidationCount}");
        builder.AppendLine($"False-call rate before learning: {metrics.FalseCallRateBeforeLearning:P1}");
        builder.AppendLine($"False-call rate after learning: {metrics.FalseCallRateAfterLearning:P1}");
        builder.AppendLine($"Possible escapes: {metrics.PossibleEscapes}");
        builder.AppendLine($"Recommended threshold: {(metrics.RecommendedThreshold.HasValue ? metrics.RecommendedThreshold.Value.ToString("F2", CultureInfo.InvariantCulture) : "No safe threshold")}");
        builder.AppendLine();
        builder.AppendLine("Open visual_learning_report.html first.");
        builder.AppendLine();
        builder.AppendLine("Image-only boundary:");
        builder.AppendLine("- No defect labels were required.");
        builder.AppendLine("- No bounding boxes were required.");
        builder.AppendLine("- No per-defect variables were required.");
        builder.AppendLine("- No model files or camera hardware were required.");
        builder.AppendLine("- Image-only Stage 1 learning is not live camera validation.");
        builder.AppendLine("- Formal acceptance requires customer/evaluator dataset.");
        if (warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Warnings:");
            foreach (var warning in warnings)
                builder.AppendLine($"- {warning}");
        }

        return builder.ToString();
    }

    private static void CopyDirectoryContents(string sourceFolder, string destinationFolder)
    {
        if (string.IsNullOrWhiteSpace(sourceFolder) || !Directory.Exists(sourceFolder))
            return;

        foreach (var sourcePath in Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceFolder, sourcePath);
            CopyFile(sourcePath, Path.Combine(destinationFolder, relative));
        }
    }

    private static void CopyFile(string sourcePath, string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? ".");
        if (!Path.GetFullPath(sourcePath).Equals(Path.GetFullPath(destinationPath), StringComparison.OrdinalIgnoreCase))
            File.Copy(sourcePath, destinationPath, overwrite: true);
    }

    private static string FindArtifactPath(LearnedPcbVisualModel model, string artifactName)
    {
        var path = model.Artifacts.FirstOrDefault(
            artifact => string.Equals(artifact.ArtifactName, artifactName, StringComparison.OrdinalIgnoreCase))?.ArtifactPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidOperationException($"Required learned-model artifact is missing: {artifactName}.");

        return path;
    }

    private static string BuildProjectName(string projectFolder)
    {
        var folderName = Directory.Exists(projectFolder)
            ? new DirectoryInfo(Path.GetFullPath(projectFolder)).Name
            : "customer-images";
        return $"Learn from images - {folderName}";
    }

    private static bool IsOperationalWarning(string warning)
        => !IsBoundaryOnlyWarning(warning);

    private static bool IsOperationalReportWarning(string warning)
        => !IsBoundaryOnlyWarning(warning);

    private static bool IsBoundaryOnlyWarning(string warning)
        => warning.Contains("Stage 2 live camera validation remains separate", StringComparison.OrdinalIgnoreCase) ||
           warning.Contains("Formal acceptance requires", StringComparison.OrdinalIgnoreCase) ||
           warning.Contains("customer/evaluator dataset", StringComparison.OrdinalIgnoreCase);

    private static string BuildWarningSuffix(IReadOnlyList<string> warnings)
    {
        var relevant = warnings
            .Where(IsOperationalWarning)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return relevant.Length == 0
            ? string.Empty
            : "Import warnings: " + string.Join(" ", relevant);
    }

    private static string Csv(string value)
    {
        var text = value ?? string.Empty;
        if (!text.Contains('"') && !text.Contains(',') && !text.Contains('\n') && !text.Contains('\r'))
            return text;

        return $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static bool IsHelp(string value)
        => string.Equals(value, "-h", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "--help", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "help", StringComparison.OrdinalIgnoreCase);

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("Usage:");
        writer.WriteLine("  AOI_Monitor.Tools learn-from-images --project-folder <folder> --output <folder> --operator <id> [--false-call-target 0.05] [--board-model <name>]");
        writer.WriteLine("Folder convention:");
        writer.WriteLine("  project_folder/golden");
        writer.WriteLine("  project_folder/ok_learning");
        writer.WriteLine("  project_folder/ok_validation");
        writer.WriteLine("  project_folder/inspection");
        writer.WriteLine("  project_folder/ng_validation optional");
        writer.WriteLine("  project_folder/image_truth.csv optional, image-level only");
    }

    private sealed record ParseResult(
        bool Success,
        string Message,
        string ProjectFolder,
        string OutputFolder,
        string OperatorId,
        double FalseCallTarget,
        string BoardModel)
    {
        public static ParseResult Fail(string message)
            => new(false, message, string.Empty, string.Empty, string.Empty, 0.05, string.Empty);
    }

    private sealed record LearnFromImagesPackageResult(
        string OutputFolder,
        string ReportPath,
        string SummaryPath,
        string ReadmePath,
        string InspectionResultsPath,
        string ExportVerificationStatus);
}
