using System.Globalization;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class ModelLifecycleService
{
    public static ModelConfigurationTestResult ValidateRuntime(string modelId, UserRole role, string operatorId)
    {
        if (!RoleAuthorization.CanTestModelConfiguration(role))
            throw new UnauthorizedAccessException(RoleAuthorization.DeniedMessage(role, "validating model runtime"));

        var result = ModelRegistryService.Validate(modelId);
        var state = result.Status == ModelConfigurationTestStatus.Ready
            ? ModelLifecycleState.RuntimeValidated
            : ModelLifecycleState.Registered;
        Transition(modelId, state, operatorId, $"Runtime validation result={result.DisplayStatus}. {result.Message}");
        return result;
    }

    public static void RecordAcceptanceResult(ModelAcceptanceRun run, string operatorId)
    {
        if (string.IsNullOrWhiteSpace(run.ModelId))
            return;

        var state = run.Status.ToUpperInvariant() switch
        {
            "PASS" => ModelLifecycleState.AcceptancePassed,
            "CONDITIONAL" => ModelLifecycleState.AcceptanceConditional,
            _ => ModelLifecycleState.AcceptanceFailed,
        };
        Transition(
            run.ModelId,
            state,
            operatorId,
            $"Model acceptance run {run.Id} status={run.Status}.",
            latestAcceptanceStatus: run.Status);
    }

    public static void RecordReleasePackage(ModelReleasePackageRecord package, string operatorId)
    {
        if (string.IsNullOrWhiteSpace(package.ModelId))
            return;

        var model = ModelRegistryService.GetModel(package.ModelId);
        if (model is null)
            return;

        Transition(
            package.ModelId,
            model.LifecycleState,
            operatorId,
            $"Model release package {package.Id} recorded at {package.PackagePath}.",
            latestAcceptanceStatus: package.Status,
            latestReleasePackagePath: package.PackagePath);
    }

    public static void PromoteProductionCandidate(long acceptanceRunId, UserRole role, string approvedBy)
    {
        if (!RoleAuthorization.CanChangeThresholds(role))
            throw new UnauthorizedAccessException(RoleAuthorization.DeniedMessage(role, "promoting production candidate models"));

        var run = AoiDatabase.GetLatestModelAcceptanceRun();
        if (run is null || run.Id != acceptanceRunId)
            throw new InvalidOperationException("Model acceptance run was not found.");
        if (!string.Equals(run.Status, "PASS", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only PASS model acceptance runs can be promoted to production candidate.");
        if (string.IsNullOrWhiteSpace(run.ModelId) || ModelRegistryService.GetModel(run.ModelId) is null)
            throw new InvalidOperationException("Accepted model registry entry was not found.");

        AoiDatabase.PromoteModelAcceptanceRun(run.Id, approvedBy);
        Transition(
            run.ModelId,
            ModelLifecycleState.ProductionCandidate,
            approvedBy,
            $"Promoted model acceptance run {run.Id} to production candidate.",
            latestAcceptanceStatus: run.Status);
    }

    public static void DeployModel(string modelId, UserRole role, string approvedBy, string? waiverReason = null)
    {
        if (!RoleAuthorization.CanManageSettings(role))
            throw new UnauthorizedAccessException(RoleAuthorization.DeniedMessage(role, "deploying model lifecycle state"));

        var model = ModelRegistryService.GetModel(modelId) ?? throw new InvalidOperationException("Model registry entry was not found.");
        if (model.LifecycleState == ModelLifecycleState.Retired)
            throw new InvalidOperationException("Retired models cannot be deployed.");

        var latestAcceptance = AoiDatabase.GetLatestModelAcceptanceRun(model.ModelId);
        var hasPassingAcceptance = latestAcceptance is not null &&
            string.Equals(latestAcceptance.Status, "PASS", StringComparison.OrdinalIgnoreCase);
        var hasWaiver = !string.IsNullOrWhiteSpace(waiverReason);
        if (!hasPassingAcceptance && !hasWaiver)
            throw new InvalidOperationException("Model deployment requires PASS model acceptance or an Admin deployment waiver reason.");

        var normalizedWaiver = hasWaiver ? waiverReason!.Trim() : string.Empty;
        var now = DateTime.UtcNow;
        AoiDatabase.UpdateModelLifecycle(
            model.ModelId,
            ModelLifecycleState.Deployed,
            latestAcceptanceStatus: latestAcceptance?.Status ?? model.LatestAcceptanceStatus,
            deploymentWaiverReason: normalizedWaiver,
            deploymentWaivedBy: hasWaiver ? approvedBy : string.Empty,
            deploymentWaivedAtUtc: hasWaiver ? now : null,
            isActive: true,
            replaceDeploymentWaiver: true);
        ModelRegistryService.SetActiveModel(model.ModelId);
        AoiDatabase.RecordAuditEvent(
            hasWaiver ? "MODEL_DEPLOYMENT_WAIVER" : "MODEL_DEPLOYMENT",
            hasWaiver
                ? $"Deployed model {model.ModelId} with Admin waiver. Reason={normalizedWaiver}"
                : $"Deployed model {model.ModelId} with PASS acceptance run {latestAcceptance?.Id.ToString(CultureInfo.InvariantCulture) ?? "unknown"}.",
            operatorWithRole: approvedBy,
            relatedEntityType: "ModelRegistry",
            relatedEntityId: model.ModelId,
            relatedPath: model.MetadataPath);
        ModelRegistryService.NotifyRegistryChanged();
    }

    public static void RetireModel(string modelId, UserRole role, string approvedBy, string reason)
    {
        if (!RoleAuthorization.CanManageSettings(role))
            throw new UnauthorizedAccessException(RoleAuthorization.DeniedMessage(role, "retiring model lifecycle state"));
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Retirement reason is required.", nameof(reason));

        var model = ModelRegistryService.GetModel(modelId) ?? throw new InvalidOperationException("Model registry entry was not found.");
        AoiDatabase.UpdateModelLifecycle(
            model.ModelId,
            ModelLifecycleState.Retired,
            retiredReason: reason.Trim(),
            retiredAtUtc: DateTime.UtcNow,
            isActive: false);
        if (model.IsActive)
            InspectionModelConfigurationService.Save(new InspectionModelConfiguration());

        AoiDatabase.RecordAuditEvent(
            "MODEL_RETIRED",
            $"Retired model {model.ModelId}. Reason={reason.Trim()}",
            operatorWithRole: approvedBy,
            relatedEntityType: "ModelRegistry",
            relatedEntityId: model.ModelId,
            relatedPath: model.MetadataPath);
        ModelRegistryService.NotifyRegistryChanged();
        InspectionModelConfigurationService.NotifyExternalConfigurationChanged();
    }

    private static void Transition(
        string modelId,
        ModelLifecycleState state,
        string operatorId,
        string message,
        string latestAcceptanceStatus = "",
        string latestReleasePackagePath = "")
    {
        AoiDatabase.UpdateModelLifecycle(
            modelId,
            state,
            latestAcceptanceStatus: latestAcceptanceStatus,
            latestReleasePackagePath: latestReleasePackagePath);
        AoiDatabase.RecordAuditEvent(
            "MODEL_LIFECYCLE",
            $"Model {modelId} lifecycle -> {state}. {message}",
            operatorWithRole: string.IsNullOrWhiteSpace(operatorId) ? "UNKNOWN" : operatorId,
            relatedEntityType: "ModelRegistry",
            relatedEntityId: modelId);
        ModelRegistryService.NotifyRegistryChanged();
    }
}
