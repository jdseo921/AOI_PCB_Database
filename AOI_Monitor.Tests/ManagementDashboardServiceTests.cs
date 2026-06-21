using System.Windows;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class ManagementDashboardServiceTests : IDisposable
{
    private readonly string _root;

    public ManagementDashboardServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_ManagementDashboard_Tests", Guid.NewGuid().ToString("N"));
        AoiDatabase.ConfigureStorageRoot(_root);
        AuthenticationSettingsService.ResetForTests();
        DeploymentProfileSettingsService.ResetForTests();
        FirstRunSettingsService.ResetForTests();
    }

    public void Dispose()
    {
        AuthenticationSettingsService.ResetForTests();
        DeploymentProfileSettingsService.ResetForTests();
        FirstRunSettingsService.ResetForTests();
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
    public void DashboardMetricsMatchSeededSqliteRecords()
    {
        SeedOperationalInspections();
        SeedValidationRun();

        var report = ManagementDashboardService.Build(new ManagementDashboardFilter { BoardModel = "BOARD-A", ModelVersion = "MODEL-1" });

        Assert.Equal(4, report.TotalBoardsInspected);
        Assert.Equal(2, report.OkCount);
        Assert.Equal(1, report.NgCount);
        Assert.Equal(1, report.ReviewCount);
        Assert.Equal(137.5, report.AverageInspectionTimeMs, precision: 2);
        Assert.Equal(200, report.P95InspectionTimeMs, precision: 2);
        Assert.Contains(report.TopDefectClasses, item => item.Name.Equals("BRIDGE", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.TopRoiRefdesContributors, item => item.Name.Contains("U1", StringComparison.OrdinalIgnoreCase));
        Assert.Single(report.ModelVersionTrend);
        Assert.Equal("MODEL-1", report.ModelVersionTrend[0].ModelVersion);
    }

    [Fact]
    public void FalseCallAndReviewBurdenCalculationsAreCorrect()
    {
        SeedOperationalInspections();
        SeedValidationRun();

        var report = ManagementDashboardService.Build(new ManagementDashboardFilter { BoardModel = "BOARD-A", LotId = "LOT-1" });

        Assert.Equal(2, report.FalseCallCount);
        Assert.Equal(1, report.PossibleEscapeCount);
        Assert.Equal(0.5, report.FalseCallRate, precision: 3);
        Assert.Equal(3.0, report.ManualReviewBurdenMinutes, precision: 3);
        var lotBreakdown = Assert.Single(report.LotModelBreakdown);
        Assert.Equal("LOT-1", lotBreakdown.LotId);
        Assert.Equal(2, lotBreakdown.FalseCalls);
        Assert.Equal(1, lotBreakdown.PossibleEscapes);
    }

    [Fact]
    public void ExportDoesNotIncludeRawCustomerImageData()
    {
        SeedOperationalInspections();
        SeedValidationRun();
        var report = ManagementDashboardService.Build(new ManagementDashboardFilter());

        var export = ManagementDashboardService.Export(report, Path.Combine(_root, "exports"), "Admin01 [Admin]");
        var exportedText = File.ReadAllText(export.HtmlPath) + File.ReadAllText(export.CsvPath) + File.ReadAllText(export.JsonPath);

        Assert.DoesNotContain("customer_board_001.png", exportedText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"C:\customer\images", exportedText, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(export.PdfPath));
    }

    [Fact]
    public void DateOperatorModelLotAndDeploymentProfileFiltersWork()
    {
        SeedOperationalInspections();
        SeedValidationRun();

        var report = ManagementDashboardService.Build(new ManagementDashboardFilter
        {
            FromDate = DateTime.Today.AddDays(-1),
            ToDate = DateTime.Today.AddDays(1),
            BoardModel = "BOARD-A",
            LotId = "LOT-1",
            OperatorId = "Engineer01",
            ModelVersion = "MODEL-1",
            DeploymentProfile = DeploymentProfile.Stage4MesPilot,
        });

        Assert.Equal(4, report.TotalBoardsInspected);
        Assert.Single(report.LotModelBreakdown);
        Assert.Equal(DeploymentProfile.Stage4MesPilot, report.Filter.DeploymentProfile);

        var operatorFilteredOut = ManagementDashboardService.Build(new ManagementDashboardFilter { OperatorId = "OperatorThatDidNotRun" });
        Assert.Equal(0, operatorFilteredOut.TotalBoardsInspected);

        var dateFilteredOut = ManagementDashboardService.Build(new ManagementDashboardFilter { FromDate = DateTime.Today.AddDays(10), ToDate = DateTime.Today.AddDays(11) });
        Assert.Equal(0, dateFilteredOut.TotalBoardsInspected);
        Assert.Empty(dateFilteredOut.LotModelBreakdown);
    }

    [Fact]
    public void PersistedLatencyTracesDriveAverageAndP95WhenAvailable()
    {
        SeedOperationalInspections();
        SeedValidationRun();
        var now = DateTime.UtcNow;
        InspectionLatencyService.Persist(new InspectionLatencyTrace
        {
            TraceId = "trace-1",
            CreatedAtUtc = now,
            FrameCapturedAtUtc = now,
            FrameReceivedAtUtc = now,
            OverlayRenderStartUtc = now.AddMilliseconds(10),
            OverlayRenderEndUtc = now.AddMilliseconds(80),
            TotalFrameToOverlayMs = 80,
            TotalFrameToSavedResultMs = 100,
            ModelId = "MODEL-1",
            SourceKind = "UnitTest",
        });
        InspectionLatencyService.Persist(new InspectionLatencyTrace
        {
            TraceId = "trace-2",
            CreatedAtUtc = now.AddMilliseconds(1),
            FrameCapturedAtUtc = now,
            FrameReceivedAtUtc = now,
            OverlayRenderStartUtc = now.AddMilliseconds(10),
            OverlayRenderEndUtc = now.AddMilliseconds(180),
            TotalFrameToOverlayMs = 180,
            TotalFrameToSavedResultMs = 200,
            ModelId = "MODEL-1",
            SourceKind = "UnitTest",
        });

        var report = ManagementDashboardService.Build(new ManagementDashboardFilter { ModelVersion = "MODEL-1" });

        Assert.Equal(130, report.AverageInspectionTimeMs, precision: 2);
        Assert.Equal(180, report.P95InspectionTimeMs, precision: 2);
        Assert.Contains(report.Notes, note => note.Contains("persisted frame-to-overlay", StringComparison.OrdinalIgnoreCase));
    }

    private void SeedOperationalInspections()
    {
        AoiDatabase.Initialize();
        foreach (var (verdict, defect, timeMs, imageName) in new[]
        {
            ("OK", "NONE", 100.0, "customer_board_001.png"),
            ("OK", "NONE", 120.0, "customer_board_002.png"),
            ("NG", "BRIDGE", 130.0, "customer_board_003.png"),
            ("REVIEW", "MISSING", 200.0, "customer_board_004.png"),
        })
        {
            AoiDatabase.RecordInspectionResult(new AnalysisResult
            {
                SamplePath = $@"C:\customer\images\{imageName}",
                BoardProgram = "BOARD-A",
                OperatorId = "Engineer01 [Engineer]",
                InspectionEngine = "Unit Test Engine",
                ModelVersion = "MODEL-1",
                Verdict = verdict,
                SuggestedDefect = defect,
                Confidence = 0.8,
                DifferenceScore = 12,
                Hotspot = new Rect(1, 2, 3, 4),
                Timing = new InspectionTiming
                {
                    TotalInspectionMilliseconds = timeMs,
                    InferenceMilliseconds = timeMs / 2,
                    OverlayRenderingMilliseconds = 10,
                },
            });
        }
    }

    private static void SeedValidationRun()
    {
        var rows = new[]
        {
            ValidationRow("OK", "OK", "NONE", "R1", "U1", "LOT-1", "PASS"),
            ValidationRow("OK", "NG", "BRIDGE", "R1", "U1", "LOT-1", "FAIL", "FalseCall"),
            ValidationRow("OK", "REVIEW", "MISSING", "R2", "U2", "LOT-1", "FAIL", "FalseCall"),
            ValidationRow("NG", "OK", "BRIDGE", "R1", "U1", "LOT-1", "FAIL", "PossibleEscape"),
        };

        AoiDatabase.RecordBatchTestRun(
            @"C:\customer\images",
            @"C:\customer\manifest.csv",
            "Unit Test Engine",
            "MODEL-1",
            accuracy: 0.5,
            precision: 0.5,
            recall: 0.5,
            falseCallRate: 0.5,
            rows);
    }

    private static BatchTestResultRecord ValidationRow(
        string groundTruth,
        string engineResult,
        string defectClass,
        string roiId,
        string refdes,
        string lotId,
        string passFail,
        string failureCategory = "")
        => new(
            Id: 0,
            RunId: 0,
            ImagePath: $@"C:\customer\images\{refdes}_{roiId}.png",
            ImageName: $"{refdes}_{roiId}.png",
            GroundTruth: groundTruth,
            EngineResult: engineResult,
            InspectionEngine: "Unit Test Engine",
            ModelVersion: "MODEL-1",
            Score: engineResult == "OK" ? 0.1 : 0.9,
            PassFail: passFail,
            DefectType: defectClass,
            NormalizedDefectClass: defectClass,
            NormalizedSide: "TOP",
            RoiId: roiId,
            RoiType: "SOLDER",
            FailureCategory: failureCategory,
            RoiX: 1,
            RoiY: 2,
            RoiWidth: 3,
            RoiHeight: 4,
            Side: "TOP",
            RefDes: refdes,
            LotId: lotId,
            BoardModel: "BOARD-A",
            Notes: string.Empty,
            ImageLoadMilliseconds: 10,
            PreprocessingMilliseconds: 10,
            InferenceMilliseconds: 20,
            OverlayRenderingMilliseconds: 5,
            TotalInspectionMilliseconds: 45,
            CreatedAtUtc: DateTime.UtcNow);
}
