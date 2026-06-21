using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class SupportBundleServiceTests : IDisposable
{
    private readonly string _root;

    public SupportBundleServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_SupportBundle_Tests", Guid.NewGuid().ToString("N"));
        AoiDatabase.ConfigureStorageRoot(_root);
        AoiDatabase.Initialize();
        RecipeService.Invalidate();
        FirstRunSettingsService.ResetForTests();
        CentralSyncSettingsService.ResetForTests();
        MesIntegrationSettingsService.Save(new MesIntegrationSettings());
        CentralSyncSettingsService.Save(new CentralSyncSettings());
    }

    public void Dispose()
    {
        RecipeService.Invalidate();
        FirstRunSettingsService.ResetForTests();
        CentralSyncSettingsService.ResetForTests();
        StorageRootSettingsService.ConfigureSettingsDirectoryForTests(null);
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
    public void BundleExcludesSecrets()
    {
        const string mesSecret = "support-mes-api-key-secret";
        const string centralSecret = "support-central-shared-secret";
        MesIntegrationSettingsService.Save(new MesIntegrationSettings
        {
            Mode = MesIntegrationMode.Rest,
            BaseUrl = "https://mes.customer.example",
            UploadResultPath = "/api/aoi/results",
            AuthMode = MesRestAuthMode.ApiKey,
            ApiKeyHeaderName = "X-Support-Key",
            ApiKey = mesSecret,
        });
        CentralSyncSettingsService.Save(new CentralSyncSettings
        {
            Mode = CentralSyncMode.RestApi,
            EndpointOrFolder = "https://central.customer.example/api/sync",
            SharedSecret = centralSecret,
        });
        AoiDatabase.RecordAuditEvent("SUPPORT_SECRET_TEST", $"token={mesSecret}; sharedSecret={centralSecret}", operatorWithRole: "Admin01 [Admin]");

        var result = SupportBundleService.Export(new SupportBundleOptions
        {
            OutputRoot = Path.Combine(_root, "support"),
        }, "Admin01 [Admin]");

        var text = ReadAllTextEntries(result.ZipPath);
        Assert.DoesNotContain(mesSecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(centralSecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretProtectionService.ProtectedPrefix, text, StringComparison.Ordinal);
        Assert.Contains("REDACTED", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BundleExcludesImagesAndCustomerImagePathsByDefault()
    {
        Directory.CreateDirectory(AoiDatabase.ImageVaultPath);
        var imagePath = Path.Combine(AoiDatabase.ImageVaultPath, "customer_board_001.png");
        File.WriteAllBytes(imagePath, [1, 2, 3, 4]);
        AoiDatabase.RecordAuditEvent(
            "SUPPORT_IMAGE_TEST",
            $"Customer image failed review: {imagePath}",
            operatorWithRole: "Admin01 [Admin]",
            relatedPath: imagePath);

        var result = SupportBundleService.Export(new SupportBundleOptions
        {
            OutputRoot = Path.Combine(_root, "support"),
        }, "Admin01 [Admin]");

        using var archive = ZipFile.OpenRead(result.ZipPath);
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
        var text = ReadAllTextEntries(archive);
        Assert.DoesNotContain("customer_board_001.png", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(imagePath, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED_IMAGE_PATH]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ManifestListsIncludedFilesAndChecksums()
    {
        var result = SupportBundleService.Export(new SupportBundleOptions
        {
            OutputRoot = Path.Combine(_root, "support"),
        }, "Admin01 [Admin]");

        using var archive = ZipFile.OpenRead(result.ZipPath);
        Assert.NotNull(archive.GetEntry("support_bundle_manifest.json"));
        Assert.NotNull(archive.GetEntry("support_summary.html"));
        Assert.NotNull(archive.GetEntry("readiness_summary.json"));

        var manifest = ReadJsonEntry<SupportBundleManifest>(archive, "support_bundle_manifest.json");
        Assert.Contains(manifest.IncludedFiles, file => file.RelativePath == "support_summary.html");
        Assert.Contains(manifest.IncludedFiles, file => file.RelativePath == "readiness_summary.json");
        Assert.DoesNotContain(manifest.IncludedFiles, file => file.RelativePath == "support_bundle_manifest.json");
        Assert.All(manifest.IncludedFiles, file =>
        {
            Assert.True(file.Bytes > 0);
            Assert.Matches("^[0-9a-f]{64}$", file.Sha256);
        });

        var summaryEntry = Assert.Single(manifest.IncludedFiles, file => file.RelativePath == "support_summary.html");
        Assert.Equal(summaryEntry.Sha256, Sha256(archive.GetEntry("support_summary.html")!));
    }

    [Fact]
    public void ReadinessSummaryIsIncluded()
    {
        BuildTestEvidenceService.CreateLocalEvidence(operatorId: "Admin01 [Admin]");

        var result = SupportBundleService.Export(new SupportBundleOptions
        {
            OutputRoot = Path.Combine(_root, "support"),
        }, "Admin01 [Admin]");

        using var archive = ZipFile.OpenRead(result.ZipPath);
        var readiness = ReadTextEntry(archive, "readiness_summary.json");
        Assert.Contains("\"overallStatus\"", readiness, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"categories\"", readiness, StringComparison.OrdinalIgnoreCase);
    }

    private static T ReadJsonEntry<T>(ZipArchive archive, string entryName)
    {
        var json = ReadTextEntry(archive, entryName);
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? throw new InvalidOperationException($"Could not deserialize {entryName}.");
    }

    private static string ReadAllTextEntries(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        return ReadAllTextEntries(archive);
    }

    private static string ReadAllTextEntries(ZipArchive archive)
    {
        var builder = new StringBuilder();
        foreach (var entry in archive.Entries.Where(entry =>
                     entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                     entry.FullName.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                     entry.FullName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
        {
            builder.AppendLine(ReadTextEntry(archive, entry.FullName));
        }

        return builder.ToString();
    }

    private static string ReadTextEntry(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName) ?? throw new FileNotFoundException(entryName);
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string Sha256(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
