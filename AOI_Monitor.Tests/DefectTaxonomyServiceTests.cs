using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class DefectTaxonomyServiceTests : IDisposable
{
    private readonly string _root;

    public DefectTaxonomyServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_Taxonomy_Tests", Guid.NewGuid().ToString("N"));
        AoiDatabase.ConfigureStorageRoot(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException ex)
        {
            System.Diagnostics.Trace.WriteLine($"Taxonomy test cleanup skipped: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            System.Diagnostics.Trace.WriteLine($"Taxonomy test cleanup skipped: {ex.Message}");
        }
    }

    [Fact]
    public void DefaultAliasMapsToCanonicalDefectClass()
    {
        // "Pin Height Error" is now an alias of the first-class Connector Pin Height class.
        var normalized = DefectTaxonomyService.Normalize("Pin Height Error");

        Assert.True(normalized.IsKnown);
        Assert.Equal("Connector Pin Height", normalized.CanonicalClass);
        Assert.Equal("CPH", normalized.MesCode);
    }

    [Fact]
    public void DefaultTaxonomyIncludesMandatoryDefectClasses()
    {
        var classes = DefectTaxonomyService.ActiveCanonicalClasses();

        foreach (var required in new[]
        {
            "Solder Bridge", "Insufficient Solder", "Solder Volume", "Cold Joint",
            "Polarity Error", "Tombstone", "Missing Component", "Misalignment",
            "Height Error", "Connector Pin Height", "3D Coplanarity", "Shield Can Gap",
        })
        {
            Assert.Contains(required, classes);
        }

        // Coplanarity is now its own class rather than an alias of Height Error.
        Assert.Equal("3D Coplanarity", DefectTaxonomyService.Normalize("Coplanarity").CanonicalClass);
    }

    [Fact]
    public void UnknownLabelTriggersTaxonomyWarning()
    {
        var normalized = DefectTaxonomyService.Normalize("Vendor Special Void");

        Assert.False(normalized.IsKnown);
        Assert.Contains("Unknown defect label", normalized.Warning);
    }

    [Fact]
    public void ModelLabelMapMissingRequiredClassBecomesConditional()
    {
        var validation = DefectTaxonomyService.ValidateModelLabels(new Dictionary<int, string>
        {
            [0] = "OK",
            [1] = "Solder Bridge",
        });

        Assert.Equal("CONDITIONAL", validation.Status);
        Assert.Contains("Missing Component", validation.MissingRequiredClasses);
        Assert.Contains(validation.Messages, message => message.Contains("missing required defect class", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MesPayloadUsesConfiguredDefectCode()
    {
        var result = new AnalysisResult
        {
            LotId = "LOT-1",
            BoardProgram = "BOARD-A",
            BoardId = "SN-1",
            Verdict = "NG",
            SuggestedDefect = "Bridge",
            DifferenceScore = 22.5,
            Confidence = 0.91,
        };
        result.Defects.Add(new DefectResult
        {
            DefectType = "Solder Bridge",
            Confidence = 0.91,
            JudgmentStatus = "NG",
        });

        var upload = await TraceabilityUploadService.UploadInspectionResultAsync(result);
        var payload = JsonSerializer.Deserialize<TraceabilityPayload>(File.ReadAllText(upload.PayloadPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(payload);
        Assert.Contains("SB", payload!.DefectCodes);
        Assert.Contains("Solder Bridge(SB)=1", payload.DefectSummary);
    }

    [Fact]
    public void InitializeCreatesDefectTaxonomyTables()
    {
        AoiDatabase.Initialize();

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = AoiDatabase.DatabasePath }.ToString());
        connection.Open();
        Assert.True(AoiDatabase.TableExists(connection, "DefectTaxonomies"));
        Assert.True(AoiDatabase.TableExists(connection, "DefectTaxonomyEntries"));
        Assert.True(AoiDatabase.TableExists(connection, "DefectClassAliases"));
        Assert.True(AoiDatabase.TableExists(connection, "MesDefectCodeMappings"));
    }
}
