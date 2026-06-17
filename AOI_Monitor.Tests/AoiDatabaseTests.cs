using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Microsoft.Data.Sqlite;
using Microsoft.ML.OnnxRuntime.Tensors;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class AoiDatabaseTests : IDisposable
{
    private readonly string _root;

    public AoiDatabaseTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_Tests", Guid.NewGuid().ToString("N"));
        AoiDatabase.ConfigureStorageRoot(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void InitializeCreatesSqliteDatabaseAndExpectedTables()
    {
        AoiDatabase.Initialize();

        Assert.True(File.Exists(AoiDatabase.DatabasePath));
        Assert.True(Directory.Exists(AoiDatabase.ImageVaultPath));
        Assert.Equal("ok", AoiDatabase.RunIntegrityCheck(), ignoreCase: true);

        var tables = ReadTableNames();
        Assert.Contains("Images", tables);
        Assert.Contains("InspectionResults", tables);
        Assert.Contains("BatchTestRuns", tables);
        Assert.Contains("RecipeRevisions", tables);
        Assert.Contains("ExportHistory", tables);
    }

    [Fact]
    public void TryImportImageValidatesMissingUnsupportedAndInvalidFiles()
    {
        AoiDatabase.Initialize();
        var unsupported = Path.Combine(_root, "notes.txt");
        var invalidImage = Path.Combine(_root, "not-image.png");
        File.WriteAllText(unsupported, "hello");
        File.WriteAllText(invalidImage, "not a png");

        Assert.Equal("Missing", AoiDatabase.TryImportImage(Path.Combine(_root, "missing.png"), "BM", "LOT", "top").Status);
        Assert.Equal("Unsupported", AoiDatabase.TryImportImage(unsupported, "BM", "LOT", "top").Status);
        Assert.Equal("Invalid", AoiDatabase.TryImportImage(invalidImage, "BM", "LOT", "top").Status);
    }

    [Fact]
    public void TryImportImageDetectsDuplicateByHash()
    {
        AoiDatabase.Initialize();
        var source = WriteTinyPng("sample.png");

        var first = AoiDatabase.TryImportImage(source, "TBOX", "LOT-1", "top");
        var second = AoiDatabase.TryImportImage(source, "TBOX", "LOT-1", "top");

        Assert.True(first.Imported);
        Assert.Equal("Imported", first.Status);
        Assert.False(second.Imported);
        Assert.Equal("Duplicate", second.Status);
        Assert.Equal(first.Image?.FileHash, second.Image?.FileHash);
        Assert.Single(AoiDatabase.GetImportedImages());
    }

    [Fact]
    public void TryImportImageCopiesImageIntoConfiguredVault()
    {
        AoiDatabase.Initialize();
        var source = WriteTinyPng("vault-copy.png");

        var result = AoiDatabase.TryImportImage(source, "TBOX", "LOT-1", "top");

        Assert.True(result.Imported);
        Assert.NotNull(result.Image);
        Assert.True(File.Exists(source));
        Assert.True(File.Exists(result.Image.VaultPath));
        Assert.NotEqual(Path.GetFullPath(source), Path.GetFullPath(result.Image.VaultPath));
        Assert.StartsWith(Path.GetFullPath(AoiDatabase.ImageVaultPath), Path.GetFullPath(result.Image.VaultPath), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(result.Image.VaultPath));
        Assert.Equal(source, result.Image.OriginalPath);
    }

    [Fact]
    public void RecordInspectionResultPersistsHistory()
    {
        AoiDatabase.Initialize();
        var result = new AnalysisResult
        {
            SamplePath = @"C:\temp\sample.png",
            GoldenPath = @"C:\temp\golden.png",
            BoardProgram = "TBOX-MAIN",
            OperatorId = "Engineer01",
            InspectionEngine = "Unit Test Engine",
            DifferenceScore = 18.5,
            MeanBrightness = 112.25,
            Verdict = "NG",
            Confidence = 0.91,
            SuggestedDefect = "Bridge",
            PolicyName = "Balanced",
            ModelVersion = "TEST-1",
            ModelFilePath = @"C:\models\unit-test.onnx",
            ConfidenceThreshold = 0.73,
            DecisionReason = "Synthetic test result",
            Hotspot = new Rect(0.1, 0.2, 0.3, 0.4),
            Timing = new InspectionTiming
            {
                ImageLoadMilliseconds = 12,
                PreprocessingMilliseconds = 23,
                InferenceMilliseconds = 34,
                OverlayRenderingMilliseconds = 5,
                TotalInspectionMilliseconds = 74,
            },
            Defects =
            {
                new DefectResult
                {
                    DefectType = "Bridge",
                    Confidence = 0.88,
                    BoundingBox = new Rect(0.1, 0.2, 0.3, 0.4),
                    XPosition = 12,
                    YPosition = 34,
                    SideOrViewType = "top",
                    RoiId = "R1",
                    JudgmentStatus = "NG",
                },
            },
        };

        AoiDatabase.RecordInspectionResult(result);

        var history = AoiDatabase.GetInspectionHistory(new LogFilter());
        Assert.Single(history);
        Assert.Equal("TBOX-MAIN", history[0].BoardProgram);
        Assert.Equal("NG", history[0].Verdict);
        Assert.Equal("Unit Test Engine", history[0].InspectionEngine);
        Assert.Equal("TEST-1", history[0].ModelVersion);
        Assert.Equal(@"C:\models\unit-test.onnx", history[0].ModelFilePath);
        Assert.Equal(0.73, history[0].ConfidenceThreshold, precision: 3);
        Assert.Equal(18.5, history[0].DifferenceScore, precision: 3);
        Assert.Equal(12, history[0].ImageLoadMilliseconds, precision: 3);
        Assert.Equal(23, history[0].PreprocessingMilliseconds, precision: 3);
        Assert.Equal(34, history[0].InferenceMilliseconds, precision: 3);
        Assert.Equal(5, history[0].OverlayRenderingMilliseconds, precision: 3);
        Assert.Equal(74, history[0].TotalInspectionMilliseconds, precision: 3);
    }

    [Fact]
    public void RecordInspectionResultPersistsDefectRows()
    {
        AoiDatabase.Initialize();
        var result = new AnalysisResult
        {
            SamplePath = @"C:\temp\sample.png",
            GoldenPath = @"C:\temp\golden.png",
            BoardProgram = "TBOX-MAIN",
            OperatorId = "Engineer01 [Engineer]",
            InspectionEngine = "Unit Test Engine",
            DifferenceScore = 18.5,
            MeanBrightness = 112.25,
            Verdict = "NG",
            Confidence = 0.91,
            SuggestedDefect = "Bridge",
            PolicyName = "Balanced",
            ModelVersion = "TEST-1",
            DecisionReason = "Synthetic test result",
            Hotspot = new Rect(0.1, 0.2, 0.3, 0.4),
            Defects =
            {
                new DefectResult
                {
                    DefectType = "Bridge",
                    Confidence = 0.88,
                    BoundingBox = new Rect(0.1, 0.2, 0.3, 0.4),
                    XPosition = 12,
                    YPosition = 34,
                    SideOrViewType = "top",
                    RoiId = "R1",
                    JudgmentStatus = "NG",
                },
            },
        };

        AoiDatabase.RecordInspectionResult(result);

        using var connection = new SqliteConnection($"Data Source={AoiDatabase.DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT d.DefectType, d.Confidence, d.RoiX, d.RoiY, d.RoiWidth, d.RoiHeight,
                   d.XPosition, d.YPosition, d.SideOrViewType, d.RoiId, d.JudgmentStatus,
                   r.Verdict
            FROM Defects d
            INNER JOIN InspectionResults r ON r.Id = d.InspectionResultId;
            """;

        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("Bridge", reader.GetString(0));
        Assert.Equal(0.88, reader.GetDouble(1), precision: 3);
        Assert.Equal(0.1, reader.GetDouble(2), precision: 3);
        Assert.Equal(0.2, reader.GetDouble(3), precision: 3);
        Assert.Equal(0.3, reader.GetDouble(4), precision: 3);
        Assert.Equal(0.4, reader.GetDouble(5), precision: 3);
        Assert.Equal(12, reader.GetDouble(6), precision: 3);
        Assert.Equal(34, reader.GetDouble(7), precision: 3);
        Assert.Equal("top", reader.GetString(8));
        Assert.Equal("R1", reader.GetString(9));
        Assert.Equal("NG", reader.GetString(10));
        Assert.Equal("NG", reader.GetString(11));
        Assert.False(reader.Read());
    }

    [Fact]
    public void CalculateMetricsBuildsConfusionMatrixAndRates()
    {
        var rows = new[]
        {
            Row("NG", "NG"),
            Row("OK", "OK"),
            Row("OK", "NG"),
            Row("NG", "OK"),
            Row("UNKNOWN", "NG"),
        };

        var metrics = BatchValidationService.CalculateMetrics(rows);

        Assert.Equal(0.5, metrics.Accuracy, precision: 3);
        Assert.Equal(0.5, metrics.Precision, precision: 3);
        Assert.Equal(0.5, metrics.Recall, precision: 3);
        Assert.Equal(0.5, metrics.FalseCallRate, precision: 3);
        Assert.Equal(1, metrics.TruePositive);
        Assert.Equal(1, metrics.TrueNegative);
        Assert.Equal(1, metrics.FalsePositive);
        Assert.Equal(1, metrics.FalseNegative);
        Assert.Equal(1, metrics.Unknown);
        Assert.Equal(2, metrics.OkCount);
        Assert.Equal(3, metrics.NgCount);
        Assert.Equal(0, metrics.ReviewCount);

        rows[0].TotalInspectionMilliseconds = 850;
        rows[1].TotalInspectionMilliseconds = 1250;
        rows[2].TotalInspectionMilliseconds = 500;
        rows[3].TotalInspectionMilliseconds = 0;
        rows[4].TotalInspectionMilliseconds = 0;
        var performance = BatchValidationService.CalculatePerformanceSummary(rows);
        Assert.Equal(866.666, performance.AverageMilliseconds, precision: 2);
        Assert.Equal(1250, performance.MaxMilliseconds, precision: 3);
        Assert.Equal(500, performance.MinMilliseconds, precision: 3);
        Assert.Equal(1, performance.CountOverOneSecond);
    }

    [Fact]
    public void GenericDetectionOutputParserParsesDetectionRows()
    {
        var tensor = new DenseTensor<float>(new[] { 2, 6 });
        tensor[0, 0] = 1;
        tensor[0, 1] = 0.91f;
        tensor[0, 2] = 0.10f;
        tensor[0, 3] = 0.20f;
        tensor[0, 4] = 0.30f;
        tensor[0, 5] = 0.40f;
        tensor[1, 0] = 6;
        tensor[1, 1] = 0.20f;
        tensor[1, 2] = 10;
        tensor[1, 3] = 20;
        tensor[1, 4] = 30;
        tensor[1, 5] = 40;

        var parser = new GenericDetectionOutputParser();
        var detections = parser.Parse(
            tensor,
            new Dictionary<int, string>
            {
                [1] = "Solder Bridge",
                [6] = "Anomaly",
            },
            0.65,
            100,
            100);

        Assert.Single(detections);
        Assert.Equal("Solder Bridge", detections[0].Label);
        Assert.Equal(0.91, detections[0].Confidence, precision: 2);
        Assert.Equal(0.10, detections[0].BoundingBox.X, precision: 2);
        Assert.Equal(0.20, detections[0].BoundingBox.Y, precision: 2);
        Assert.Equal(0.30, detections[0].BoundingBox.Width, precision: 2);
        Assert.Equal(0.40, detections[0].BoundingBox.Height, precision: 2);
    }

    [Fact]
    public void OnnxEngineWithoutModelReturnsFriendlyReviewResult()
    {
        var engine = new OnnxInspectionEngine(new InspectionModelConfiguration
        {
            SelectedEngineKey = InspectionEngineFactory.OnnxEngineKey,
            ModelFilePath = Path.Combine(_root, "missing.onnx"),
            ModelVersion = "UNIT-ONNX",
            ConfidenceThreshold = 0.7,
            InputTensorName = "images",
            OutputTensorName = "detections",
        });

        var result = engine.Analyze(Path.Combine(_root, "missing-sample.png"), null, DetectionPriority.Balanced);

        Assert.Equal("REVIEW", result.Verdict);
        Assert.Equal("ONNX ML Model", result.InspectionEngine);
        Assert.Equal("UNIT-ONNX", result.ModelVersion);
        Assert.Equal(0.7, result.ConfidenceThreshold, precision: 3);
        Assert.Equal(Path.Combine(_root, "missing.onnx"), result.ModelFilePath);
        Assert.Contains(result.Evidence, line => line.Contains("No ML inference", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Defects, defect => defect.DefectType == "ML Model Missing");
    }

    [Fact]
    public void OnnxEngineInvalidModelReturnsFriendlyReviewResult()
    {
        var samplePath = WriteTinyPng("onnx-sample.png");
        var modelPath = Path.Combine(_root, "invalid.onnx");
        File.WriteAllText(modelPath, "not an onnx model");
        var engine = new OnnxInspectionEngine(new InspectionModelConfiguration
        {
            SelectedEngineKey = InspectionEngineFactory.OnnxEngineKey,
            ModelFilePath = modelPath,
            ModelVersion = "INVALID-ONNX",
            ConfidenceThreshold = 0.65,
        });

        var result = engine.Analyze(samplePath, null, DetectionPriority.Balanced);

        Assert.Equal("REVIEW", result.Verdict);
        Assert.Equal("ML Runtime Error", result.SuggestedDefect);
        Assert.Equal(modelPath, result.ModelFilePath);
        Assert.Contains(result.Evidence, line => line.Contains("Error type", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LoadValidationManifestParsesGroundTruthCsv()
    {
        var imageFolder = Path.Combine(_root, "images");
        Directory.CreateDirectory(imageFolder);
        var samplePath = Path.Combine(imageFolder, "board-1.png");
        var goldenPath = Path.Combine(imageFolder, "golden-1.png");
        File.WriteAllBytes(samplePath, TinyPngBytes());
        File.WriteAllBytes(goldenPath, TinyPngBytes());
        var csv = Path.Combine(_root, "ground_truth.csv");
        File.WriteAllText(
            csv,
            string.Join(
                Environment.NewLine,
                "image,ground_truth,golden_image,defect_type,side,refdes,lot_id,board_model,notes",
                "\"board-1.png\",NG,\"golden-1.png\",\"Solder, Bridge\",top,U10,LOT-7,TBOX,\"customer sample\""),
            Encoding.UTF8);

        var manifest = BatchValidationService.LoadValidationManifest(csv, imageFolder);

        Assert.True(manifest.IsFormalManifest);
        Assert.Single(manifest.OrderedEntries);
        Assert.True(manifest.ByImageName.ContainsKey("board-1.png"));
        var entry = manifest.ByImageName["board-1.png"];
        Assert.Equal("NG", entry.Label);
        Assert.Equal("Solder, Bridge", entry.DefectType);
        Assert.Equal("top", entry.Side);
        Assert.Equal("U10", entry.RefDes);
        Assert.Equal("LOT-7", entry.LotId);
        Assert.Equal("TBOX", entry.BoardModel);
        Assert.Equal("customer sample", entry.Notes);
        Assert.Equal(samplePath, entry.ImagePath);
        Assert.Equal(goldenPath, entry.GoldenPath);
    }

    [Fact]
    public void FormalValidationManifestPreservesMissingImageRowsForErrorReporting()
    {
        var imageFolder = Path.Combine(_root, "images");
        Directory.CreateDirectory(imageFolder);
        var csv = Path.Combine(_root, "ground_truth_missing.csv");
        File.WriteAllText(
            csv,
            string.Join(
                Environment.NewLine,
                "image,ground_truth,golden_image,defect_type,side,refdes,lot_id,board_model,notes",
                "missing-board.png,NG,missing-golden.png,Bridge,top,U10,LOT-7,TBOX,missing sample"),
            Encoding.UTF8);

        var manifest = BatchValidationService.LoadValidationManifest(csv, imageFolder);
        var runItems = BatchValidationService.BuildRunItems(Array.Empty<string>(), manifest);

        Assert.True(manifest.IsFormalManifest);
        Assert.Single(runItems);
        Assert.EndsWith("missing-board.png", runItems[0].ImagePath);
        Assert.False(File.Exists(runItems[0].ImagePath));
        Assert.EndsWith("missing-golden.png", runItems[0].Manifest.GoldenPath);
    }

    [Fact]
    public void ProfileViewParsesHeightMapCsvWithRequiredColumns()
    {
        Directory.CreateDirectory(_root);
        var csv = Path.Combine(_root, "height_map.csv");
        File.WriteAllText(
            csv,
            string.Join(
                Environment.NewLine,
                "x,y,height",
                "0,0,0.125",
                "1,0,0.250",
                "0,1,-0.050"),
            Encoding.UTF8);

        var points = Views.ProfileView.ParseHeightMap(csv);

        Assert.Equal(3, points.Count);
        Assert.Contains(points, p => p.X == 1 && p.Y == 0 && Math.Abs(p.Height - 0.250) < 0.0001);
        Assert.Contains(points, p => p.X == 0 && p.Y == 1 && Math.Abs(p.Height + 0.050) < 0.0001);
    }

    [Fact]
    public void ProfileDispositionEventsPersistUserAndRole()
    {
        AoiDatabase.Initialize();
        WorkflowState.Instance.SetCurrentUser("Engineer3D", UserRole.Engineer);

        WorkflowState.Instance.AddDisposition("Accept Defect: 3D sample-data defect type=Height High, x=1, y=2, height=0.250. 3D camera not connected.");

        var events = AoiDatabase.GetReviewEvents(new LogFilter { Result = "Height High" });
        Assert.Contains(events, e =>
            e.Category == "DISPOSITION" &&
            e.OperatorId == "Engineer3D [Engineer]" &&
            e.Message.Contains("3D camera not connected", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RecordExportPersistsExportHistory()
    {
        AoiDatabase.Initialize();

        AoiDatabase.RecordExport("InspectionHistoryCsv", @"C:\exports\history.csv", "WARN", "Admin01 [Admin]");

        var history = AoiDatabase.GetExportHistory();
        Assert.Single(history);
        Assert.Equal("InspectionHistoryCsv", history[0].ExportType);
        Assert.Equal(@"C:\exports\history.csv", history[0].FilePath);
        Assert.Equal("WARN", history[0].Status);
        Assert.Equal("Admin01 [Admin]", history[0].OperatorId);
    }

    [Fact]
    public void SoakTestReportCanBeWrittenWithStabilityMetrics()
    {
        var result = new SoakTestResult
        {
            ImageFolder = @"C:\soak\images",
            OutputFolder = _root,
            EngineName = "Unit Test Engine",
            EngineVersion = "TEST-1",
            OperatorId = "Admin01 [Admin]",
            RequestedDuration = TimeSpan.FromMinutes(2),
            DelayBetweenInspections = TimeSpan.FromMilliseconds(250),
            TotalCycles = 3,
            SuccessfulCycles = 2,
            FailedCycles = 1,
            AverageInspectionMilliseconds = 740,
            MinInspectionMilliseconds = 610,
            MaxInspectionMilliseconds = 1205,
            CountOverOneSecond = 1,
            StartManagedMemoryMegabytes = 20,
            EndManagedMemoryMegabytes = 22,
            StartWorkingSetMegabytes = 80,
            EndWorkingSetMegabytes = 85,
            PeakWorkingSetMegabytes = 90,
        };
        result.Errors.Add("Synthetic inspection exception");
        result.Cycles.Add(new SoakTestCycleRecord(1, "Top-000001", @"C:\soak\images\board.png", "REVIEW", 740, true, "Synthetic cycle"));

        var reportPath = SoakTestService.WriteHtmlReport(result, _root);
        var report = File.ReadAllText(reportPath);

        Assert.True(File.Exists(reportPath));
        Assert.Contains("AOI Monitor Soak Test Report", report);
        Assert.Contains("Stability Metrics", report);
        Assert.Contains("Total cycles", report);
        Assert.Contains("Count over 1 second", report);
        Assert.Contains("Synthetic inspection exception", report);
        Assert.Contains("folder-simulated camera frames", report);
    }

    [Fact]
    public void LocalRolesEnforceExpectedPageAndActionPermissions()
    {
        WorkflowState.Instance.SetCurrentUser("Operator01", UserRole.Operator);

        Assert.Equal("Operator01 [Operator]", WorkflowState.Instance.OperatorWithRole);
        Assert.False(RoleAuthorization.CanEditRecipes(WorkflowState.Instance.CurrentRole));
        Assert.False(RoleAuthorization.CanRunModelTests(WorkflowState.Instance.CurrentRole));
        Assert.False(RoleAuthorization.CanTestModelConfiguration(WorkflowState.Instance.CurrentRole));
        Assert.True(RoleAuthorization.CanAccessPage(WorkflowState.Instance.CurrentRole, "guide"));

        WorkflowState.Instance.SetCurrentUser("Engineer01", UserRole.Engineer);
        Assert.True(RoleAuthorization.CanEditRecipes(WorkflowState.Instance.CurrentRole));
        Assert.True(RoleAuthorization.CanRunModelTests(WorkflowState.Instance.CurrentRole));
        Assert.True(RoleAuthorization.CanTestModelConfiguration(WorkflowState.Instance.CurrentRole));
        Assert.True(RoleAuthorization.CanChangeThresholds(WorkflowState.Instance.CurrentRole));
        Assert.False(RoleAuthorization.CanExportLogs(WorkflowState.Instance.CurrentRole));

        WorkflowState.Instance.SetCurrentUser("Admin01", UserRole.Admin);
        Assert.True(RoleAuthorization.CanExportLogs(WorkflowState.Instance.CurrentRole));
        Assert.True(RoleAuthorization.CanManageSettings(WorkflowState.Instance.CurrentRole));
        Assert.True(RoleAuthorization.CanUseMaintenanceActions(WorkflowState.Instance.CurrentRole));
    }

    [Fact]
    public void ModelConfigurationValidatorReportsMissingModel()
    {
        var config = new InspectionModelConfiguration
        {
            SelectedEngineKey = InspectionEngineFactory.OnnxEngineKey,
            ModelFilePath = Path.Combine(_root, "missing.onnx"),
            InputTensorName = "images",
            OutputTensorName = "detections",
        };

        var result = ModelConfigurationValidator.Test(config);

        Assert.Equal(ModelConfigurationTestStatus.MissingModel, result.Status);
        Assert.Contains("missing", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ModelConfigurationValidatorReportsInvalidLabelMapBeforeRuntime()
    {
        Directory.CreateDirectory(_root);
        var modelPath = Path.Combine(_root, "invalid.onnx");
        File.WriteAllText(modelPath, "not an onnx model");
        var config = new InspectionModelConfiguration
        {
            SelectedEngineKey = InspectionEngineFactory.OnnxEngineKey,
            ModelFilePath = modelPath,
            LabelMapPath = Path.Combine(_root, "missing-labels.json"),
            InputTensorName = "images",
            OutputTensorName = "detections",
        };

        var result = ModelConfigurationValidator.Test(config);

        Assert.Equal(ModelConfigurationTestStatus.InvalidLabelMap, result.Status);
    }

    [Fact]
    public void ModelConfigurationValidatorRequiresTensorNames()
    {
        Directory.CreateDirectory(_root);
        var modelPath = Path.Combine(_root, "invalid.onnx");
        File.WriteAllText(modelPath, "not an onnx model");
        var config = new InspectionModelConfiguration
        {
            SelectedEngineKey = InspectionEngineFactory.OnnxEngineKey,
            ModelFilePath = modelPath,
        };

        var result = ModelConfigurationValidator.Test(config);

        Assert.Equal(ModelConfigurationTestStatus.RuntimeError, result.Status);
        Assert.Contains("tensor names", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TestAndSavePersistsRuntimeErrorReadiness()
    {
        Directory.CreateDirectory(_root);
        var modelPath = Path.Combine(_root, "invalid.onnx");
        File.WriteAllText(modelPath, "not an onnx model");
        var labelMapPath = Path.Combine(_root, "labels.txt");
        File.WriteAllLines(labelMapPath, new[] { "OK", "Solder Bridge" });
        var config = new InspectionModelConfiguration
        {
            SelectedEngineKey = InspectionEngineFactory.OnnxEngineKey,
            ModelFilePath = modelPath,
            LabelMapPath = labelMapPath,
            InputTensorName = "images",
            OutputTensorName = "detections",
        };

        var result = InspectionModelConfigurationService.TestAndSave(config);
        var loaded = InspectionModelConfigurationService.Load();

        Assert.Equal(ModelConfigurationTestStatus.RuntimeError, result.Status);
        Assert.Equal(ModelConfigurationTestStatus.RuntimeError, loaded.LastModelCheckResult);
        Assert.NotNull(loaded.LastModelCheckTimestampUtc);
        Assert.Equal(InspectionEngineStatus.MlRuntimeError, InspectionModelConfigurationService.GetStatus(loaded));
    }

    [Fact]
    public void SaveRecipeRevisionCanBeLoadedByBoardProgram()
    {
        AoiDatabase.Initialize();
        var document = new RecipeDocument
        {
            RecipeName = "TBOX_TOP",
            BoardProgram = "TBOX-MAIN",
            BackgroundImagePath = @"C:\images\board.png",
            Rois =
            {
                new RecipeRoiDocument
                {
                    Id = "ROI-1",
                    RoiType = "Presence",
                    X = 0.1,
                    Y = 0.2,
                    Width = 0.3,
                    Height = 0.4,
                    AiScoreThreshold = 0.75,
                },
            },
        };
        var json = JsonSerializer.Serialize(document);

        var id = AoiDatabase.SaveRecipeRevision(
            document.RecipeName,
            document.BoardProgram,
            "Engineer01",
            "Balanced",
            document.BackgroundImagePath,
            json);

        var loaded = AoiDatabase.GetLatestRecipeRevision("TBOX-MAIN");
        Assert.NotNull(loaded);
        Assert.Equal(id, loaded.Id);
        Assert.Equal("TBOX_TOP", loaded.RecipeName);
        Assert.Equal("Balanced", loaded.DetectionPriority);

        var loadedDocument = JsonSerializer.Deserialize<RecipeDocument>(loaded.RecipeJson);
        Assert.NotNull(loadedDocument);
        Assert.Equal("ROI-1", loadedDocument.Rois.Single().Id);
    }

    [Fact]
    public void RecordBatchTestRunPersistsResultsWithMetrics()
    {
        AoiDatabase.Initialize();
        var rows = new[]
        {
            Row("NG", "NG", "a.png"),
            Row("OK", "NG", "b.png"),
        };
        var metrics = BatchValidationService.CalculateMetrics(rows);

        var runId = AoiDatabase.RecordBatchTestRun(
            @"C:\validation",
            @"C:\validation\ground_truth.csv",
            "Unit Test Engine",
            "TEST-1",
            metrics.Accuracy,
            metrics.Precision,
            metrics.Recall,
            metrics.FalseCallRate,
            rows.Select(row => row.ToRecord()).ToArray());

        var run = AoiDatabase.GetLatestBatchTestRun();
        var persistedRows = AoiDatabase.GetBatchTestResults(runId);
        Assert.NotNull(run);
        Assert.Equal(runId, run.Id);
        Assert.Equal(2, run.TotalImages);
        Assert.Equal(1, run.FailedCount);
        Assert.Equal(metrics.Accuracy, run.Accuracy, precision: 3);
        Assert.Equal("TEST-1", run.ModelVersion);
        Assert.Equal(2, persistedRows.Count);
        Assert.Equal("FAIL", persistedRows[1].PassFail);
        Assert.Equal("Unit Test Engine", persistedRows[0].InspectionEngine);
        Assert.Equal("TEST-1", persistedRows[0].ModelVersion);
        Assert.Equal("Synthetic note", persistedRows[0].Notes);
        Assert.Equal(11, persistedRows[0].ImageLoadMilliseconds, precision: 3);
        Assert.Equal(22, persistedRows[0].PreprocessingMilliseconds, precision: 3);
        Assert.Equal(33, persistedRows[0].InferenceMilliseconds, precision: 3);
        Assert.Equal(4, persistedRows[0].OverlayRenderingMilliseconds, precision: 3);
        Assert.Equal(70, persistedRows[0].TotalInspectionMilliseconds, precision: 3);
    }

    [Fact]
    public void FolderCameraSourceReturnsFramesInSortedOrderAndCyclesByView()
    {
        var topFolder = Path.Combine(_root, "camera", "top");
        var sideFolder = Path.Combine(_root, "camera", "side");
        Directory.CreateDirectory(topFolder);
        Directory.CreateDirectory(sideFolder);
        File.WriteAllBytes(Path.Combine(topFolder, "002_top.png"), TinyPngBytes());
        File.WriteAllBytes(Path.Combine(topFolder, "001_top.png"), TinyPngBytes());
        File.WriteAllBytes(Path.Combine(sideFolder, "001_side.png"), TinyPngBytes());

        var source = new FolderCameraSource(
            new Dictionary<CameraViewType, string>
            {
                [CameraViewType.Top] = topFolder,
                [CameraViewType.Side] = sideFolder,
            },
            "BOARD-X",
            "LOT-42");

        Assert.Equal(CameraSourceStatus.Simulated, source.ConnectionStatus);
        source.StartAcquisition();

        source.SelectedView = CameraViewType.Top;
        var firstTop = source.GetNextFrame();
        var secondTop = source.GetNextFrame();
        var cycledTop = source.GetNextFrame();

        source.SelectedView = CameraViewType.Side;
        var firstSide = source.GetNextFrame();

        Assert.NotNull(firstTop);
        Assert.NotNull(secondTop);
        Assert.NotNull(cycledTop);
        Assert.NotNull(firstSide);
        Assert.EndsWith("001_top.png", firstTop.SourcePath);
        Assert.EndsWith("002_top.png", secondTop.SourcePath);
        Assert.Equal(firstTop.SourcePath, cycledTop.SourcePath);
        Assert.EndsWith("001_side.png", firstSide.SourcePath);
        Assert.Equal(CameraViewType.Top, firstTop.ViewType);
        Assert.Equal(CameraViewType.Side, firstSide.ViewType);
        Assert.Equal("BOARD-X", firstTop.BoardModel);
        Assert.Equal("LOT-42", firstTop.LotId);

        source.StopAcquisition();
        Assert.Null(source.GetNextFrame());
    }

    private IReadOnlySet<string> ReadTableNames()
    {
        using var connection = new SqliteConnection($"Data Source={AoiDatabase.DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
        using var reader = command.ExecuteReader();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
            names.Add(reader.GetString(0));

        return names;
    }

    private string WriteTinyPng(string fileName)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, fileName);
        File.WriteAllBytes(path, TinyPngBytes());
        return path;
    }

    private static BatchTestRow Row(string groundTruth, string engineResult, string imageName = "image.png")
    {
        var passFail = BatchValidationService.CalculatePassFail(groundTruth, engineResult);
        return new BatchTestRow
        {
            Image = imageName,
            ImagePath = Path.Combine(@"C:\validation", imageName),
            GroundTruth = groundTruth,
            EngineResult = engineResult,
            InspectionEngine = "Unit Test Engine",
            ModelVersion = "TEST-1",
            Score = engineResult == "NG" ? 80 : 5,
            PassFail = passFail,
            DefectType = "Synthetic",
            Side = "top",
            RefDes = "U1",
            LotId = "LOT",
            BoardModel = "TBOX",
            Notes = "Synthetic note",
            RoiX = 0.1,
            RoiY = 0.2,
            RoiWidth = 0.3,
            RoiHeight = 0.4,
            ImageLoadMilliseconds = 11,
            PreprocessingMilliseconds = 22,
            InferenceMilliseconds = 33,
            OverlayRenderingMilliseconds = 4,
            TotalInspectionMilliseconds = 70,
        };
    }

    private static byte[] TinyPngBytes()
        => Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=");
}
