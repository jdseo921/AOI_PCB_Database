using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Microsoft.Data.Sqlite;
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

        var history = AoiDatabase.GetInspectionHistory(new LogFilter());
        Assert.Single(history);
        Assert.Equal("TBOX-MAIN", history[0].BoardProgram);
        Assert.Equal("NG", history[0].Verdict);
        Assert.Equal("Unit Test Engine", history[0].InspectionEngine);
        Assert.Equal(18.5, history[0].DifferenceScore, precision: 3);
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
    public void LocalRolesEnforceExpectedPageAndActionPermissions()
    {
        WorkflowState.Instance.SetCurrentUser("Operator01", UserRole.Operator);

        Assert.Equal("Operator01 [Operator]", WorkflowState.Instance.OperatorWithRole);
        Assert.False(RoleAuthorization.CanEditRecipes(WorkflowState.Instance.CurrentRole));
        Assert.False(RoleAuthorization.CanRunModelTests(WorkflowState.Instance.CurrentRole));
        Assert.True(RoleAuthorization.CanAccessPage(WorkflowState.Instance.CurrentRole, "guide"));

        WorkflowState.Instance.SetCurrentUser("Engineer01", UserRole.Engineer);
        Assert.True(RoleAuthorization.CanEditRecipes(WorkflowState.Instance.CurrentRole));
        Assert.True(RoleAuthorization.CanRunModelTests(WorkflowState.Instance.CurrentRole));
        Assert.True(RoleAuthorization.CanChangeThresholds(WorkflowState.Instance.CurrentRole));
        Assert.False(RoleAuthorization.CanExportLogs(WorkflowState.Instance.CurrentRole));

        WorkflowState.Instance.SetCurrentUser("Admin01", UserRole.Admin);
        Assert.True(RoleAuthorization.CanExportLogs(WorkflowState.Instance.CurrentRole));
        Assert.True(RoleAuthorization.CanManageSettings(WorkflowState.Instance.CurrentRole));
        Assert.True(RoleAuthorization.CanUseMaintenanceActions(WorkflowState.Instance.CurrentRole));
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
        };
    }

    private static byte[] TinyPngBytes()
        => Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=");
}
