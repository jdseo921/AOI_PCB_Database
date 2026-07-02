using Microsoft.Data.Sqlite;

namespace AOI_Monitor.Data;

public sealed record AoiDatabaseMigration(
    int Version,
    string Description,
    Action<SqliteConnection, SqliteTransaction> Apply);

public static class AoiDatabaseMigrations
{
    private static readonly AoiDatabaseMigration[] OrderedMigrations =
    {
        new(1, "Current AOI Monitor schema baseline and compatibility repairs.", ApplyCurrentBaseline),
        new(2, "Add export artifact verification records.", ApplyExportVerification),
        new(3, "Add false-call reduction recommendation persistence.", ApplyFalseCallReduction),
        new(4, "Add validation breakdown evidence by class side and ROI.", ApplyValidationBreakdowns),
        new(5, "Add versioned threshold profiles and deployment markers.", ApplyThresholdProfiles),
        new(6, "Add Stage 2 camera acceptance test persistence.", ApplyCameraAcceptance),
        new(7, "Add Stage 2 lighting synchronization acceptance persistence.", ApplyLightingAcceptance),
        new(8, "Add robot cell acceptance persistence.", ApplyRobotAcceptance),
        new(9, "Add soak-test stability evidence persistence.", ApplySoakTests),
        new(10, "Add local build and test evidence persistence.", ApplyBuildTestEvidence),
        new(11, "Add model acceptance and release package evidence.", ApplyModelAcceptance),
        new(12, "Add 3D profile acceptance evidence persistence.", ApplyProfile3DAcceptance),
        new(13, "Add PLC/safety evidence to robot cell acceptance.", ApplyRobotSafetyAcceptance),
        new(14, "Add MES traceability test signoff evidence.", ApplyTraceabilityTestReports),
        new(15, "Add optional central data synchronization queue persistence.", ApplyCentralSync),
        new(16, "Extend soak-test stability evidence for factory acceptance.", ApplySoakTestFactoryEvidence),
        new(17, "Add normalized model acceptance metrics persistence.", ApplyModelAcceptanceMetrics),
        new(18, "Add explicit model lifecycle governance state.", ApplyModelLifecycleGovernance),
        new(19, "Add end-to-end inspection latency trace persistence.", ApplyInspectionLatencyTraces),
        new(20, "Add model lifecycle waiver expiry and evidence identifiers.", ApplyModelLifecycleEvidenceIdentifiers),
        new(21, "Add inspection latency verdict persistence.", ApplyInspectionLatencyVerdict),
        new(22, "Add local authenticated users and session audit tables.", ApplyLocalAuthenticationTables),
        new(23, "Add guided customer pilot wizard session persistence.", ApplyCustomerPilotWizard),
        new(24, "Add governable defect taxonomy and MES defect code mappings.", ApplyDefectTaxonomy),
        new(25, "Add pilot issue tracking persistence.", ApplyPilotIssueTracking),
        new(26, "Add learning annotation and training dataset version persistence.", ApplyTrainingDatasetPersistence),
        new(27, "Add image-only PCB sample learning persistence.", ApplyImageLearningPersistence),
        new(28, "Add image-only anomaly region scoring evidence.", ApplyImageLearningAnomalyEvidence),
    };

    public static int LatestVersion => OrderedMigrations[^1].Version;

    public static IReadOnlyList<AoiDatabaseMigration> Migrations => OrderedMigrations;

    public static void ApplyPending(SqliteConnection connection)
        => ApplyPending(connection, OrderedMigrations);

    public static void ApplyPending(SqliteConnection connection, IReadOnlyList<AoiDatabaseMigration> migrations)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(migrations);

        AoiDatabase.EnsureSchemaInfoTable(connection);
        var currentVersion = AoiDatabase.GetSchemaVersion(connection);

        foreach (var migration in migrations.OrderBy(item => item.Version))
        {
            if (migration.Version <= currentVersion)
                continue;

            using var transaction = connection.BeginTransaction();
            try
            {
                migration.Apply(connection, transaction);
                AoiDatabase.SetSchemaVersion(connection, transaction, migration.Version);
                transaction.Commit();
                currentVersion = migration.Version;
            }
            catch
            {
                try
                {
                    transaction.Rollback();
                }
                catch (Exception rollbackException)
                {
                    System.Diagnostics.Trace.WriteLine($"Migration rollback failed after original migration failure: {rollbackException.Message}");
                }

                throw;
            }
        }
    }

    private static void ApplyCurrentBaseline(SqliteConnection connection, SqliteTransaction transaction)
    {
        AoiDatabase.EnsureModelRegistryTable(connection, transaction);
        AoiDatabase.EnsureInspectionLatencyTraceTable(connection, transaction);
        AoiDatabase.EnsureValidationPackagesTable(connection, transaction);
        AoiDatabase.EnsureMesSpoolQueueTable(connection, transaction);
        AoiDatabase.EnsureCentralSyncTables(connection, transaction);
        AoiDatabase.EnsureLocalAuthenticationTables(connection, transaction);
        AoiDatabase.EnsureCustomerPilotTables(connection, transaction);
        AoiDatabase.EnsureDefectTaxonomyTables(connection, transaction);
        AoiDatabase.EnsurePilotIssueTables(connection, transaction);

        AoiDatabase.AddColumnIfMissing(connection, transaction, "InspectionResults", "BoardProgram", "TEXT NOT NULL DEFAULT 'UNKNOWN'");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "InspectionResults", "OperatorId", "TEXT NOT NULL DEFAULT 'UNKNOWN'");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "InspectionResults", "InspectionEngine", "TEXT NOT NULL DEFAULT 'Pixel Difference Prototype Engine'");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "InspectionResults", "ModelFilePath", "TEXT NOT NULL DEFAULT ''");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "InspectionResults", "ConfidenceThreshold", "REAL NOT NULL DEFAULT 0");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "InspectionResults", "ImageLoadMs", "REAL NOT NULL DEFAULT 0");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "InspectionResults", "PreprocessingMs", "REAL NOT NULL DEFAULT 0");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "InspectionResults", "InferenceMs", "REAL NOT NULL DEFAULT 0");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "InspectionResults", "OverlayRenderingMs", "REAL NOT NULL DEFAULT 0");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "InspectionResults", "TotalInspectionMs", "REAL NOT NULL DEFAULT 0");

        AoiDatabase.AddColumnIfMissing(connection, transaction, "Defects", "Confidence", "REAL NOT NULL DEFAULT 0");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "Defects", "XPosition", "REAL NULL");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "Defects", "YPosition", "REAL NULL");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "Defects", "SideOrViewType", "TEXT NOT NULL DEFAULT 'sample'");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "Defects", "RoiId", "TEXT NOT NULL DEFAULT 'ROI-UNASSIGNED'");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "Defects", "JudgmentStatus", "TEXT NOT NULL DEFAULT 'REVIEW'");

        AoiDatabase.AddColumnIfMissing(connection, transaction, "RecipeRevisions", "BoardProgram", "TEXT NOT NULL DEFAULT 'UNKNOWN'");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "RecipeRevisions", "OperatorId", "TEXT NOT NULL DEFAULT 'UNKNOWN'");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "RecipeRevisions", "BackgroundImagePath", "TEXT NOT NULL DEFAULT ''");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "RecipeRevisions", "RecipeJson", "TEXT NOT NULL DEFAULT ''");

        AoiDatabase.AddColumnIfMissing(connection, transaction, "BatchTestRuns", "ModelVersion", "TEXT NOT NULL DEFAULT 'UNKNOWN'");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "BatchTestResults", "InspectionEngine", "TEXT NOT NULL DEFAULT 'Pixel Difference Prototype Engine'");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "BatchTestResults", "ModelVersion", "TEXT NOT NULL DEFAULT 'PIXEL_DIFF_0.1'");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "BatchTestResults", "Side", "TEXT NOT NULL DEFAULT ''");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "BatchTestResults", "RefDes", "TEXT NOT NULL DEFAULT ''");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "BatchTestResults", "LotId", "TEXT NOT NULL DEFAULT ''");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "BatchTestResults", "BoardModel", "TEXT NOT NULL DEFAULT ''");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "BatchTestResults", "Notes", "TEXT NOT NULL DEFAULT ''");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "BatchTestResults", "ImageLoadMs", "REAL NOT NULL DEFAULT 0");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "BatchTestResults", "PreprocessingMs", "REAL NOT NULL DEFAULT 0");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "BatchTestResults", "InferenceMs", "REAL NOT NULL DEFAULT 0");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "BatchTestResults", "OverlayRenderingMs", "REAL NOT NULL DEFAULT 0");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "BatchTestResults", "TotalInspectionMs", "REAL NOT NULL DEFAULT 0");

        AoiDatabase.AddColumnIfMissing(connection, transaction, "ModelRegistry", "StoredLabelMapPath", "TEXT NOT NULL DEFAULT ''");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "ModelRegistry", "MetadataPath", "TEXT NOT NULL DEFAULT ''");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "ModelRegistry", "ValidationMessage", "TEXT NOT NULL DEFAULT ''");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "ModelRegistry", "Notes", "TEXT NOT NULL DEFAULT ''");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "ModelRegistry", "IsActive", "INTEGER NOT NULL DEFAULT 0");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "ModelRegistry", "AuditEventId", "INTEGER NULL");

        AoiDatabase.AddColumnIfMissing(connection, transaction, "ExportHistory", "OperatorId", "TEXT NOT NULL DEFAULT 'UNKNOWN'");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "ExportHistory", "AuditEventId", "INTEGER NULL");

        AoiDatabase.AddColumnIfMissing(connection, transaction, "ValidationPackages", "PackagePath", "TEXT NOT NULL DEFAULT ''");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "ValidationPackages", "Summary", "TEXT NOT NULL DEFAULT ''");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "ValidationPackages", "RunId", "INTEGER NULL");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "ValidationPackages", "OperatorId", "TEXT NOT NULL DEFAULT 'UNKNOWN'");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "ValidationPackages", "AuditEventId", "INTEGER NULL");
    }

    private static void ApplyModelLifecycleGovernance(SqliteConnection connection, SqliteTransaction transaction)
    {
        AoiDatabase.EnsureModelRegistryTable(connection, transaction);
    }

    private static void ApplyInspectionLatencyTraces(SqliteConnection connection, SqliteTransaction transaction)
    {
        AoiDatabase.EnsureInspectionLatencyTraceTable(connection, transaction);
    }

    private static void ApplyModelLifecycleEvidenceIdentifiers(SqliteConnection connection, SqliteTransaction transaction)
    {
        AoiDatabase.EnsureModelRegistryTable(connection, transaction);
    }

    private static void ApplyInspectionLatencyVerdict(SqliteConnection connection, SqliteTransaction transaction)
    {
        AoiDatabase.EnsureInspectionLatencyTraceTable(connection, transaction);
        AoiDatabase.AddColumnIfMissing(connection, transaction, "InspectionLatencyTraces", "Verdict", "TEXT NOT NULL DEFAULT ''");
    }

    private static void ApplyLocalAuthenticationTables(SqliteConnection connection, SqliteTransaction transaction)
    {
        AoiDatabase.EnsureLocalAuthenticationTables(connection, transaction);
    }

    private static void ApplyCustomerPilotWizard(SqliteConnection connection, SqliteTransaction transaction)
    {
        AoiDatabase.EnsureCustomerPilotTables(connection, transaction);
    }

    private static void ApplyDefectTaxonomy(SqliteConnection connection, SqliteTransaction transaction)
    {
        AoiDatabase.EnsureDefectTaxonomyTables(connection, transaction);
    }

    private static void ApplyPilotIssueTracking(SqliteConnection connection, SqliteTransaction transaction)
    {
        AoiDatabase.EnsurePilotIssueTables(connection, transaction);
    }

    private static void ApplyTrainingDatasetPersistence(SqliteConnection connection, SqliteTransaction transaction)
    {
        AoiDatabase.EnsureTrainingDatasetTables(connection, transaction);
    }

    private static void ApplyImageLearningPersistence(SqliteConnection connection, SqliteTransaction transaction)
    {
        AoiDatabase.EnsureImageLearningTables(connection, transaction);
    }

    private static void ApplyImageLearningAnomalyEvidence(SqliteConnection connection, SqliteTransaction transaction)
    {
        AoiDatabase.EnsureImageLearningTables(connection, transaction);
        AoiDatabase.AddColumnIfMissing(connection, transaction, "ImageLearningAnomalyRegions", "AreaPixels", "INTEGER NOT NULL DEFAULT 0");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "ImageLearningAnomalyRegions", "Confidence", "REAL NOT NULL DEFAULT 0");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "ImageLearningAnomalyRegions", "Reason", "TEXT NOT NULL DEFAULT ''");
    }

    private static void ApplyExportVerification(SqliteConnection connection, SqliteTransaction transaction)
    {
        AoiDatabase.EnsureExportVerificationTable(connection, transaction);
    }

    private static void ApplyFalseCallReduction(SqliteConnection connection, SqliteTransaction transaction)
    {
        AoiDatabase.EnsureFalseCallReductionTables(connection, transaction);
    }

    private static void ApplyValidationBreakdowns(SqliteConnection connection, SqliteTransaction transaction)
    {
        AoiDatabase.AddColumnIfMissing(connection, transaction, "BatchTestResults", "NormalizedDefectClass", "TEXT NOT NULL DEFAULT 'UNASSIGNED'");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "BatchTestResults", "NormalizedSide", "TEXT NOT NULL DEFAULT 'UNASSIGNED'");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "BatchTestResults", "RoiId", "TEXT NOT NULL DEFAULT 'UNASSIGNED'");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "BatchTestResults", "RoiType", "TEXT NOT NULL DEFAULT 'UNASSIGNED'");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "BatchTestResults", "FailureCategory", "TEXT NOT NULL DEFAULT 'UNKNOWN_GT'");
        AoiDatabase.EnsureValidationBreakdownMetricsTable(connection, transaction);
    }

    private static void ApplyThresholdProfiles(SqliteConnection connection, SqliteTransaction transaction)
    {
        AoiDatabase.AddColumnIfMissing(connection, transaction, "InspectionResults", "ThresholdProfileId", "TEXT NOT NULL DEFAULT ''");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "InspectionResults", "ThresholdProfileRevision", "TEXT NOT NULL DEFAULT ''");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "InspectionResults", "ThresholdSource", "TEXT NOT NULL DEFAULT 'Built-in policy default'");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "BatchTestRuns", "ThresholdProfileId", "TEXT NOT NULL DEFAULT ''");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "BatchTestRuns", "ThresholdProfileRevision", "TEXT NOT NULL DEFAULT ''");
        AoiDatabase.EnsureThresholdProfileTables(connection, transaction);
    }

    private static void ApplyCameraAcceptance(SqliteConnection connection, SqliteTransaction transaction)
    {
        AoiDatabase.EnsureCameraAcceptanceTables(connection, transaction);
    }

    private static void ApplyLightingAcceptance(SqliteConnection connection, SqliteTransaction transaction)
    {
        AoiDatabase.EnsureLightingAcceptanceTables(connection, transaction);
    }

    private static void ApplyRobotAcceptance(SqliteConnection connection, SqliteTransaction transaction)
    {
        AoiDatabase.EnsureRobotAcceptanceTables(connection, transaction);
    }

    private static void ApplySoakTests(SqliteConnection connection, SqliteTransaction transaction)
    {
        AoiDatabase.EnsureSoakTestTables(connection, transaction);
    }

    private static void ApplyBuildTestEvidence(SqliteConnection connection, SqliteTransaction transaction)
    {
        AoiDatabase.EnsureBuildTestEvidenceTable(connection, transaction);
    }

    private static void ApplyModelAcceptance(SqliteConnection connection, SqliteTransaction transaction)
    {
        AoiDatabase.EnsureModelAcceptanceTables(connection, transaction);
    }

    private static void ApplyProfile3DAcceptance(SqliteConnection connection, SqliteTransaction transaction)
    {
        AoiDatabase.EnsureProfile3DAcceptanceTable(connection, transaction);
    }

    private static void ApplyRobotSafetyAcceptance(SqliteConnection connection, SqliteTransaction transaction)
    {
        AoiDatabase.AddColumnIfMissing(connection, transaction, "RobotAcceptanceRuns", "SafetyControllerName", "TEXT NOT NULL DEFAULT ''");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "RobotAcceptanceRuns", "SafetySourceKind", "TEXT NOT NULL DEFAULT 'NotConnected'");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "RobotAcceptanceRuns", "SafetyFaultBlocked", "INTEGER NOT NULL DEFAULT 0");
    }

    private static void ApplyTraceabilityTestReports(SqliteConnection connection, SqliteTransaction transaction)
    {
        AoiDatabase.EnsureTraceabilityTestReportsTable(connection, transaction);
    }

    private static void ApplyCentralSync(SqliteConnection connection, SqliteTransaction transaction)
    {
        AoiDatabase.EnsureCentralSyncTables(connection, transaction);
    }

    private static void ApplySoakTestFactoryEvidence(SqliteConnection connection, SqliteTransaction transaction)
    {
        AoiDatabase.AddColumnIfMissing(connection, transaction, "SoakTestRuns", "AverageTotalCycleMs", "REAL NOT NULL DEFAULT 0");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "SoakTestRuns", "MaxTotalCycleMs", "REAL NOT NULL DEFAULT 0");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "SoakTestRuns", "P95TotalCycleMs", "REAL NOT NULL DEFAULT 0");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "SoakTestRuns", "CancellationReason", "TEXT NOT NULL DEFAULT ''");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "SoakTestRuns", "FirstCriticalError", "TEXT NOT NULL DEFAULT ''");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "SoakTestRuns", "MemoryWarningsJson", "TEXT NOT NULL DEFAULT '[]'");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "SoakTestIterations", "TotalCycleMs", "REAL NOT NULL DEFAULT 0");
        AoiDatabase.AddColumnIfMissing(connection, transaction, "SoakTestIterations", "ExceptionCategory", "TEXT NOT NULL DEFAULT ''");
    }

    private static void ApplyModelAcceptanceMetrics(SqliteConnection connection, SqliteTransaction transaction)
    {
        AoiDatabase.EnsureModelAcceptanceTables(connection, transaction);
    }
}
