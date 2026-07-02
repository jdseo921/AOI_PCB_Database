using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;

namespace AOI_Monitor.Tools;

public static class ClientImageLearningDemoCommand
{
    public const string SyntheticDemoNotice = "Synthetic demo only. This proves workflow capability, not customer acceptance.";

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

        if (!string.Equals(args[0], "client-image-learning-demo", StringComparison.OrdinalIgnoreCase))
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

            var sourceFolder = parse.Synthetic
                ? GenerateSyntheticDataset(Path.Combine(outputFolder, "example_images", "synthetic_source_dataset"))
                : ValidateRealProjectFolder(parse.ProjectFolder);
            var evidenceMode = parse.Synthetic
                ? ImageLearningEvidenceMode.SyntheticDemo
                : ImageLearningEvidenceMode.CustomerData;
            var options = CreateLearningOptions(parse.FalseCallTarget);

            var imported = ImageLearningFolderImportService.ImportProjectFolder(
                sourceFolder,
                parse.OperatorId,
                evidenceMode,
                parse.Synthetic ? "Synthetic image-only PCB learning demo" : string.Empty,
                parse.Synthetic ? "SYNTHETIC-PCB-DEMO" : string.Empty);
            var validation = ImageLearningProjectService.ValidateMinimumProjectRequirements(imported.Project.ProjectId);
            if (!validation.CanTrain)
                throw new InvalidOperationException(string.Join(" ", validation.BlockingIssues));

            var learning = ImageOnlyPcbLearningService.TrainProject(imported.Project.ProjectId, options, parse.OperatorId);
            var inspectionImages = ImageLearningProjectService.GetProjectImages(imported.Project.ProjectId, ImageLearningImageRole.Inspection);
            foreach (var image in inspectionImages)
                ImageOnlyPcbLearningService.InspectProjectImage(learning.Model.ModelId, image, options, parse.OperatorId);

            var comparison = ImageLearningFalseCallComparisonService.RunComparison(
                imported.Project.ProjectId,
                learning.Model.ModelId,
                options,
                parse.OperatorId);
            var overlays = ImageLearningOverlayExportService.ExportProjectVisualEvidence(
                imported.Project.ProjectId,
                learning.Model.ModelId,
                new ImageLearningOverlayExportOptions
                {
                    LearningOptions = options,
                    MaxBatchExamples = 10,
                },
                parse.OperatorId);
            var report = ImageOnlyLearningReportService.ExportReport(
                imported.Project.ProjectId,
                learning.Model.ModelId,
                new ImageOnlyLearningReportOptions
                {
                    GeneratePdf = true,
                    LearningOptions = options,
                },
                parse.OperatorId);

            var package = CreateEvidencePackage(
                outputFolder,
                parse,
                imported,
                learning,
                comparison,
                overlays,
                report);

            WriteConsoleSummary(output, parse, package, imported, learning, comparison);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            error.WriteLine($"FAIL Client image-only learning demo failed: {ex.Message}");
            return 1;
        }
    }

    private static ParseResult Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var synthetic = false;
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (IsHelp(key))
                return ParseResult.Fail("Use the command form shown below.");
            if (!key.StartsWith("--", StringComparison.Ordinal))
                return ParseResult.Fail($"Unexpected argument: {key}");
            if (key.Equals("--synthetic", StringComparison.OrdinalIgnoreCase))
            {
                synthetic = true;
                continue;
            }

            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                return ParseResult.Fail($"Missing value for {key}.");

            values[key[2..]] = args[++i];
        }

        if (!values.TryGetValue("output", out var outputFolder) || string.IsNullOrWhiteSpace(outputFolder))
            return ParseResult.Fail("Missing required option --output.");
        if (!values.TryGetValue("operator", out var operatorId) || string.IsNullOrWhiteSpace(operatorId))
            return ParseResult.Fail("Missing required option --operator.");
        values.TryGetValue("project-folder", out var projectFolder);
        if (!synthetic && string.IsNullOrWhiteSpace(projectFolder))
            synthetic = true;
        if (!synthetic && string.IsNullOrWhiteSpace(projectFolder))
            return ParseResult.Fail("Missing --project-folder for real project-folder mode.");
        if (synthetic && !string.IsNullOrWhiteSpace(projectFolder))
            return ParseResult.Fail("Use either --synthetic or --project-folder, not both.");

        var falseCallTarget = 0.05;
        if (values.TryGetValue("false-call-target", out var targetText) &&
            (!double.TryParse(targetText, NumberStyles.Float, CultureInfo.InvariantCulture, out falseCallTarget) ||
             falseCallTarget < 0 ||
             falseCallTarget > 1))
        {
            return ParseResult.Fail("Invalid --false-call-target. Use a number from 0.0 to 1.0.");
        }

        return new ParseResult(
            true,
            string.Empty,
            outputFolder,
            operatorId,
            synthetic,
            projectFolder ?? string.Empty,
            falseCallTarget);
    }

    private static ImageOnlyPcbLearningOptions CreateLearningOptions(double falseCallTarget)
        => new()
        {
            InputWidth = 64,
            InputHeight = 64,
            SafeFallbackWidth = 64,
            SafeFallbackHeight = 64,
            AlignmentSearchRadiusPixels = 8,
            MinimumAnomalyAreaPixels = 16,
            MinimumTolerance = 3.0,
            DefaultLearnedThreshold = 4.0,
            FalseCallTarget = falseCallTarget,
            MaxAllowedPossibleEscapeRate = 0.0,
        };

    private static string ValidateRealProjectFolder(string projectFolder)
    {
        if (string.IsNullOrWhiteSpace(projectFolder))
            throw new ArgumentException("Real project-folder mode requires --project-folder.");

        var root = Path.GetFullPath(projectFolder);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Image learning project folder was not found: {root}");

        var required = new[]
        {
            ImageLearningImageRole.GoldenReference,
            ImageLearningImageRole.OkLearning,
            ImageLearningImageRole.OkValidation,
            ImageLearningImageRole.Inspection,
        };
        var missing = required
            .Select(role => ImageLearningFolderImportService.ConventionFolders[role])
            .Where(folder => !Directory.Exists(Path.Combine(root, folder)))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                "Real project-folder mode requires folder convention image groups. Missing required folder(s): " +
                string.Join(", ", missing) +
                ".");
        }

        return root;
    }

    private static string GenerateSyntheticDataset(string root)
    {
        Directory.CreateDirectory(root);
        foreach (var folder in ImageLearningFolderImportService.ConventionFolders.Values)
            Directory.CreateDirectory(Path.Combine(root, folder));

        WriteSyntheticPng(Path.Combine(root, "golden", "golden_reference.png"));

        WriteSyntheticPng(Path.Combine(root, "ok_learning", "ok_learning_shift_left.png"), offsetX: -1, variant: 1);
        WriteSyntheticPng(Path.Combine(root, "ok_learning", "ok_learning_shift_right.png"), offsetX: 1, variant: 2);
        WriteSyntheticPng(Path.Combine(root, "ok_learning", "ok_learning_bright.png"), brightnessShift: 5, variant: 3);
        WriteSyntheticPng(Path.Combine(root, "ok_learning", "ok_learning_dim.png"), brightnessShift: -5, variant: 4);
        WriteSyntheticPng(Path.Combine(root, "ok_learning", "ok_learning_nominal.png"), variant: 5);

        WriteSyntheticPng(Path.Combine(root, "ok_validation", "ok_validation_lighting_shift.png"), brightnessShift: 30, offsetX: 1, variant: 11);
        WriteSyntheticPng(Path.Combine(root, "ok_validation", "ok_validation_dim_shift.png"), brightnessShift: -24, offsetY: -1, variant: 12);
        WriteSyntheticPng(Path.Combine(root, "ok_validation", "ok_validation_position_shift.png"), offsetX: -1, offsetY: 1, variant: 13);

        WriteSyntheticPng(Path.Combine(root, "inspection", "inspection_bridge_anomaly.png"), anomalyKind: SyntheticAnomalyKind.Bridge, variant: 21);
        WriteSyntheticPng(Path.Combine(root, "inspection", "inspection_missing_pad_anomaly.png"), anomalyKind: SyntheticAnomalyKind.MissingPad, variant: 22);
        WriteSyntheticPng(Path.Combine(root, "inspection", "inspection_ok_sample.png"), brightnessShift: 10, variant: 23);

        WriteSyntheticPng(Path.Combine(root, "ng_validation", "ng_validation_bridge.png"), anomalyKind: SyntheticAnomalyKind.Bridge, variant: 31);
        File.WriteAllText(
            Path.Combine(root, "image_truth.csv"),
            "image,truth,notes" + Environment.NewLine +
            "ok_validation/ok_validation_lighting_shift.png,OK,synthetic harmless lighting variation" + Environment.NewLine +
            "ok_validation/ok_validation_dim_shift.png,OK,synthetic harmless dim variation" + Environment.NewLine +
            "ok_validation/ok_validation_position_shift.png,OK,synthetic harmless position variation" + Environment.NewLine +
            "ng_validation/ng_validation_bridge.png,NG,synthetic known-bad bridge example",
            Encoding.UTF8);

        return root;
    }

    private static EvidencePackageResult CreateEvidencePackage(
        string outputFolder,
        ParseResult parse,
        ImageLearningFolderImportResult imported,
        ImageOnlyPcbLearningResult learning,
        ImageLearningFalseCallComparisonResult comparison,
        ImageLearningOverlayExportResult overlays,
        ImageOnlyLearningReportResult report)
    {
        CopyDirectoryContents(report.OutputFolder, outputFolder);
        CopyFile(comparison.HtmlReportPath, Path.Combine(outputFolder, "before_after_false_call_report.html"));
        CopyFile(comparison.ResultsCsvPath, Path.Combine(outputFolder, "before_after_results.csv"));
        CopyFile(comparison.ThresholdSweepCsvPath, Path.Combine(outputFolder, "threshold_sweep.csv"));
        CopyFile(FindArtifactPath(learning.Model, "learned_reference.png"), Path.Combine(outputFolder, "learned_reference.png"));
        CopyFile(FindArtifactPath(learning.Model, "tolerance_map.png"), Path.Combine(outputFolder, "learned_tolerance_map.png"));

        CopyOverlayFiles(overlays, Path.Combine(outputFolder, "annotated_overlays"), item => item.AnnotatedOverlayPath);
        CopyOverlayFiles(overlays, Path.Combine(outputFolder, "heatmaps"), item => item.HeatmapPath);
        CopyExampleImages(imported.Project.ProjectId, Path.Combine(outputFolder, "example_images"));

        var inspectionResultsPath = Path.Combine(outputFolder, "inspection_results.csv");
        File.WriteAllText(
            inspectionResultsPath,
            BuildInspectionResultsCsv(imported.Project.ProjectId, learning.Model.ModelId),
            Encoding.UTF8);
        var readmePath = Path.Combine(outputFolder, "README_CLIENT_IMAGE_LEARNING_DEMO.txt");
        File.WriteAllText(
            readmePath,
            BuildReadme(parse, imported, learning, comparison, report),
            Encoding.UTF8);
        var manifestPath = Path.Combine(outputFolder, "package_manifest.json");
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(BuildPackageManifest(outputFolder, imported, learning, comparison, report), JsonOptions),
            Encoding.UTF8);

        var exportStatus = report.Warnings.Count == 0 && overlays.Warnings.Count == 0 && comparison.Warnings.Count == 0 ? "OK" : "WARN";
        var verified = ExportVerificationService.RecordVerifiedExport(
            "ClientImageLearningDemoEvidencePackage",
            outputFolder,
            exportStatus,
            parse.OperatorId);
        AoiDatabase.RecordAuditEvent(
            "CLIENT_IMAGE_LEARNING_DEMO_EXPORT",
            $"Client image-only learning demo exported: project={imported.Project.ProjectId}; model={learning.Model.ModelId}; status={verified.ExportStatus}.",
            operatorWithRole: parse.OperatorId,
            relatedEntityType: "ImageLearningProject",
            relatedEntityId: imported.Project.ProjectId,
            relatedPath: outputFolder);

        return new EvidencePackageResult(
            outputFolder,
            Path.Combine(outputFolder, "visual_learning_report.html"),
            readmePath,
            inspectionResultsPath,
            manifestPath,
            verified.ExportStatus);
    }

    private static void WriteConsoleSummary(
        TextWriter output,
        ParseResult parse,
        EvidencePackageResult package,
        ImageLearningFolderImportResult imported,
        ImageOnlyPcbLearningResult learning,
        ImageLearningFalseCallComparisonResult comparison)
    {
        if (parse.Synthetic)
            output.WriteLine(SyntheticDemoNotice);

        var metrics = comparison.Metrics;
        output.WriteLine("OK Client image-only learning demo evidence package complete.");
        output.WriteLine($"projectId: {imported.Project.ProjectId}");
        output.WriteLine($"images learned from: {learning.Model.GoldenCount + learning.Model.OkLearningCount}");
        output.WriteLine($"OK validation count: {metrics.OkValidationCount}");
        output.WriteLine($"false calls before learning: {metrics.FalseCallsBeforeLearning}");
        output.WriteLine($"false calls after learning: {metrics.FalseCallsAfterLearning}");
        output.WriteLine($"false-call reduction: {metrics.FalseCallReductionPercentage:P1}");
        output.WriteLine(metrics.PossibleEscapeRate.HasValue
            ? $"possible escapes: {metrics.PossibleEscapes} ({metrics.PossibleEscapeRate.Value:P1})"
            : $"possible escapes: {metrics.PossibleEscapes} (NG validation not provided; missed-defect rate cannot yet be proven)");
        output.WriteLine($"recommended threshold: {(metrics.RecommendedThreshold.HasValue ? metrics.RecommendedThreshold.Value.ToString("F2", CultureInfo.InvariantCulture) : "No safe threshold")}");
        output.WriteLine($"report path: {package.ReportPath}");
        output.WriteLine($"evidence folder: {package.OutputFolder}");
        output.WriteLine($"export verification: {package.ExportVerificationStatus}");
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

    private static string BuildReadme(
        ParseResult parse,
        ImageLearningFolderImportResult imported,
        ImageOnlyPcbLearningResult learning,
        ImageLearningFalseCallComparisonResult comparison,
        ImageOnlyLearningReportResult report)
    {
        var metrics = comparison.Metrics;
        var builder = new StringBuilder();
        builder.AppendLine("AOI Monitor Client Image-only PCB Learning Demo");
        builder.AppendLine();
        if (parse.Synthetic)
        {
            builder.AppendLine(SyntheticDemoNotice);
            builder.AppendLine();
        }

        builder.AppendLine("What this package shows:");
        builder.AppendLine("- The program learned normal PCB appearance from uploaded image groups.");
        builder.AppendLine("- The program calibrated tolerance using OK Validation images.");
        builder.AppendLine("- The package compares false calls before learning and after learning.");
        builder.AppendLine("- No manual defect labels, defect-class variables, or bounding boxes were required for learning.");
        builder.AppendLine();
        builder.AppendLine("Evidence boundary:");
        builder.AppendLine("- Image-only Stage 1 learning.");
        builder.AppendLine("- Not live camera validation.");
        builder.AppendLine("- Formal acceptance requires customer/evaluator dataset.");
        builder.AppendLine("- Stage 2 live camera validation remains separate.");
        builder.AppendLine();
        builder.AppendLine($"Project ID: {imported.Project.ProjectId}");
        builder.AppendLine($"Model ID: {learning.Model.ModelId}");
        builder.AppendLine($"Images learned from: {learning.Model.GoldenCount + learning.Model.OkLearningCount}");
        builder.AppendLine($"OK validation count: {metrics.OkValidationCount}");
        builder.AppendLine($"False calls before learning: {metrics.FalseCallsBeforeLearning}");
        builder.AppendLine($"False calls after learning: {metrics.FalseCallsAfterLearning}");
        builder.AppendLine($"False-call reduction: {metrics.FalseCallReductionPercentage:P1}");
        builder.AppendLine($"Possible escapes: {metrics.PossibleEscapes}");
        builder.AppendLine($"Recommended threshold: {(metrics.RecommendedThreshold.HasValue ? metrics.RecommendedThreshold.Value.ToString("F2", CultureInfo.InvariantCulture) : "No safe threshold")}");
        builder.AppendLine();
        builder.AppendLine("Open visual_learning_report.html first for the client-facing summary.");
        if (report.Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Warnings and limitations:");
            foreach (var warning in report.Warnings)
                builder.AppendLine($"- {warning}");
        }

        return builder.ToString();
    }

    private static object BuildPackageManifest(
        string outputFolder,
        ImageLearningFolderImportResult imported,
        ImageOnlyPcbLearningResult learning,
        ImageLearningFalseCallComparisonResult comparison,
        ImageOnlyLearningReportResult report)
    {
        var manifestPath = Path.Combine(outputFolder, "package_manifest.json");
        var includedFiles = Directory
            .EnumerateFiles(outputFolder, "*", SearchOption.AllDirectories)
            .Where(path => !path.Equals(manifestPath, StringComparison.OrdinalIgnoreCase))
            .Select(path => new
            {
                relativePath = NormalizeRelative(Path.GetRelativePath(outputFolder, path)),
                sizeBytes = new FileInfo(path).Length,
            })
            .OrderBy(item => item.relativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new
        {
            schemaVersion = "client-image-learning-demo-package.v1",
            packageId = $"CILD-{DateTime.UtcNow:yyyyMMddHHmmssfff}",
            generatedAtUtc = DateTime.UtcNow,
            includedFiles,
            project = new
            {
                imported.Project.ProjectId,
                imported.Project.ProjectName,
                imported.Project.BoardModel,
                imported.Project.EvidenceMode,
            },
            model = new
            {
                learning.Model.ModelId,
                learning.Model.ModelVersion,
                learning.Model.LearnedThreshold,
                learning.Model.FalseCallTarget,
            },
            metrics = comparison.Metrics,
            report = new
            {
                html = NormalizeRelative(Path.GetRelativePath(outputFolder, Path.Combine(outputFolder, "visual_learning_report.html"))),
                json = NormalizeRelative(Path.GetRelativePath(outputFolder, Path.Combine(outputFolder, "visual_learning_report.json"))),
                sourceReportFolder = report.OutputFolder,
            },
            evidenceBoundary = new[]
            {
                "Image-only Stage 1 learning.",
                "Not live camera validation.",
                "Synthetic demo only when evidenceMode is SyntheticDemo.",
                "Formal acceptance requires customer/evaluator dataset.",
            },
        };
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

    private static void CopyOverlayFiles(
        ImageLearningOverlayExportResult overlays,
        string destinationFolder,
        Func<ImageLearningOverlayExportItem, string> pathSelector)
    {
        Directory.CreateDirectory(destinationFolder);
        var index = 1;
        foreach (var item in overlays.Items)
        {
            var source = pathSelector(item);
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
                continue;

            var name = $"{index++:000}_{SafePathPart(item.Category)}_{SafePathPart(Path.GetFileName(source))}";
            CopyFile(source, Path.Combine(destinationFolder, name));
        }
    }

    private static void CopyExampleImages(string projectId, string destinationFolder)
    {
        Directory.CreateDirectory(destinationFolder);
        foreach (var role in Enum.GetValues<ImageLearningImageRole>())
        {
            var roleFolder = Path.Combine(destinationFolder, ImageLearningFolderImportService.ConventionFolders[role]);
            Directory.CreateDirectory(roleFolder);
            var index = 1;
            foreach (var image in ImageLearningProjectService.GetProjectImages(projectId, role).OrderBy(row => row.FileName, StringComparer.OrdinalIgnoreCase))
            {
                var source = File.Exists(image.VaultPath) ? image.VaultPath : image.OriginalPath;
                if (!File.Exists(source))
                    continue;

                var extension = Path.GetExtension(source);
                if (string.IsNullOrWhiteSpace(extension))
                    extension = ".png";
                var target = Path.Combine(roleFolder, $"{index++:000}_{SafePathPart(Path.GetFileNameWithoutExtension(image.FileName))}{extension}");
                CopyFile(source, target);
            }
        }
    }

    private static string FindArtifactPath(LearnedPcbVisualModel model, string artifactName)
    {
        var path = model.Artifacts.FirstOrDefault(
            artifact => string.Equals(artifact.ArtifactName, artifactName, StringComparison.OrdinalIgnoreCase))?.ArtifactPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidOperationException($"Required learned-model artifact is missing: {artifactName}.");

        return path;
    }

    private static void CopyFile(string sourcePath, string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? ".");
        if (!Path.GetFullPath(sourcePath).Equals(Path.GetFullPath(destinationPath), StringComparison.OrdinalIgnoreCase))
            File.Copy(sourcePath, destinationPath, overwrite: true);
    }

    private static void WriteSyntheticPng(
        string path,
        int brightnessShift = 0,
        int offsetX = 0,
        int offsetY = 0,
        int variant = 0,
        SyntheticAnomalyKind anomalyKind = SyntheticAnomalyKind.None)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        const int width = 64;
        const int height = 64;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var patternX = x - offsetX;
                var patternY = y - offsetY;
                var (r, g, b) = SyntheticPixel(patternX, patternY, brightnessShift, variant);
                if (anomalyKind == SyntheticAnomalyKind.Bridge && x is >= 43 and <= 54 && y is >= 10 and <= 20)
                    (r, g, b) = (240, 238, 210);
                if (anomalyKind == SyntheticAnomalyKind.MissingPad && x is >= 12 and <= 22 && y is >= 40 and <= 50)
                    (r, g, b) = (18, 44, 35);

                var offset = (y * width + x) * 4;
                pixels[offset] = b;
                pixels[offset + 1] = g;
                pixels[offset + 2] = r;
                pixels[offset + 3] = 255;
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static (byte R, byte G, byte B) SyntheticPixel(int x, int y, int brightnessShift, int variant)
    {
        var r = 38 + brightnessShift;
        var g = 88 + brightnessShift;
        var b = 62 + brightnessShift;
        if (x < 0 || y < 0 || x >= 64 || y >= 64)
            return (ClampByte(r), ClampByte(g), ClampByte(b));

        if (x is >= 6 and <= 58 && y is >= 29 and <= 34)
            (r, g, b) = (178 + brightnessShift + variant % 3, 151 + brightnessShift, 75 + brightnessShift);
        if (y is >= 6 and <= 58 && x is >= 30 and <= 35)
            (r, g, b) = (166 + brightnessShift, 144 + brightnessShift - variant % 2, 70 + brightnessShift);
        if (x is >= 10 and <= 20 && y is >= 10 and <= 20)
            (r, g, b) = (138 + brightnessShift + variant % 4, 138 + brightnessShift, 130 + brightnessShift);
        if (x is >= 42 and <= 54 && y is >= 42 and <= 52)
            (r, g, b) = (145 + brightnessShift, 145 + brightnessShift - variant % 4, 136 + brightnessShift);
        if ((x + y + variant) % 17 == 0)
            (r, g, b) = (r + 10, g + 10, b + 10);

        return (ClampByte(r), ClampByte(g), ClampByte(b));
    }

    private static byte ClampByte(int value)
        => (byte)Math.Clamp(value, 0, 255);

    private static string NormalizeRelative(string path)
        => path.Replace('\\', '/').TrimStart('/');

    private static string SafePathPart(string value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "image_learning" : value.Trim();
        foreach (var ch in Path.GetInvalidFileNameChars().Concat(Path.GetInvalidPathChars()).Distinct())
            text = text.Replace(ch, '_');

        return text.Replace(' ', '_');
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
        writer.WriteLine("  AOI_Monitor.Tools client-image-learning-demo --output <folder> --operator <id> [--synthetic] [--project-folder <folder>] [--false-call-target 0.05]");
        writer.WriteLine("Folder convention for real project-folder mode:");
        writer.WriteLine("  project_folder/golden");
        writer.WriteLine("  project_folder/ok_learning");
        writer.WriteLine("  project_folder/ok_validation");
        writer.WriteLine("  project_folder/inspection");
        writer.WriteLine("  project_folder/ng_validation optional");
    }

    private enum SyntheticAnomalyKind
    {
        None,
        Bridge,
        MissingPad,
    }

    private sealed record ParseResult(
        bool Success,
        string Message,
        string OutputFolder,
        string OperatorId,
        bool Synthetic,
        string ProjectFolder,
        double FalseCallTarget)
    {
        public static ParseResult Fail(string message)
            => new(false, message, string.Empty, string.Empty, false, string.Empty, 0.05);
    }

    private sealed record EvidencePackageResult(
        string OutputFolder,
        string ReportPath,
        string ReadmePath,
        string InspectionResultsPath,
        string ManifestPath,
        string ExportVerificationStatus);
}
