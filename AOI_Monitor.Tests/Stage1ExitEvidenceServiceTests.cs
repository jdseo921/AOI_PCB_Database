using System.Windows;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using AOI_Monitor.Tools;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class Stage1ExitEvidenceServiceTests : IDisposable
{
    private readonly string _root;

    public Stage1ExitEvidenceServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_Stage1Exit_Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        AoiDatabase.ConfigureStorageRoot(Path.Combine(_root, "storage"));
        InspectionModelConfigurationService.Save(new InspectionModelConfiguration());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException ex)
        {
            System.Diagnostics.Trace.WriteLine($"Stage 1 exit evidence test cleanup skipped: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            System.Diagnostics.Trace.WriteLine($"Stage 1 exit evidence test cleanup skipped: {ex.Message}");
        }
    }

    [Fact]
    public void Stage1ExitCommandFailsClearlyWhenDatasetIsMissing()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = Stage1ExitCommand.Execute(
            new[]
            {
                "stage1-exit",
                "--dataset",
                Path.Combine(_root, "missing-dataset"),
                "--manifest",
                Path.Combine(_root, "missing.csv"),
                "--output",
                Path.Combine(_root, "out"),
                "--operator",
                "qa-operator",
            },
            stdout,
            stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains("FAIL", stderr.ToString());
        Assert.Contains("Dataset folder was not found", stderr.ToString());
    }

    [Fact]
    public void Stage1ExitWorkflowProducesPrototypeOnlyEvidenceUnderOutputFolder()
    {
        var dataset = CreateDataset(Path.Combine(_root, "customer-dataset"));
        var output = Path.Combine(_root, "stage1-output");

        var result = Stage1ExitEvidenceService.Run(
            new Stage1ExitEvidenceRequest
            {
                DatasetFolder = dataset.DatasetFolder,
                ManifestPath = dataset.ManifestPath,
                OutputFolder = output,
                OperatorId = "qa-engineer",
            },
            new LabelEchoInspectionEngine());

        Assert.True(string.Equals("WARN", result.Status, StringComparison.OrdinalIgnoreCase), Stage1ExitEvidenceService.BuildTextSummary(result));
        Assert.True(result.PrototypeOnly);
        Assert.Equal("PROTOTYPE_ONLY", result.ModelAcceptanceStatus);
        Assert.Equal("PASS", result.PreflightStatus);
        Assert.Equal(50, result.ManifestRows);
        Assert.Equal(0, result.FalseCallCount);
        Assert.Equal(0, result.PossibleEscapeCount);
        Assert.True(Directory.Exists(result.Outputs.ValidationPackageFolder));
        Assert.True(File.Exists(result.Outputs.ValidationResultsCsvPath));
        Assert.True(File.Exists(result.Outputs.ExportVerificationJsonPath));
        Assert.True(Directory.Exists(result.Outputs.FactoryReadinessFolder));
        Assert.True(File.Exists(result.Outputs.SummaryJsonPath));
        Assert.StartsWith(Path.GetFullPath(output), Path.GetFullPath(result.Outputs.ValidationPackageFolder), StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Path.GetFullPath(output), Path.GetFullPath(result.Outputs.FactoryReadinessFolder), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("prototype-only", File.ReadAllText(result.Outputs.SummaryTextPath), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Stage1ExitSimulationFlagProducesDryRunBoundary()
    {
        var dataset = CreateDataset(Path.Combine(_root, "simulation-dataset"));
        var output = Path.Combine(_root, "stage1-simulation-output");

        var result = Stage1ExitEvidenceService.Run(
            new Stage1ExitEvidenceRequest
            {
                DatasetFolder = dataset.DatasetFolder,
                ManifestPath = dataset.ManifestPath,
                OutputFolder = output,
                OperatorId = "qa-engineer",
                AllowSimulation = true,
            },
            new LabelEchoInspectionEngine());

        Assert.Equal("DRY RUN", result.Status);
        Assert.True(result.DryRun);
        Assert.Equal(Stage1ExitEvidenceService.SimulationDryRunNotice, result.EvidenceBoundary);
        Assert.Contains(Stage1ExitEvidenceService.SimulationDryRunNotice, File.ReadAllText(result.Outputs.SummaryTextPath));
    }

    private static DatasetSeed CreateDataset(string datasetFolder)
    {
        var images = Path.Combine(datasetFolder, "images");
        var golden = Path.Combine(datasetFolder, "golden");
        Directory.CreateDirectory(images);
        Directory.CreateDirectory(golden);
        WritePng(Path.Combine(golden, "golden_top.png"), "golden");

        var rows = new List<string>();
        for (var i = 0; i < 25; i++)
        {
            var extension = i == 0 ? ".jpg" : ".png";
            var image = $"images/board_ok_{i:D2}{extension}";
            WriteImage(Path.Combine(datasetFolder, image), $"ok-{i}");
            rows.Add(Row(image, "OK", "golden/golden_top.png", "OK", $"R-OK-{i:D2}", $"ROI-OK-{i:D2}"));
        }

        for (var i = 0; i < 13; i++)
        {
            var image = $"images/board_ng_bridge_{i:D2}.png";
            WriteImage(Path.Combine(datasetFolder, image), $"bridge-{i}");
            rows.Add(Row(image, "NG", "golden/golden_top.png", "Solder Bridge", $"R-BR-{i:D2}", $"ROI-BR-{i:D2}"));
        }

        for (var i = 0; i < 12; i++)
        {
            var image = $"images/board_ng_missing_{i:D2}.png";
            WriteImage(Path.Combine(datasetFolder, image), $"missing-{i}");
            rows.Add(Row(image, "NG", "golden/golden_top.png", "Missing Component", $"R-MC-{i:D2}", $"ROI-MC-{i:D2}"));
        }

        var manifest = Path.Combine(datasetFolder, "customer_validation_manifest.csv");
        File.WriteAllLines(
            manifest,
            new[] { "image,ground_truth,golden_image,defect_type,side,refdes,roi_id,roi_type,lot_id,board_model,notes" }.Concat(rows));
        return new DatasetSeed(datasetFolder, manifest);
    }

    private static string Row(string image, string truth, string golden, string defectType, string refdes, string roiId)
        => $"{image},{truth},{golden},{defectType},Top,{refdes},{roiId},Component,LOT-STAGE1,TBOX-STAGE1,synthetic generated test image";

    private static void WriteImage(string path, string suffix)
    {
        if (Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase))
            WriteJpg(path, suffix);
        else
            WritePng(path, suffix);
    }

    private static void WritePng(string path, string suffix)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, TinyPngBytes().Concat(System.Text.Encoding.UTF8.GetBytes(suffix)).ToArray());
    }

    private static void WriteJpg(string path, string suffix)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, TinyJpgBytes().Concat(System.Text.Encoding.UTF8.GetBytes(suffix)).ToArray());
    }

    private static byte[] TinyPngBytes()
        => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=");

    private static byte[] TinyJpgBytes()
        => Convert.FromBase64String("/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////2wBDAf//////////////////////////////////////////////////////////////////////////////////////wAARCAABAAEDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAX/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oADAMBAAIQAxAAAAH/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oACAEBAAEFAqf/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oACAEDAQE/AV//xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oACAECAQE/AV//xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oACAEBAAY/Al//xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oACAEBAAE/IV//2gAMAwEAAgADAAAAEP/EABQRAQAAAAAAAAAAAAAAAAAAABD/2gAIAQMBAT8QH//EABQRAQAAAAAAAAAAAAAAAAAAABD/2gAIAQIBAT8QH//EABQQAQAAAAAAAAAAAAAAAAAAABD/2gAIAQEAAT8QH//Z");

    private sealed record DatasetSeed(string DatasetFolder, string ManifestPath);

    private sealed class LabelEchoInspectionEngine : IInspectionEngine
    {
        public string Name => "Deterministic Stage 1 Test Engine";
        public string Version => "TEST-1.0";

        public AnalysisResult Analyze(string samplePath, string? goldenPath, DetectionPriority priority)
        {
            var isNg = Path.GetFileName(samplePath).Contains("_ng_", StringComparison.OrdinalIgnoreCase);
            var timing = new InspectionTiming
            {
                ImageLoadMilliseconds = 4,
                PreprocessingMilliseconds = 5,
                InferenceMilliseconds = 6,
                OverlayRenderingMilliseconds = 7,
            };
            timing.RecalculateTotal();

            var result = new AnalysisResult
            {
                SamplePath = samplePath,
                GoldenPath = goldenPath,
                InspectionEngine = Name,
                ModelVersion = Version,
                Verdict = isNg ? "NG" : "OK",
                DifferenceScore = isNg ? 0.95 : 0.02,
                SuggestedDefect = isNg ? "Solder Bridge" : "OK",
                Hotspot = new Rect(0.1, 0.1, 0.2, 0.2),
                Timing = timing,
            };

            if (isNg)
            {
                result.Defects.Add(new DefectResult
                {
                    DefectType = samplePath.Contains("missing", StringComparison.OrdinalIgnoreCase) ? "Missing Component" : "Solder Bridge",
                    Confidence = 0.95,
                    BoundingBox = new Rect(0.1, 0.1, 0.2, 0.2),
                    SideOrViewType = "Top",
                    RoiId = "ROI-TEST",
                    RoiType = "Component",
                });
            }

            return result;
        }
    }
}
