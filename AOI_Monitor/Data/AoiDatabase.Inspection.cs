using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Microsoft.Data.Sqlite;

namespace AOI_Monitor.Data;

public static partial class AoiDatabase
{
    public static long RecordInspectionResult(AnalysisResult result)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO InspectionResults
                (SampleImagePath, GoldenImagePath, BoardProgram, OperatorId, InspectionEngine, DifferenceScore, MeanBrightness, Verdict, Confidence,
                 SuggestedDefect, PolicyName, ModelVersion, ModelFilePath, ConfidenceThreshold, ThresholdProfileId, ThresholdProfileRevision,
                 ThresholdSource, DecisionReason, HotspotX, HotspotY, HotspotWidth,
                 HotspotHeight, ImageLoadMs, PreprocessingMs, InferenceMs, OverlayRenderingMs, TotalInspectionMs, CreatedAtUtc)
            VALUES
                ($sampleImagePath, $goldenImagePath, $boardProgram, $operatorId, $inspectionEngine, $differenceScore, $meanBrightness, $verdict, $confidence,
                 $suggestedDefect, $policyName, $modelVersion, $modelFilePath, $confidenceThreshold, $thresholdProfileId, $thresholdProfileRevision,
                 $thresholdSource, $decisionReason, $hotspotX, $hotspotY, $hotspotWidth,
                 $hotspotHeight, $imageLoadMs, $preprocessingMs, $inferenceMs, $overlayRenderingMs, $totalInspectionMs, $createdAtUtc);
            SELECT last_insert_rowid();
            """;

        command.Parameters.AddWithValue("$sampleImagePath", result.SamplePath);
        command.Parameters.AddWithValue("$goldenImagePath", (object?)result.GoldenPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$boardProgram", result.BoardProgram);
        command.Parameters.AddWithValue("$operatorId", result.OperatorId);
        command.Parameters.AddWithValue("$inspectionEngine", result.InspectionEngine);
        command.Parameters.AddWithValue("$differenceScore", result.DifferenceScore);
        command.Parameters.AddWithValue("$meanBrightness", result.MeanBrightness);
        command.Parameters.AddWithValue("$verdict", result.Verdict);
        command.Parameters.AddWithValue("$confidence", result.Confidence);
        command.Parameters.AddWithValue("$suggestedDefect", result.SuggestedDefect);
        command.Parameters.AddWithValue("$policyName", result.PolicyName);
        command.Parameters.AddWithValue("$modelVersion", result.ModelVersion);
        command.Parameters.AddWithValue("$modelFilePath", result.ModelFilePath);
        command.Parameters.AddWithValue("$confidenceThreshold", result.ConfidenceThreshold);
        command.Parameters.AddWithValue("$thresholdProfileId", result.ThresholdProfileId);
        command.Parameters.AddWithValue("$thresholdProfileRevision", result.ThresholdProfileRevision);
        command.Parameters.AddWithValue("$thresholdSource", result.ThresholdSource);
        command.Parameters.AddWithValue("$decisionReason", result.DecisionReason);
        command.Parameters.AddWithValue("$hotspotX", result.Hotspot.X);
        command.Parameters.AddWithValue("$hotspotY", result.Hotspot.Y);
        command.Parameters.AddWithValue("$hotspotWidth", result.Hotspot.Width);
        command.Parameters.AddWithValue("$hotspotHeight", result.Hotspot.Height);
        command.Parameters.AddWithValue("$imageLoadMs", result.Timing.ImageLoadMilliseconds);
        command.Parameters.AddWithValue("$preprocessingMs", result.Timing.PreprocessingMilliseconds);
        command.Parameters.AddWithValue("$inferenceMs", result.Timing.InferenceMilliseconds);
        command.Parameters.AddWithValue("$overlayRenderingMs", result.Timing.OverlayRenderingMilliseconds);
        command.Parameters.AddWithValue("$totalInspectionMs", result.Timing.TotalInspectionMilliseconds);
        command.Parameters.AddWithValue("$createdAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        var inspectionResultId = (long)(command.ExecuteScalar() ?? 0L);

        foreach (var defect in result.Defects)
        {
            using var defectCommand = connection.CreateCommand();
            defectCommand.Transaction = transaction;
            defectCommand.CommandText =
                """
                INSERT INTO Defects
                    (InspectionResultId, ImageId, RefDes, DefectType, Severity, Confidence,
                     RoiX, RoiY, RoiWidth, RoiHeight, XPosition, YPosition, SideOrViewType,
                     RoiId, JudgmentStatus, CreatedAtUtc)
                VALUES
                    ($inspectionResultId, NULL, NULL, $defectType, $severity, $confidence,
                     $roiX, $roiY, $roiWidth, $roiHeight, $xPosition, $yPosition, $sideOrViewType,
                     $roiId, $judgmentStatus, $createdAtUtc);
                """;

            defectCommand.Parameters.AddWithValue("$inspectionResultId", inspectionResultId);
            defectCommand.Parameters.AddWithValue("$defectType", defect.DefectType);
            defectCommand.Parameters.AddWithValue("$severity", ToDefectSeverity(defect.JudgmentStatus));
            defectCommand.Parameters.AddWithValue("$confidence", defect.Confidence);
            defectCommand.Parameters.AddWithValue("$roiX", defect.BoundingBox.X);
            defectCommand.Parameters.AddWithValue("$roiY", defect.BoundingBox.Y);
            defectCommand.Parameters.AddWithValue("$roiWidth", defect.BoundingBox.Width);
            defectCommand.Parameters.AddWithValue("$roiHeight", defect.BoundingBox.Height);
            defectCommand.Parameters.AddWithValue("$xPosition", defect.XPosition);
            defectCommand.Parameters.AddWithValue("$yPosition", defect.YPosition);
            defectCommand.Parameters.AddWithValue("$sideOrViewType", defect.SideOrViewType);
            defectCommand.Parameters.AddWithValue("$roiId", defect.RoiId);
            defectCommand.Parameters.AddWithValue("$judgmentStatus", defect.JudgmentStatus);
            defectCommand.Parameters.AddWithValue("$createdAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            defectCommand.ExecuteNonQuery();
        }

        transaction.Commit();
        RecordAuditEvent(
            "INSPECTION_RESULT",
            $"Inspection result persisted: {result.Verdict}, engine {result.InspectionEngine}, score {result.DifferenceScore:F1}%.",
            operatorWithRole: result.OperatorId,
            relatedEntityType: "InspectionResult",
            relatedEntityId: inspectionResultId.ToString(CultureInfo.InvariantCulture),
            relatedPath: result.SamplePath);
        return inspectionResultId;
    }

    public static long RecordInspectionLatencyTrace(InspectionLatencyTrace trace)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO InspectionLatencyTraces
                (TraceId, CreatedAtUtc, FrameCapturedAtUtc, FrameReceivedAtUtc,
                 PreprocessingStartUtc, PreprocessingEndUtc, InferenceStartUtc, InferenceEndUtc,
                 PostprocessStartUtc, PostprocessEndUtc, OverlayRenderStartUtc, OverlayRenderEndUtc,
                 ResultPersistStartUtc, ResultPersistEndUtc, TotalFrameToOverlayMs, TotalFrameToSavedResultMs,
                 SourceKind, Engine, ModelId, ImageWidth, ImageHeight, Verdict, WarningsJson)
            VALUES
                ($traceId, $createdAtUtc, $frameCapturedAtUtc, $frameReceivedAtUtc,
                 $preprocessingStartUtc, $preprocessingEndUtc, $inferenceStartUtc, $inferenceEndUtc,
                 $postprocessStartUtc, $postprocessEndUtc, $overlayRenderStartUtc, $overlayRenderEndUtc,
                 $resultPersistStartUtc, $resultPersistEndUtc, $totalFrameToOverlayMs, $totalFrameToSavedResultMs,
                 $sourceKind, $engine, $modelId, $imageWidth, $imageHeight, $verdict, $warningsJson);
            SELECT last_insert_rowid();
            """;
        BindInspectionLatencyTrace(command, trace);
        var id = (long)(command.ExecuteScalar() ?? 0L);
        trace.Id = id;
        return id;
    }

    public static IReadOnlyList<InspectionLatencyTrace> GetInspectionLatencyTraces(int limit = 500)
    {
        EnsureInitialized();

        var traces = new List<InspectionLatencyTrace>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, TraceId, CreatedAtUtc, FrameCapturedAtUtc, FrameReceivedAtUtc,
                   PreprocessingStartUtc, PreprocessingEndUtc, InferenceStartUtc, InferenceEndUtc,
                   PostprocessStartUtc, PostprocessEndUtc, OverlayRenderStartUtc, OverlayRenderEndUtc,
                   ResultPersistStartUtc, ResultPersistEndUtc, TotalFrameToOverlayMs, TotalFrameToSavedResultMs,
                   SourceKind, Engine, ModelId, ImageWidth, ImageHeight, Verdict, WarningsJson
            FROM InspectionLatencyTraces
            ORDER BY datetime(CreatedAtUtc) DESC, Id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 10000));
        using var reader = command.ExecuteReader();
        while (reader.Read())
            traces.Add(ReadInspectionLatencyTrace(reader));

        return traces;
    }

    public static IReadOnlyList<InspectionLatencyTrace> GetInspectionLatencyTraces(DateTime fromUtc, DateTime toUtc, int limit = 10000)
    {
        EnsureInitialized();

        var from = fromUtc.ToUniversalTime();
        var to = toUtc.ToUniversalTime();
        if (to < from)
            (from, to) = (to, from);

        var traces = new List<InspectionLatencyTrace>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, TraceId, CreatedAtUtc, FrameCapturedAtUtc, FrameReceivedAtUtc,
                   PreprocessingStartUtc, PreprocessingEndUtc, InferenceStartUtc, InferenceEndUtc,
                   PostprocessStartUtc, PostprocessEndUtc, OverlayRenderStartUtc, OverlayRenderEndUtc,
                   ResultPersistStartUtc, ResultPersistEndUtc, TotalFrameToOverlayMs, TotalFrameToSavedResultMs,
                   SourceKind, Engine, ModelId, ImageWidth, ImageHeight, Verdict, WarningsJson
            FROM InspectionLatencyTraces
            WHERE CreatedAtUtc >= $fromUtc
              AND CreatedAtUtc <= $toUtc
            ORDER BY datetime(CreatedAtUtc) DESC, Id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$fromUtc", from.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$toUtc", to.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100000));
        using var reader = command.ExecuteReader();
        while (reader.Read())
            traces.Add(ReadInspectionLatencyTrace(reader));

        return traces;
    }

    public static long RecordBatchTestRun(
        string imageFolder,
        string? groundTruthCsvPath,
        string engineName,
        string modelVersion,
        double accuracy,
        double precision,
        double recall,
        double falseCallRate,
        IReadOnlyList<BatchTestResultRecord> results,
        string thresholdProfileId = "",
        string thresholdProfileRevision = "")
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO BatchTestRuns
                (ImageFolder, GroundTruthCsvPath, EngineName, ModelVersion, ThresholdProfileId, ThresholdProfileRevision, Accuracy, Precision, Recall,
                 FalseCallRate, TotalImages, FailedCount, CreatedAtUtc)
            VALUES
                ($imageFolder, $groundTruthCsvPath, $engineName, $modelVersion, $thresholdProfileId, $thresholdProfileRevision, $accuracy, $precision, $recall,
                 $falseCallRate, $totalImages, $failedCount, $createdAtUtc);
            SELECT last_insert_rowid();
            """;

        command.Parameters.AddWithValue("$imageFolder", imageFolder);
        command.Parameters.AddWithValue("$groundTruthCsvPath", (object?)groundTruthCsvPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$engineName", engineName);
        command.Parameters.AddWithValue("$modelVersion", modelVersion);
        command.Parameters.AddWithValue("$thresholdProfileId", thresholdProfileId);
        command.Parameters.AddWithValue("$thresholdProfileRevision", thresholdProfileRevision);
        command.Parameters.AddWithValue("$accuracy", accuracy);
        command.Parameters.AddWithValue("$precision", precision);
        command.Parameters.AddWithValue("$recall", recall);
        command.Parameters.AddWithValue("$falseCallRate", falseCallRate);
        command.Parameters.AddWithValue("$totalImages", results.Count);
        command.Parameters.AddWithValue("$failedCount", results.Count(r => r.PassFail == "FAIL"));
        command.Parameters.AddWithValue("$createdAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));

        var runId = (long)(command.ExecuteScalar() ?? 0L);

        foreach (var result in results)
        {
            using var resultCommand = connection.CreateCommand();
            resultCommand.Transaction = transaction;
            resultCommand.CommandText =
                """
                INSERT INTO BatchTestResults
                    (RunId, ImagePath, ImageName, GroundTruth, EngineResult, InspectionEngine, ModelVersion, Score, PassFail,
                     DefectType, NormalizedDefectClass, NormalizedSide, RoiId, RoiType, FailureCategory,
                     RoiX, RoiY, RoiWidth, RoiHeight, Side, RefDes, LotId, BoardModel, Notes,
                     ImageLoadMs, PreprocessingMs, InferenceMs, OverlayRenderingMs, TotalInspectionMs, CreatedAtUtc)
                VALUES
                    ($runId, $imagePath, $imageName, $groundTruth, $engineResult, $inspectionEngine, $modelVersion, $score, $passFail,
                     $defectType, $normalizedDefectClass, $normalizedSide, $roiId, $roiType, $failureCategory,
                     $roiX, $roiY, $roiWidth, $roiHeight, $side, $refDes, $lotId, $boardModel, $notes,
                     $imageLoadMs, $preprocessingMs, $inferenceMs, $overlayRenderingMs, $totalInspectionMs, $createdAtUtc);
                """;

            resultCommand.Parameters.AddWithValue("$runId", runId);
            resultCommand.Parameters.AddWithValue("$imagePath", result.ImagePath);
            resultCommand.Parameters.AddWithValue("$imageName", result.ImageName);
            resultCommand.Parameters.AddWithValue("$groundTruth", result.GroundTruth);
            resultCommand.Parameters.AddWithValue("$engineResult", result.EngineResult);
            resultCommand.Parameters.AddWithValue("$inspectionEngine", result.InspectionEngine);
            resultCommand.Parameters.AddWithValue("$modelVersion", result.ModelVersion);
            resultCommand.Parameters.AddWithValue("$score", result.Score);
            resultCommand.Parameters.AddWithValue("$passFail", result.PassFail);
            resultCommand.Parameters.AddWithValue("$defectType", result.DefectType);
            resultCommand.Parameters.AddWithValue("$normalizedDefectClass", result.NormalizedDefectClass);
            resultCommand.Parameters.AddWithValue("$normalizedSide", result.NormalizedSide);
            resultCommand.Parameters.AddWithValue("$roiId", result.RoiId);
            resultCommand.Parameters.AddWithValue("$roiType", result.RoiType);
            resultCommand.Parameters.AddWithValue("$failureCategory", result.FailureCategory);
            resultCommand.Parameters.AddWithValue("$roiX", result.RoiX);
            resultCommand.Parameters.AddWithValue("$roiY", result.RoiY);
            resultCommand.Parameters.AddWithValue("$roiWidth", result.RoiWidth);
            resultCommand.Parameters.AddWithValue("$roiHeight", result.RoiHeight);
            resultCommand.Parameters.AddWithValue("$side", result.Side);
            resultCommand.Parameters.AddWithValue("$refDes", result.RefDes);
            resultCommand.Parameters.AddWithValue("$lotId", result.LotId);
            resultCommand.Parameters.AddWithValue("$boardModel", result.BoardModel);
            resultCommand.Parameters.AddWithValue("$notes", result.Notes);
            resultCommand.Parameters.AddWithValue("$imageLoadMs", result.ImageLoadMilliseconds);
            resultCommand.Parameters.AddWithValue("$preprocessingMs", result.PreprocessingMilliseconds);
            resultCommand.Parameters.AddWithValue("$inferenceMs", result.InferenceMilliseconds);
            resultCommand.Parameters.AddWithValue("$overlayRenderingMs", result.OverlayRenderingMilliseconds);
            resultCommand.Parameters.AddWithValue("$totalInspectionMs", result.TotalInspectionMilliseconds);
            resultCommand.Parameters.AddWithValue("$createdAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            resultCommand.ExecuteNonQuery();
        }

        foreach (var metric in ClassMetricsService.Flatten(ClassMetricsService.Calculate(results.Select(BatchTestRow.FromRecord).ToArray())))
        {
            using var metricCommand = connection.CreateCommand();
            metricCommand.Transaction = transaction;
            metricCommand.CommandText =
                """
                INSERT INTO ValidationBreakdownMetrics
                    (RunId, BreakdownType, Key, DisplayName, Total, TruePositive, TrueNegative,
                     FalsePositive, FalseNegative, WrongDefectClass, WrongSide, UnknownGroundTruth,
                     Precision, Recall, FalseCallRate, CreatedAtUtc)
                VALUES
                    ($runId, $breakdownType, $key, $displayName, $total, $truePositive, $trueNegative,
                     $falsePositive, $falseNegative, $wrongDefectClass, $wrongSide, $unknownGroundTruth,
                     $precision, $recall, $falseCallRate, $createdAtUtc);
                """;
            BindValidationBreakdownMetric(metricCommand, runId, metric);
            metricCommand.ExecuteNonQuery();
        }

        transaction.Commit();
        return runId;
    }

    public static BatchTestRunRecord? GetLatestBatchTestRun()
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT Id, ImageFolder, GroundTruthCsvPath, EngineName, ModelVersion, CreatedAtUtc, Accuracy, Precision,
                   Recall, FalseCallRate, TotalImages, FailedCount, ThresholdProfileId, ThresholdProfileRevision
            FROM BatchTestRuns
            ORDER BY datetime(CreatedAtUtc) DESC, Id DESC
            LIMIT 1;
            """;

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadBatchTestRun(reader) : null;
    }

    public static IReadOnlyList<BatchTestResultRecord> GetBatchTestResults(long runId)
    {
        EnsureInitialized();

        var results = new List<BatchTestResultRecord>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, RunId, ImagePath, ImageName, GroundTruth, EngineResult, Score, PassFail,
                   DefectType, RoiX, RoiY, RoiWidth, RoiHeight, Side, RefDes, LotId, BoardModel,
                   InspectionEngine, ModelVersion, Notes, ImageLoadMs, PreprocessingMs, InferenceMs,
                   NormalizedDefectClass, NormalizedSide, RoiId, RoiType, FailureCategory,
                   OverlayRenderingMs, TotalInspectionMs, CreatedAtUtc
            FROM BatchTestResults
            WHERE RunId = $runId
            ORDER BY Id ASC;
            """;
        command.Parameters.AddWithValue("$runId", runId);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadBatchTestResult(reader));
        }

        return results;
    }

    public static IReadOnlyList<InspectionHistoryRecord> GetInspectionHistory(LogFilter filter)
    {
        EnsureInitialized();

        var records = new List<InspectionHistoryRecord>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var where = BuildInspectionWhere(filter, command);
        command.CommandText =
            $"""
            SELECT Id, CreatedAtUtc, BoardProgram, OperatorId, InspectionEngine, ModelVersion, ModelFilePath, ConfidenceThreshold,
                   SampleImagePath, GoldenImagePath, Verdict, DifferenceScore, Confidence,
                   SuggestedDefect, DecisionReason, HotspotX, HotspotY, HotspotWidth, HotspotHeight,
                   ImageLoadMs, PreprocessingMs, InferenceMs, OverlayRenderingMs, TotalInspectionMs
            FROM InspectionResults
            {where}
            ORDER BY datetime(CreatedAtUtc) DESC, Id DESC;
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            records.Add(ReadInspectionHistory(reader));
        }

        return records;
    }

    public static IReadOnlyList<ReviewEventRecord> GetReviewEvents(LogFilter filter)
    {
        EnsureInitialized();

        var records = new List<ReviewEventRecord>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var where = BuildReviewWhere(filter, command);
        command.CommandText =
            $"""
            SELECT Id, EventTimeUtc, Category, OperatorId, Disposition, Message
            FROM ReviewEvents
            {where}
            ORDER BY datetime(EventTimeUtc) DESC, Id DESC;
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            records.Add(ReadReviewEvent(reader));
        }

        return records;
    }

    public static IReadOnlyList<ExportHistoryRecord> GetExportHistory(int limit = 100)
    {
        EnsureInitialized();

        var records = new List<ExportHistoryRecord>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, CreatedAtUtc, ExportType, FilePath, Status, OperatorId, AuditEventId
            FROM ExportHistory
            ORDER BY datetime(CreatedAtUtc) DESC, Id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            records.Add(ReadExportHistory(reader));
        }

        return records;
    }

    public static ExportVerificationRecord? GetLatestExportVerification(long exportHistoryId)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, ExportHistoryId, CheckedAtUtc, ExportType, ExportPath, Status,
                   Sha256, SizeBytes, MessagesJson, ArtifactChecksumsJson
            FROM ExportVerification
            WHERE ExportHistoryId = $exportHistoryId
            ORDER BY datetime(CheckedAtUtc) DESC, Id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$exportHistoryId", exportHistoryId);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadExportVerification(reader) : null;
    }

    public static IReadOnlyList<ExportVerificationRecord> GetExportVerifications(int limit = 100)
    {
        EnsureInitialized();

        var records = new List<ExportVerificationRecord>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, ExportHistoryId, CheckedAtUtc, ExportType, ExportPath, Status,
                   Sha256, SizeBytes, MessagesJson, ArtifactChecksumsJson
            FROM ExportVerification
            ORDER BY datetime(CheckedAtUtc) DESC, Id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = command.ExecuteReader();
        while (reader.Read())
            records.Add(ReadExportVerification(reader));

        return records;
    }

    public static BuildTestEvidenceRecord? GetLatestBuildTestEvidence()
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, GeneratedAtUtc, CommitSha, Configuration, HygieneStatus, RestoreStatus,
                   BuildStatus, TestStatus, PublishValidationStatus, EvidencePath, OperatorId,
                   CreatedAtUtc, TestResultPath, MachineName
            FROM BuildTestEvidence
            ORDER BY datetime(GeneratedAtUtc) DESC, Id DESC
            LIMIT 1;
            """;

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadBuildTestEvidence(reader) : null;
    }

    public static IReadOnlyList<ValidationPackageRecord> GetValidationPackages(int limit = 100)
    {
        EnsureInitialized();

        var records = new List<ValidationPackageRecord>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, CreatedAtUtc, PackageId, PackagePath, ManifestPath, AcceptanceStatus,
                   Summary, RunId, OperatorId, AuditEventId
            FROM ValidationPackages
            ORDER BY datetime(CreatedAtUtc) DESC, Id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = command.ExecuteReader();
        while (reader.Read())
            records.Add(ReadValidationPackage(reader));

        return records;
    }

    public static IReadOnlyList<ValidationBreakdownMetric> GetValidationBreakdownMetrics(long runId)
    {
        EnsureInitialized();

        var metrics = new List<ValidationBreakdownMetric>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, RunId, BreakdownType, Key, DisplayName, Total, TruePositive, TrueNegative,
                   FalsePositive, FalseNegative, WrongDefectClass, WrongSide, UnknownGroundTruth,
                   Precision, Recall, FalseCallRate
            FROM ValidationBreakdownMetrics
            WHERE RunId = $runId
            ORDER BY BreakdownType ASC, (FalsePositive + FalseNegative + WrongDefectClass + WrongSide) DESC, Key ASC;
            """;
        command.Parameters.AddWithValue("$runId", runId);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            metrics.Add(new ValidationBreakdownMetric
            {
                Id = reader.GetInt64(0),
                RunId = reader.GetInt64(1),
                BreakdownType = reader.GetString(2),
                Key = reader.GetString(3),
                DisplayName = reader.GetString(4),
                Total = reader.GetInt32(5),
                TruePositive = reader.GetInt32(6),
                TrueNegative = reader.GetInt32(7),
                FalsePositive = reader.GetInt32(8),
                FalseNegative = reader.GetInt32(9),
                WrongDefectClass = reader.GetInt32(10),
                WrongSide = reader.GetInt32(11),
                UnknownGroundTruth = reader.GetInt32(12),
                Precision = reader.GetDouble(13),
                Recall = reader.GetDouble(14),
                FalseCallRate = reader.GetDouble(15),
            });
        }

        return metrics;
    }

}
