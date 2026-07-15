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
    public static long CreateImageLearningProject(ImageLearningProject project)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(project);

        var now = DateTime.UtcNow;
        project.ProjectId = string.IsNullOrWhiteSpace(project.ProjectId)
            ? $"ILP-{now:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6]}"
            : project.ProjectId.Trim();
        project.ProjectName = string.IsNullOrWhiteSpace(project.ProjectName) ? project.ProjectId : project.ProjectName.Trim();
        project.BoardModel = project.BoardModel?.Trim() ?? string.Empty;
        project.Description = project.Description?.Trim() ?? string.Empty;
        project.CreatedBy = string.IsNullOrWhiteSpace(project.CreatedBy) ? AuditOperatorProvider?.Invoke() ?? "UNKNOWN" : project.CreatedBy.Trim();
        project.CreatedAtUtc = project.CreatedAtUtc == default ? now : project.CreatedAtUtc.ToUniversalTime();
        project.UpdatedAtUtc = project.UpdatedAtUtc == default ? project.CreatedAtUtc : project.UpdatedAtUtc.ToUniversalTime();

        RecordAuditEvent(
            "IMAGE_LEARNING_PROJECT_CREATED",
            $"Image-only learning project created: {project.ProjectId}; board={project.BoardModel}; evidenceMode={project.EvidenceMode}.",
            operatorWithRole: project.CreatedBy,
            relatedEntityType: "ImageLearningProject",
            relatedEntityId: project.ProjectId);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO ImageLearningProjects
                (ProjectId, ProjectName, BoardModel, Description, EvidenceMode, CreatedBy,
                 CreatedAtUtc, UpdatedAtUtc, IsArchived, ArchivedBy, ArchivedAtUtc, ArchiveReason)
            VALUES
                ($projectId, $projectName, $boardModel, $description, $evidenceMode, $createdBy,
                 $createdAtUtc, $updatedAtUtc, $isArchived, $archivedBy, $archivedAtUtc, $archiveReason);
            SELECT last_insert_rowid();
            """;
        BindImageLearningProject(command, project);
        project.Id = (long)(command.ExecuteScalar() ?? 0L);
        return project.Id;
    }

    public static ImageLearningProject? GetImageLearningProject(string projectId)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, ProjectId, ProjectName, BoardModel, Description, EvidenceMode, CreatedBy,
                   CreatedAtUtc, UpdatedAtUtc, IsArchived, ArchivedBy, ArchivedAtUtc, ArchiveReason
            FROM ImageLearningProjects
            WHERE ProjectId = $projectId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$projectId", projectId);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadImageLearningProject(reader) : null;
    }

    public static IReadOnlyList<ImageLearningProject> GetImageLearningProjects(bool includeArchived = false)
    {
        EnsureInitialized();

        var projects = new List<ImageLearningProject>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, ProjectId, ProjectName, BoardModel, Description, EvidenceMode, CreatedBy,
                   CreatedAtUtc, UpdatedAtUtc, IsArchived, ArchivedBy, ArchivedAtUtc, ArchiveReason
            FROM ImageLearningProjects
            WHERE $includeArchived = 1 OR IsArchived = 0
            ORDER BY datetime(UpdatedAtUtc) DESC, Id DESC;
            """;
        command.Parameters.AddWithValue("$includeArchived", includeArchived ? 1 : 0);

        using var reader = command.ExecuteReader();
        while (reader.Read())
            projects.Add(ReadImageLearningProject(reader));

        return projects;
    }

    public static void UpdateImageLearningProjectMetadata(
        string projectId,
        string projectName,
        string boardModel,
        string description,
        string updatedBy = "UNKNOWN")
    {
        EnsureInitialized();

        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("Project ID is required.", nameof(projectId));
        var updatedAt = DateTime.UtcNow;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE ImageLearningProjects
            SET ProjectName = $projectName,
                BoardModel = $boardModel,
                Description = $description,
                UpdatedAtUtc = $updatedAtUtc
            WHERE ProjectId = $projectId;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.Trim());
        command.Parameters.AddWithValue("$projectName", string.IsNullOrWhiteSpace(projectName) ? "Image-only PCB learning" : projectName.Trim());
        command.Parameters.AddWithValue("$boardModel", string.IsNullOrWhiteSpace(boardModel) ? "UNKNOWN" : boardModel.Trim());
        command.Parameters.AddWithValue("$description", description?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$updatedAtUtc", updatedAt.ToString("O", CultureInfo.InvariantCulture));
        if (command.ExecuteNonQuery() == 0)
            throw new InvalidOperationException("Image-only learning project does not exist.");

        RecordAuditEvent(
            "IMAGE_LEARNING_PROJECT_METADATA_UPDATED",
            $"Image-only learning project metadata updated: {projectId}; name={projectName}.",
            operatorWithRole: updatedBy,
            relatedEntityType: "ImageLearningProject",
            relatedEntityId: projectId);
    }

    public static void ArchiveImageLearningProject(string projectId, string archivedBy, string reason)
    {
        EnsureInitialized();

        var archivedAt = DateTime.UtcNow;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE ImageLearningProjects
            SET IsArchived = 1,
                ArchivedBy = $archivedBy,
                ArchivedAtUtc = $archivedAtUtc,
                ArchiveReason = $archiveReason,
                UpdatedAtUtc = $archivedAtUtc
            WHERE ProjectId = $projectId;
            """;
        command.Parameters.AddWithValue("$projectId", projectId);
        command.Parameters.AddWithValue("$archivedBy", string.IsNullOrWhiteSpace(archivedBy) ? "UNKNOWN" : archivedBy.Trim());
        command.Parameters.AddWithValue("$archivedAtUtc", archivedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$archiveReason", reason?.Trim() ?? string.Empty);
        command.ExecuteNonQuery();

        RecordAuditEvent(
            "IMAGE_LEARNING_PROJECT_ARCHIVED",
            $"Image-only learning project archived: {projectId}; reason={reason}.",
            operatorWithRole: archivedBy,
            relatedEntityType: "ImageLearningProject",
            relatedEntityId: projectId);
    }

    public static void DeleteImageLearningProjectMetadata(string projectId, string deletedBy, string reason)
    {
        EnsureInitialized();

        RecordAuditEvent(
            "IMAGE_LEARNING_PROJECT_METADATA_DELETE",
            $"Image-only learning project metadata deleted: {projectId}; source customer images were not deleted; reason={reason}.",
            operatorWithRole: deletedBy,
            relatedEntityType: "ImageLearningProject",
            relatedEntityId: projectId);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        ExecuteImageLearningDelete(connection, transaction, "DELETE FROM ImageLearningAnomalyRegions WHERE InspectionResultId IN (SELECT Id FROM ImageLearningInspectionResults WHERE ProjectId = $projectId);", projectId);
        ExecuteImageLearningDelete(connection, transaction, "DELETE FROM ImageLearningInspectionResults WHERE ProjectId = $projectId;", projectId);
        ExecuteImageLearningDelete(connection, transaction, "DELETE FROM ImageLearningComparisonResults WHERE ProjectId = $projectId;", projectId);
        ExecuteImageLearningDelete(connection, transaction, "DELETE FROM ImageLearningCalibrationResults WHERE ProjectId = $projectId;", projectId);
        ExecuteImageLearningDelete(connection, transaction, "DELETE FROM LearnedPcbVisualModelArtifacts WHERE ModelId IN (SELECT ModelId FROM LearnedPcbVisualModels WHERE ProjectId = $projectId);", projectId);
        ExecuteImageLearningDelete(connection, transaction, "DELETE FROM LearnedPcbVisualModels WHERE ProjectId = $projectId;", projectId);
        ExecuteImageLearningDelete(connection, transaction, "DELETE FROM ImageLearningProjectImages WHERE ProjectId = $projectId;", projectId);
        ExecuteImageLearningDelete(connection, transaction, "DELETE FROM ImageLearningProjects WHERE ProjectId = $projectId;", projectId);
        transaction.Commit();
    }

    public static ImageLearningProjectImage InsertImageLearningProjectImage(ImageLearningProjectImage image)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(image);

        image.ImportedAtUtc = image.ImportedAtUtc == default ? DateTime.UtcNow : image.ImportedAtUtc.ToUniversalTime();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO ImageLearningProjectImages
                (ProjectId, Role, OriginalPath, VaultPath, FileName, Sha256, BoardModel, LotId, ViewType,
                 Width, Height, ImportedBy, ImportedAtUtc, ImageLevelTruth, Notes)
            VALUES
                ($projectId, $role, $originalPath, $vaultPath, $fileName, $sha256, $boardModel, $lotId, $viewType,
                 $width, $height, $importedBy, $importedAtUtc, $imageLevelTruth, $notes);
            SELECT last_insert_rowid();
            """;
        BindImageLearningProjectImage(command, image);
        image.Id = (long)(command.ExecuteScalar() ?? 0L);

        RecordAuditEvent(
            "IMAGE_LEARNING_IMAGES_IMPORTED",
            $"Image-only learning image imported: project={image.ProjectId}; role={image.Role}; file={image.FileName}; truth={image.ImageLevelTruth}.",
            operatorWithRole: image.ImportedBy,
            relatedEntityType: "ImageLearningProjectImage",
            relatedEntityId: image.Id.ToString(CultureInfo.InvariantCulture),
            relatedPath: image.VaultPath);
        return image;
    }

    public static ImageLearningProjectImage? GetImageLearningProjectImageByHash(string projectId, string sha256)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, ProjectId, Role, OriginalPath, VaultPath, FileName, Sha256, BoardModel, LotId, ViewType,
                   Width, Height, ImportedBy, ImportedAtUtc, ImageLevelTruth, Notes
            FROM ImageLearningProjectImages
            WHERE ProjectId = $projectId
              AND Sha256 = $sha256
            ORDER BY Id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$projectId", projectId);
        command.Parameters.AddWithValue("$sha256", sha256);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadImageLearningProjectImage(reader) : null;
    }

    public static IReadOnlyList<ImageLearningProjectImage> GetImageLearningProjectImages(string projectId, ImageLearningImageRole? role = null)
    {
        EnsureInitialized();

        var images = new List<ImageLearningProjectImage>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, ProjectId, Role, OriginalPath, VaultPath, FileName, Sha256, BoardModel, LotId, ViewType,
                   Width, Height, ImportedBy, ImportedAtUtc, ImageLevelTruth, Notes
            FROM ImageLearningProjectImages
            WHERE ProjectId = $projectId
              AND ($role = '' OR Role = $role)
            ORDER BY datetime(ImportedAtUtc) DESC, Id DESC;
            """;
        command.Parameters.AddWithValue("$projectId", projectId);
        command.Parameters.AddWithValue("$role", role?.ToString() ?? string.Empty);

        using var reader = command.ExecuteReader();
        while (reader.Read())
            images.Add(ReadImageLearningProjectImage(reader));

        return images;
    }

    public static IReadOnlyDictionary<ImageLearningImageRole, int> GetImageLearningImageCountsByRole(string projectId)
    {
        EnsureInitialized();

        var counts = Enum.GetValues<ImageLearningImageRole>().ToDictionary(role => role, _ => 0);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Role, COUNT(*)
            FROM ImageLearningProjectImages
            WHERE ProjectId = $projectId
            GROUP BY Role;
            """;
        command.Parameters.AddWithValue("$projectId", projectId);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (Enum.TryParse<ImageLearningImageRole>(reader.GetString(0), out var role))
                counts[role] = reader.GetInt32(1);
        }

        return counts;
    }

    public static long RecordLearnedPcbVisualModel(LearnedPcbVisualModel model)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(model);

        var now = DateTime.UtcNow;
        model.ModelId = string.IsNullOrWhiteSpace(model.ModelId)
            ? $"ILM-{now:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6]}"
            : model.ModelId.Trim();
        model.ModelVersion = string.IsNullOrWhiteSpace(model.ModelVersion) ? "1.0.0" : model.ModelVersion.Trim();
        model.CreatedAtUtc = model.CreatedAtUtc == default ? now : model.CreatedAtUtc.ToUniversalTime();
        model.CreatedBy = string.IsNullOrWhiteSpace(model.CreatedBy) ? AuditOperatorProvider?.Invoke() ?? "UNKNOWN" : model.CreatedBy.Trim();

        var auditEventId = RecordAuditEvent(
            "IMAGE_LEARNING_MODEL_CREATED",
            $"Image-only visual model metadata recorded: model={model.ModelId}; version={model.ModelVersion}; project={model.ProjectId}; evidenceMode={model.EvidenceMode}.",
            operatorWithRole: model.CreatedBy,
            relatedEntityType: "LearnedPcbVisualModel",
            relatedEntityId: model.ModelId);
        model.AuditEventId = auditEventId;

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO LearnedPcbVisualModels
                (ModelId, ModelVersion, CreatedAtUtc, ProjectId, GoldenCount, OkLearningCount, OkValidationCount,
                 InputWidth, InputHeight, AlignmentMode, BrightnessNormalizationMode, LearnedThreshold,
                 FalseCallTarget, FalseCallRate, PossibleEscapeRate, EvidenceMode, CreatedBy, AuditEventId)
            VALUES
                ($modelId, $modelVersion, $createdAtUtc, $projectId, $goldenCount, $okLearningCount, $okValidationCount,
                 $inputWidth, $inputHeight, $alignmentMode, $brightnessNormalizationMode, $learnedThreshold,
                 $falseCallTarget, $falseCallRate, $possibleEscapeRate, $evidenceMode, $createdBy, $auditEventId)
            ON CONFLICT(ModelId) DO UPDATE SET
                ModelVersion = excluded.ModelVersion,
                CreatedAtUtc = excluded.CreatedAtUtc,
                ProjectId = excluded.ProjectId,
                GoldenCount = excluded.GoldenCount,
                OkLearningCount = excluded.OkLearningCount,
                OkValidationCount = excluded.OkValidationCount,
                InputWidth = excluded.InputWidth,
                InputHeight = excluded.InputHeight,
                AlignmentMode = excluded.AlignmentMode,
                BrightnessNormalizationMode = excluded.BrightnessNormalizationMode,
                LearnedThreshold = excluded.LearnedThreshold,
                FalseCallTarget = excluded.FalseCallTarget,
                FalseCallRate = excluded.FalseCallRate,
                PossibleEscapeRate = excluded.PossibleEscapeRate,
                EvidenceMode = excluded.EvidenceMode,
                CreatedBy = excluded.CreatedBy,
                AuditEventId = excluded.AuditEventId;
            """;
        BindLearnedPcbVisualModel(command, model);
        command.ExecuteNonQuery();

        using (var idCommand = connection.CreateCommand())
        {
            idCommand.Transaction = transaction;
            idCommand.CommandText = "SELECT Id FROM LearnedPcbVisualModels WHERE ModelId = $modelId LIMIT 1;";
            idCommand.Parameters.AddWithValue("$modelId", model.ModelId);
            model.Id = (long)(idCommand.ExecuteScalar() ?? 0L);
        }

        using (var clearArtifacts = connection.CreateCommand())
        {
            clearArtifacts.Transaction = transaction;
            clearArtifacts.CommandText = "DELETE FROM LearnedPcbVisualModelArtifacts WHERE ModelId = $modelId;";
            clearArtifacts.Parameters.AddWithValue("$modelId", model.ModelId);
            clearArtifacts.ExecuteNonQuery();
        }

        foreach (var artifact in model.Artifacts)
        {
            artifact.ModelId = model.ModelId;
            artifact.CreatedAtUtc = artifact.CreatedAtUtc == default ? model.CreatedAtUtc : artifact.CreatedAtUtc.ToUniversalTime();
            using var artifactCommand = connection.CreateCommand();
            artifactCommand.Transaction = transaction;
            artifactCommand.CommandText =
                """
                INSERT INTO LearnedPcbVisualModelArtifacts
                    (ModelId, ArtifactName, ArtifactPath, Sha256, CreatedAtUtc)
                VALUES
                    ($modelId, $artifactName, $artifactPath, $sha256, $createdAtUtc);
                SELECT last_insert_rowid();
                """;
            BindLearnedPcbVisualModelArtifact(artifactCommand, artifact);
            artifact.Id = (long)(artifactCommand.ExecuteScalar() ?? 0L);
        }

        transaction.Commit();
        return model.Id;
    }

    public static LearnedPcbVisualModel? GetLearnedPcbVisualModel(string modelId)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, ModelId, ModelVersion, CreatedAtUtc, ProjectId, GoldenCount, OkLearningCount, OkValidationCount,
                   InputWidth, InputHeight, AlignmentMode, BrightnessNormalizationMode, LearnedThreshold,
                   FalseCallTarget, FalseCallRate, PossibleEscapeRate, EvidenceMode, CreatedBy, AuditEventId
            FROM LearnedPcbVisualModels
            WHERE ModelId = $modelId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$modelId", modelId);

        using var reader = command.ExecuteReader();
        return reader.Read()
            ? ReadLearnedPcbVisualModel(reader, GetLearnedPcbVisualModelArtifacts(modelId))
            : null;
    }

    public static IReadOnlyList<LearnedPcbVisualModel> GetLearnedPcbVisualModels(int limit = 100)
    {
        EnsureInitialized();

        var models = new List<LearnedPcbVisualModel>();
        using (var connection = OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT Id, ModelId, ModelVersion, CreatedAtUtc, ProjectId, GoldenCount, OkLearningCount, OkValidationCount,
                       InputWidth, InputHeight, AlignmentMode, BrightnessNormalizationMode, LearnedThreshold,
                       FalseCallTarget, FalseCallRate, PossibleEscapeRate, EvidenceMode, CreatedBy, AuditEventId
                FROM LearnedPcbVisualModels
                ORDER BY CreatedAtUtc DESC, Id DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));

            using var reader = command.ExecuteReader();
            while (reader.Read())
                models.Add(ReadLearnedPcbVisualModel(reader, Array.Empty<LearnedPcbVisualModelArtifact>()));
        }

        foreach (var model in models)
            model.Artifacts = GetLearnedPcbVisualModelArtifacts(model.ModelId).ToList();

        return models;
    }

    public static IReadOnlyList<LearnedPcbVisualModel> GetLearnedPcbVisualModelsForProject(string projectId, int limit = 100)
    {
        EnsureInitialized();

        var models = new List<LearnedPcbVisualModel>();
        using (var connection = OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT Id, ModelId, ModelVersion, CreatedAtUtc, ProjectId, GoldenCount, OkLearningCount, OkValidationCount,
                       InputWidth, InputHeight, AlignmentMode, BrightnessNormalizationMode, LearnedThreshold,
                       FalseCallTarget, FalseCallRate, PossibleEscapeRate, EvidenceMode, CreatedBy, AuditEventId
                FROM LearnedPcbVisualModels
                WHERE ProjectId = $projectId
                ORDER BY CreatedAtUtc DESC, Id DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$projectId", projectId);
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));

            using var reader = command.ExecuteReader();
            while (reader.Read())
                models.Add(ReadLearnedPcbVisualModel(reader, Array.Empty<LearnedPcbVisualModelArtifact>()));
        }

        foreach (var model in models)
            model.Artifacts = GetLearnedPcbVisualModelArtifacts(model.ModelId).ToList();

        return models;
    }

    public static int DeleteLearnedPcbVisualModelArtifactRecords(string modelId, IEnumerable<string>? artifactNames = null)
    {
        EnsureInitialized();

        var names = artifactNames?
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<string>();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var deleted = 0;

        if (names.Length == 0)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM LearnedPcbVisualModelArtifacts WHERE ModelId = $modelId;";
            command.Parameters.AddWithValue("$modelId", modelId);
            deleted += command.ExecuteNonQuery();
        }
        else
        {
            foreach (var name in names)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    DELETE FROM LearnedPcbVisualModelArtifacts
                    WHERE ModelId = $modelId
                      AND ArtifactName = $artifactName;
                    """;
                command.Parameters.AddWithValue("$modelId", modelId);
                command.Parameters.AddWithValue("$artifactName", name);
                deleted += command.ExecuteNonQuery();
            }
        }

        transaction.Commit();
        return deleted;
    }

    public static long RecordImageLearningInspectionResult(ImageLearningInspectionResult result)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(result);

        var now = DateTime.UtcNow;
        result.ResultId = string.IsNullOrWhiteSpace(result.ResultId)
            ? $"ILR-{now:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6]}"
            : result.ResultId.Trim();
        result.CreatedAtUtc = result.CreatedAtUtc == default ? now : result.CreatedAtUtc.ToUniversalTime();

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO ImageLearningInspectionResults
                (ResultId, ProjectId, ModelId, ProjectImageId, ImageSha256, ImagePath, CreatedAtUtc,
                 Verdict, AnomalyScore, DecisionReason, OperatorId, EvidenceMode)
            VALUES
                ($resultId, $projectId, $modelId, $projectImageId, $imageSha256, $imagePath, $createdAtUtc,
                 $verdict, $anomalyScore, $decisionReason, $operatorId, $evidenceMode);
            SELECT last_insert_rowid();
            """;
        BindImageLearningInspectionResult(command, result);
        result.Id = (long)(command.ExecuteScalar() ?? 0L);

        foreach (var region in result.AnomalyRegions)
        {
            region.InspectionResultId = result.Id;
            using var regionCommand = connection.CreateCommand();
            regionCommand.Transaction = transaction;
            regionCommand.CommandText =
                """
                INSERT INTO ImageLearningAnomalyRegions
                    (InspectionResultId, RegionId, X, Y, Width, Height, Score, AreaPixels, Confidence, Severity, RegionType, Reason, Notes)
                VALUES
                    ($inspectionResultId, $regionId, $x, $y, $width, $height, $score, $areaPixels, $confidence, $severity, $regionType, $reason, $notes);
                SELECT last_insert_rowid();
                """;
            BindImageLearningAnomalyRegion(regionCommand, region);
            region.Id = (long)(regionCommand.ExecuteScalar() ?? 0L);
        }

        transaction.Commit();
        return result.Id;
    }

    public static ImageLearningInspectionResult? GetImageLearningInspectionResult(long id)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, ResultId, ProjectId, ModelId, ProjectImageId, ImageSha256, ImagePath, CreatedAtUtc,
                   Verdict, AnomalyScore, DecisionReason, OperatorId, EvidenceMode
            FROM ImageLearningInspectionResults
            WHERE Id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        return reader.Read()
            ? ReadImageLearningInspectionResult(reader, GetImageLearningAnomalyRegions(id))
            : null;
    }

    public static IReadOnlyList<ImageLearningInspectionResult> GetImageLearningInspectionResults(
        string projectId,
        string? modelId = null,
        int limit = 1000)
    {
        EnsureInitialized();

        var results = new List<ImageLearningInspectionResult>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        if (string.IsNullOrWhiteSpace(modelId))
        {
            command.CommandText =
                """
                SELECT Id, ResultId, ProjectId, ModelId, ProjectImageId, ImageSha256, ImagePath, CreatedAtUtc,
                       Verdict, AnomalyScore, DecisionReason, OperatorId, EvidenceMode
                FROM ImageLearningInspectionResults
                WHERE ProjectId = $projectId
                ORDER BY CreatedAtUtc DESC, Id DESC
                LIMIT $limit;
                """;
        }
        else
        {
            command.CommandText =
                """
                SELECT Id, ResultId, ProjectId, ModelId, ProjectImageId, ImageSha256, ImagePath, CreatedAtUtc,
                       Verdict, AnomalyScore, DecisionReason, OperatorId, EvidenceMode
                FROM ImageLearningInspectionResults
                WHERE ProjectId = $projectId AND ModelId = $modelId
                ORDER BY CreatedAtUtc DESC, Id DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$modelId", modelId.Trim());
        }

        command.Parameters.AddWithValue("$projectId", projectId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 10000));

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            results.Add(ReadImageLearningInspectionResult(reader, GetImageLearningAnomalyRegions(id)));
        }

        return results;
    }

    public static long RecordImageLearningCalibrationResult(ImageLearningCalibrationResult result)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(result);

        var now = DateTime.UtcNow;
        result.CalibrationId = string.IsNullOrWhiteSpace(result.CalibrationId)
            ? $"ILC-{now:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6]}"
            : result.CalibrationId.Trim();
        result.CreatedAtUtc = result.CreatedAtUtc == default ? now : result.CreatedAtUtc.ToUniversalTime();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO ImageLearningCalibrationResults
                (CalibrationId, ProjectId, ModelId, CreatedAtUtc, OkValidationCount, NgValidationCount,
                 LearnedThreshold, FalseCallTarget, FalseCallRate, PossibleEscapeRate, Status, Summary,
                 HeldOutOkCount, HeldOutFalseCalls, HeldOutFalseCallRate)
            VALUES
                ($calibrationId, $projectId, $modelId, $createdAtUtc, $okValidationCount, $ngValidationCount,
                 $learnedThreshold, $falseCallTarget, $falseCallRate, $possibleEscapeRate, $status, $summary,
                 $heldOutOkCount, $heldOutFalseCalls, $heldOutFalseCallRate);
            SELECT last_insert_rowid();
            """;
        BindImageLearningCalibrationResult(command, result);
        result.Id = (long)(command.ExecuteScalar() ?? 0L);
        return result.Id;
    }

    public static ImageLearningCalibrationResult? GetImageLearningCalibrationResult(string calibrationId)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, CalibrationId, ProjectId, ModelId, CreatedAtUtc, OkValidationCount, NgValidationCount,
                   LearnedThreshold, FalseCallTarget, FalseCallRate, PossibleEscapeRate, Status, Summary,
                   HeldOutOkCount, HeldOutFalseCalls, HeldOutFalseCallRate
            FROM ImageLearningCalibrationResults
            WHERE CalibrationId = $calibrationId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$calibrationId", calibrationId);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadImageLearningCalibrationResult(reader) : null;
    }

    public static IReadOnlyList<ImageLearningCalibrationResult> GetImageLearningCalibrationResults(
        string? projectId = null,
        string? modelId = null,
        int limit = 100)
    {
        EnsureInitialized();

        var results = new List<ImageLearningCalibrationResult>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, CalibrationId, ProjectId, ModelId, CreatedAtUtc, OkValidationCount, NgValidationCount,
                   LearnedThreshold, FalseCallTarget, FalseCallRate, PossibleEscapeRate, Status, Summary,
                   HeldOutOkCount, HeldOutFalseCalls, HeldOutFalseCallRate
            FROM ImageLearningCalibrationResults
            WHERE ($projectId = '' OR ProjectId = $projectId)
              AND ($modelId = '' OR ModelId = $modelId)
            ORDER BY datetime(CreatedAtUtc) DESC, Id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$projectId", string.IsNullOrWhiteSpace(projectId) ? string.Empty : projectId.Trim());
        command.Parameters.AddWithValue("$modelId", string.IsNullOrWhiteSpace(modelId) ? string.Empty : modelId.Trim());
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));

        using var reader = command.ExecuteReader();
        while (reader.Read())
            results.Add(ReadImageLearningCalibrationResult(reader));

        return results;
    }

    public static long RecordImageLearningComparisonResult(ImageLearningComparisonResult result)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(result);

        var now = DateTime.UtcNow;
        result.ComparisonId = string.IsNullOrWhiteSpace(result.ComparisonId)
            ? $"ILCMP-{now:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6]}"
            : result.ComparisonId.Trim();
        result.CreatedAtUtc = result.CreatedAtUtc == default ? now : result.CreatedAtUtc.ToUniversalTime();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO ImageLearningComparisonResults
                (ComparisonId, ProjectId, ModelId, ProjectImageId, ImageSha256, CreatedAtUtc,
                 DifferenceScore, AnomalyScore, Verdict, Summary)
            VALUES
                ($comparisonId, $projectId, $modelId, $projectImageId, $imageSha256, $createdAtUtc,
                 $differenceScore, $anomalyScore, $verdict, $summary);
            SELECT last_insert_rowid();
            """;
        BindImageLearningComparisonResult(command, result);
        result.Id = (long)(command.ExecuteScalar() ?? 0L);
        return result.Id;
    }

    public static ImageLearningComparisonResult? GetImageLearningComparisonResult(string comparisonId)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, ComparisonId, ProjectId, ModelId, ProjectImageId, ImageSha256, CreatedAtUtc,
                   DifferenceScore, AnomalyScore, Verdict, Summary
            FROM ImageLearningComparisonResults
            WHERE ComparisonId = $comparisonId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$comparisonId", comparisonId);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadImageLearningComparisonResult(reader) : null;
    }

    public static long RecordValidationPackage(
        string packageId,
        string packagePath,
        string manifestPath,
        string acceptanceStatus,
        string summary,
        long? runId = null,
        string? operatorId = null)
    {
        EnsureInitialized();

        var effectiveOperator = string.IsNullOrWhiteSpace(operatorId) ? AuditOperatorProvider?.Invoke() ?? "UNKNOWN" : operatorId;
        var auditEventId = RecordAuditEvent(
            "EXPORT",
            $"Stage 1 validation package recorded: {packageId}; status={acceptanceStatus}; manifest={manifestPath}.",
            operatorWithRole: effectiveOperator,
            relatedEntityType: "ValidationPackage",
            relatedEntityId: packageId,
            relatedPath: manifestPath);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO ValidationPackages
                (PackageId, PackagePath, ManifestPath, AcceptanceStatus, Summary, RunId, OperatorId, AuditEventId, CreatedAtUtc)
            VALUES
                ($packageId, $packagePath, $manifestPath, $acceptanceStatus, $summary, $runId, $operatorId, $auditEventId, $createdAtUtc);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$packageId", packageId);
        command.Parameters.AddWithValue("$packagePath", packagePath);
        command.Parameters.AddWithValue("$manifestPath", manifestPath);
        command.Parameters.AddWithValue("$acceptanceStatus", acceptanceStatus);
        command.Parameters.AddWithValue("$summary", summary);
        command.Parameters.AddWithValue("$runId", runId is { } id ? (object)id : DBNull.Value);
        command.Parameters.AddWithValue("$operatorId", effectiveOperator);
        command.Parameters.AddWithValue("$auditEventId", auditEventId);
        command.Parameters.AddWithValue("$createdAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        return (long)(command.ExecuteScalar() ?? 0L);
    }

}
