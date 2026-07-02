using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public sealed class LearnedVisualModelRegistryEntry
{
    public LearnedVisualModelRegistryEntry(
        LearnedPcbVisualModel model,
        ImageLearningProject? project,
        bool isActive)
    {
        Model = model;
        Project = project;
        IsActive = isActive;
        ModelId = model.ModelId;
        ModelVersion = model.ModelVersion;
        ProjectId = model.ProjectId;
        ProjectName = string.IsNullOrWhiteSpace(project?.ProjectName) ? model.ProjectId : project.ProjectName;
        BoardModel = project?.BoardModel ?? string.Empty;
        CreatedAtUtc = model.CreatedAtUtc;
        ImagesLearnedFrom = model.GoldenCount + model.OkLearningCount;
        OkValidationCount = model.OkValidationCount;
        FalseCallTargetDisplay = model.FalseCallTarget.ToString("P1", CultureInfo.InvariantCulture);
        RecommendedThresholdDisplay = model.LearnedThreshold.ToString("F2", CultureInfo.InvariantCulture);
        EvidenceMode = model.EvidenceMode.ToString();
        ActiveDisplay = isActive ? "Yes" : string.Empty;
        ArtifactStatus = HasRequiredArtifacts(model) ? "Ready" : "Missing artifacts";
    }

    public LearnedPcbVisualModel Model { get; }
    public ImageLearningProject? Project { get; }
    public bool IsActive { get; }
    public string ModelId { get; }
    public string ModelVersion { get; }
    public string ProjectId { get; }
    public string ProjectName { get; }
    public string BoardModel { get; }
    public DateTime CreatedAtUtc { get; }
    public int ImagesLearnedFrom { get; }
    public int OkValidationCount { get; }
    public string FalseCallTargetDisplay { get; }
    public string RecommendedThresholdDisplay { get; }
    public string EvidenceMode { get; }
    public string ActiveDisplay { get; }
    public string ArtifactStatus { get; }

    private static bool HasRequiredArtifacts(LearnedPcbVisualModel model)
        => LearnedVisualModelRegistryService.RequiredArtifactNames.All(name =>
            model.Artifacts.Any(artifact =>
                string.Equals(artifact.ArtifactName, name, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(artifact.ArtifactPath) &&
                File.Exists(artifact.ArtifactPath)));
}

public static class LearnedVisualModelRegistryService
{
    public static readonly string[] RequiredArtifactNames =
    [
        "learned_reference.png",
        "tolerance_map.png",
        "anomaly_threshold_map.png",
    ];

    public static event Action? ActiveModelChanged;

    public static IReadOnlyList<LearnedVisualModelRegistryEntry> ListLearnedModels(int limit = 100)
    {
        var configuration = InspectionModelConfigurationService.Load();
        return AoiDatabase.GetLearnedPcbVisualModels(limit)
            .Select(model => new LearnedVisualModelRegistryEntry(
                model,
                AoiDatabase.GetImageLearningProject(model.ProjectId),
                configuration.IsLearnedVisualModelSelected &&
                string.Equals(configuration.ActiveModelId, model.ModelId, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    public static LearnedVisualModelRegistryEntry? GetActiveLearnedVisualModel()
    {
        var configuration = InspectionModelConfigurationService.Load();
        if (!configuration.IsLearnedVisualModelSelected || string.IsNullOrWhiteSpace(configuration.ActiveModelId))
            return null;

        var model = AoiDatabase.GetLearnedPcbVisualModel(configuration.ActiveModelId);
        if (model is null)
            return null;

        return new LearnedVisualModelRegistryEntry(
            model,
            AoiDatabase.GetImageLearningProject(model.ProjectId),
            isActive: true);
    }

    public static bool HasUsableActiveModel(string? modelId = null)
    {
        var effectiveModelId = string.IsNullOrWhiteSpace(modelId)
            ? InspectionModelConfigurationService.Load().ActiveModelId
            : modelId.Trim();
        if (string.IsNullOrWhiteSpace(effectiveModelId))
            return false;

        var model = AoiDatabase.GetLearnedPcbVisualModel(effectiveModelId);
        return model is not null && HasRequiredArtifacts(model);
    }

    public static LearnedVisualModelRegistryEntry SetActiveLearnedVisualModel(
        string modelId,
        UserRole role,
        string operatorId = "UNKNOWN")
    {
        if (!RoleAuthorization.CanSetActiveLearnedVisualModel(role))
            throw new UnauthorizedAccessException(RoleAuthorization.DeniedMessage(role, "Setting active learned visual model"));

        var model = AoiDatabase.GetLearnedPcbVisualModel(modelId)
            ?? throw new InvalidOperationException("The selected learned PCB visual model was not found.");
        if (!HasRequiredArtifacts(model))
            throw new InvalidOperationException("The selected learned PCB visual model is missing required reference/tolerance artifacts.");

        var existing = InspectionModelConfigurationService.Load();
        existing.SelectedEngineKey = InspectionEngineFactory.LearnedVisualEngineKey;
        existing.ActiveModelId = model.ModelId;
        existing.ActiveModelSha256 = ComputeArtifactFingerprint(model);
        existing.ActiveModelValidationStatus = "ImageOnlyStage1";
        existing.ModelVersion = model.ModelVersion;
        existing.ModelFilePath = ArtifactPath(model, "learned_reference.png");
        existing.LabelMapPath = string.Empty;
        existing.InputImageWidth = model.InputWidth;
        existing.InputImageHeight = model.InputHeight;
        existing.InputTensorName = string.Empty;
        existing.OutputTensorName = string.Empty;
        existing.LastModelCheckTimestampUtc = DateTime.UtcNow;
        existing.LastModelCheckResult = ModelConfigurationTestStatus.Ready;
        existing.LastModelCheckMessage = "Learned PCB Visual Model selected. Image-only Stage 1 learning evidence; not live camera validation.";
        existing.LastModelCheckConfigurationHash = string.Empty;
        InspectionModelConfigurationService.Save(existing);

        var project = AoiDatabase.GetImageLearningProject(model.ProjectId);
        AoiDatabase.RecordAuditEvent(
            "IMAGE_LEARNING_MODEL_SET_ACTIVE",
            $"Learned PCB visual model selected for inspection: model={model.ModelId}; version={model.ModelVersion}; project={model.ProjectId}; evidenceMode={model.EvidenceMode}; stage=ImageOnlyStage1.",
            operatorWithRole: operatorId,
            relatedEntityType: "LearnedPcbVisualModel",
            relatedEntityId: model.ModelId);

        ActiveModelChanged?.Invoke();
        return new LearnedVisualModelRegistryEntry(model, project, isActive: true);
    }

    public static IReadOnlyList<string> BuildEvidenceLines(LearnedPcbVisualModel model)
    {
        var learnedFrom = model.GoldenCount + model.OkLearningCount;
        return
        [
            $"Learned from {learnedFrom} OK sample images.",
            $"False-call target: {model.FalseCallTarget:P1}.",
            $"Recommended threshold: {model.LearnedThreshold:F2}.",
            "Image-only Stage 1 learning; not live camera validation.",
        ];
    }

    public static LearnedVisualModelEvidenceSummary? GetActiveSummary()
        => GetActiveLearnedVisualModel() is { } active
            ? BuildSummary(active.Model, active.Project)
            : null;

    public static LearnedVisualModelEvidenceSummary BuildSummary(
        LearnedPcbVisualModel model,
        ImageLearningProject? project = null,
        string artifactPathPrefix = "")
    {
        string WithPrefix(string path)
            => string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(artifactPathPrefix)
                ? path
                : Path.Combine(artifactPathPrefix, Path.GetFileName(path)).Replace('\\', '/');

        return new LearnedVisualModelEvidenceSummary
        {
            ModelId = model.ModelId,
            ModelVersion = model.ModelVersion,
            CreatedAtUtc = model.CreatedAtUtc,
            ProjectId = model.ProjectId,
            ProjectName = project?.ProjectName ?? string.Empty,
            BoardModel = project?.BoardModel ?? string.Empty,
            EvidenceMode = model.EvidenceMode.ToString(),
            GoldenCount = model.GoldenCount,
            OkLearningCount = model.OkLearningCount,
            OkValidationCount = model.OkValidationCount,
            ImagesLearnedFrom = model.GoldenCount + model.OkLearningCount,
            InputWidth = model.InputWidth,
            InputHeight = model.InputHeight,
            AlignmentMode = model.AlignmentMode,
            BrightnessNormalizationMode = model.BrightnessNormalizationMode,
            LearnedThreshold = model.LearnedThreshold,
            FalseCallTarget = model.FalseCallTarget,
            FalseCallRate = model.FalseCallRate,
            PossibleEscapeRate = model.PossibleEscapeRate,
            LearnedReferenceArtifactPath = WithPrefix(ArtifactPath(model, "learned_reference.png")),
            ToleranceMapArtifactPath = WithPrefix(ArtifactPath(model, "tolerance_map.png")),
            AnomalyThresholdMapArtifactPath = WithPrefix(ArtifactPath(model, "anomaly_threshold_map.png")),
            EvidenceLines = BuildEvidenceLines(model).ToList(),
        };
    }

    public static string ArtifactPath(LearnedPcbVisualModel model, string artifactName)
        => model.Artifacts.FirstOrDefault(artifact =>
            string.Equals(artifact.ArtifactName, artifactName, StringComparison.OrdinalIgnoreCase))?.ArtifactPath
           ?? string.Empty;

    private static bool HasRequiredArtifacts(LearnedPcbVisualModel model)
        => RequiredArtifactNames.All(name =>
            model.Artifacts.Any(artifact =>
                string.Equals(artifact.ArtifactName, name, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(artifact.ArtifactPath) &&
                File.Exists(artifact.ArtifactPath)));

    private static string ComputeArtifactFingerprint(LearnedPcbVisualModel model)
    {
        var text = string.Join(
            "|",
            new[] { model.ModelId, model.ModelVersion }
                .Concat(model.Artifacts
                    .OrderBy(artifact => artifact.ArtifactName, StringComparer.OrdinalIgnoreCase)
                    .Select(artifact => $"{artifact.ArtifactName}:{artifact.Sha256}:{artifact.ArtifactPath}")));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
