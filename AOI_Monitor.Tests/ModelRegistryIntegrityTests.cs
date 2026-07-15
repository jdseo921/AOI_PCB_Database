using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

/// <summary>
/// Verifies that a registered ONNX model whose stored artifact is altered after
/// registration cannot be activated. This exercises the activation-time SHA-256
/// re-verification required by the AOI Software Architecture, Secure Development,
/// and Change-Control Standard (Docs/standard, §29/§31 model-artifact integrity):
/// the registered hash is captured once at import, so activation must recompute
/// and compare it to catch tampering or corruption before a bad model becomes the
/// inference source.
/// </summary>
public sealed class ModelRegistryIntegrityTests : IDisposable
{
    private readonly string _root;

    /// <summary>Configures an isolated storage root and initializes the database for each test.</summary>
    public ModelRegistryIntegrityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_ModelIntegrity_Tests", Guid.NewGuid().ToString("N"));
        AoiDatabase.ConfigureStorageRoot(_root);
        AoiDatabase.AuditOperatorProvider = null;
        AoiDatabase.AuditUserIdProvider = null;
        AoiDatabase.AuditUserRoleProvider = null;
        AoiDatabase.AuditStationProvider = null;
        AoiDatabase.Initialize();
        InspectionModelConfigurationService.Save(new InspectionModelConfiguration());
    }

    /// <summary>Cleans up global state and the temporary storage root.</summary>
    public void Dispose()
    {
        InspectionModelConfigurationService.Save(new InspectionModelConfiguration());
        AoiDatabase.AuditOperatorProvider = null;
        AoiDatabase.AuditUserIdProvider = null;
        AoiDatabase.AuditUserRoleProvider = null;
        AoiDatabase.AuditStationProvider = null;
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Registers a model from a source file and returns its registry entry.</summary>
    private static ModelRegistryEntry RegisterModel(string sourcePath, byte[] content)
    {
        File.WriteAllBytes(sourcePath, content);
        return ModelRegistryService.Register(new ModelRegistrationRequest
        {
            ModelFilePath = sourcePath,
            DisplayName = "IntegrityTestModel",
            Version = "v1",
            InputTensorName = "input",
            OutputTensorName = "output",
        });
    }

    [Fact]
    public void SetActiveModelSucceedsWhenStoredArtifactMatchesRegisteredHash()
    {
        var source = Path.Combine(_root, "model_source.onnx");
        var entry = RegisterModel(source, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        // The stored copy is untouched, so its hash matches the registered SHA-256.
        var activated = ModelRegistryService.SetActiveModel(entry.ModelId);

        Assert.True(activated);
        Assert.Equal(entry.ModelId, ModelRegistryService.GetActiveModel()?.ModelId);
    }

    [Fact]
    public void SetActiveModelRefusesWhenStoredArtifactWasTampered()
    {
        var source = Path.Combine(_root, "model_source.onnx");
        var entry = RegisterModel(source, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        // Tamper the stored artifact after registration so its bytes no longer match
        // the SHA-256 recorded at import time.
        File.WriteAllBytes(entry.StoredModelPath, new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 });

        var failure = Assert.Throws<InvalidOperationException>(() => ModelRegistryService.SetActiveModel(entry.ModelId));
        Assert.Contains("integrity", failure.Message, StringComparison.OrdinalIgnoreCase);

        // The tampered model must NOT have become active, and the refusal must be audited.
        Assert.NotEqual(entry.ModelId, ModelRegistryService.GetActiveModel()?.ModelId);
        var audits = AoiDatabase.GetAuditEvents(new LogFilter());
        Assert.Contains(audits, a => a.ActionCategory == "MODEL_INTEGRITY");
    }

    [Fact]
    public void SetActiveModelRefusesWhenStoredArtifactIsMissing()
    {
        var source = Path.Combine(_root, "model_source.onnx");
        var entry = RegisterModel(source, new byte[] { 1, 2, 3, 4 });

        File.Delete(entry.StoredModelPath);

        Assert.Throws<FileNotFoundException>(() => ModelRegistryService.SetActiveModel(entry.ModelId));
        Assert.NotEqual(entry.ModelId, ModelRegistryService.GetActiveModel()?.ModelId);
    }
}
