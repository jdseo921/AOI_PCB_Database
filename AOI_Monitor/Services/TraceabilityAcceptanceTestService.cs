using System.IO;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

/// <summary>
/// Stage 4 MES/ERP acceptance harness entry point.
/// This wraps the existing traceability signoff implementation so future REST,
/// OPC UA, or customer-specific adapters can hang off one production-review name.
/// </summary>
public static class TraceabilityAcceptanceTestService
{
    private static readonly System.Text.Json.JsonSerializerOptions ContractJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
    };

    public static async Task<TraceabilityTestReport> RunAsync(
        string? testImagePath = null,
        bool productionModeConfirmed = false,
        string? operatorId = null,
        CancellationToken cancellationToken = default)
        => await TraceabilitySignoffService.RunAsync(
            testImagePath,
            productionModeConfirmed,
            operatorId,
            cancellationToken).ConfigureAwait(false);

    public static Task<TraceabilityTestReport> RunResultOnlyAsync(
        string? operatorId = null,
        CancellationToken cancellationToken = default)
        => RunAsync(testImagePath: null, productionModeConfirmed: false, operatorId, cancellationToken);

    public static Task<TraceabilityTestReport> RunResultAndImageAsync(
        string testImagePath,
        bool productionModeConfirmed,
        string? operatorId = null,
        CancellationToken cancellationToken = default)
        => RunAsync(testImagePath, productionModeConfirmed, operatorId, cancellationToken);

    public static MesEndpointContractExport ExportEndpointContracts(string? outputRoot = null)
    {
        var root = string.IsNullOrWhiteSpace(outputRoot)
            ? Path.Combine(Data.AoiDatabase.StorageRoot, "exports", "mes_contract")
            : outputRoot.Trim();
        Directory.CreateDirectory(root);

        var payloadContractPath = Path.Combine(root, "mes_payload_contract.json");
        var responseContractPath = Path.Combine(root, "mes_response_contract.json");
        var exampleResultPath = Path.Combine(root, "example_result_payload.json");
        var exampleImagePath = Path.Combine(root, "example_image_upload_metadata.json");

        File.WriteAllText(payloadContractPath, System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = "string required, currently 1.0.0",
            integrationMode = "string required",
            lotId = "string required",
            boardModel = "string required",
            serialNumber = "string optional",
            stationId = "string required",
            operatorId = "string required",
            result = "string required: OK, NG, REVIEW, or site-defined code",
            timestampUtc = "ISO-8601 UTC timestamp required",
            defectSummary = "string required",
            defectCodes = "array of strings; MES defect codes mapped from the defect taxonomy",
            imagePath = "string optional; local path is not sent unless customer adapter maps it",
            overlayPath = "string optional",
            inspectionEngine = "string required",
            modelVersion = "string required",
            confidence = "number optional",
            score = "number optional",
            sourceNotice = "string required; must distinguish mock/simulation from production writeback",
        }, ContractJsonOptions));
        File.WriteAllText(responseContractPath, System.Text.Json.JsonSerializer.Serialize(new
        {
            accepted = "boolean required; true only when MES accepted the payload",
            message = "string required; human-readable status",
            externalInspectionId = "string required; MES-side inspection/result identifier",
            timestampUtc = "ISO-8601 UTC timestamp required",
            resultCode = "string required; MES-side result/status code",
        }, ContractJsonOptions));
        File.WriteAllText(exampleResultPath, System.Text.Json.JsonSerializer.Serialize(new TraceabilityPayload
        {
            IntegrationMode = "REST",
            LotId = "LOT-20260621-A",
            BoardModel = "TBOX-MAIN",
            SerialNumber = "PCB-000123",
            StationId = "AOI-LINE-01",
            OperatorId = "Engineer01 [Engineer]",
            Result = "NG",
            TimestampUtc = DateTime.Parse("2026-06-21T10:15:30Z").ToUniversalTime(),
            DefectSummary = "SolderBridge=1; MissingComponent=0",
            InspectionEngine = "CustomerONNX",
            ModelVersion = "MODEL-1.2.3",
            Confidence = 0.97,
            Score = 87.4,
            SourceNotice = "Example contract payload. No secret values are included.",
        }, ContractJsonOptions));
        File.WriteAllText(exampleImagePath, System.Text.Json.JsonSerializer.Serialize(new
        {
            inspectionId = "MES-INSPECTION-12345",
            boardId = "PCB-000123",
            imageType = "annotated-overlay",
            timestampUtc = "2026-06-21T10:15:31Z",
            fileField = "file",
            contentType = "image/png or image/jpeg",
            note = "Image upload uses multipart/form-data. Secrets must be supplied only via configured auth headers and never placed in payload JSON.",
        }, ContractJsonOptions));

        ExportVerificationService.RecordVerifiedExport("MesEndpointPayloadContract", payloadContractPath);
        ExportVerificationService.RecordVerifiedExport("MesEndpointResponseContract", responseContractPath);
        ExportVerificationService.RecordVerifiedExport("MesEndpointExampleResultPayload", exampleResultPath);
        ExportVerificationService.RecordVerifiedExport("MesEndpointExampleImageMetadata", exampleImagePath);
        return new MesEndpointContractExport(payloadContractPath, responseContractPath, exampleResultPath, exampleImagePath);
    }
}

public sealed record MesEndpointContractExport(
    string PayloadContractPath,
    string ResponseContractPath,
    string ExampleResultPayloadPath,
    string ExampleImageUploadMetadataPath);
