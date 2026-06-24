using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class CustomerDatasetPreflightServiceTests : IDisposable
{
    private readonly string _root;

    public CustomerDatasetPreflightServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_CustomerDatasetPreflight_Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
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
    public void MissingImageFailsPreflight()
    {
        var dataset = CreateDataset();
        WriteGolden(dataset, "golden/ref.png");
        var manifest = WriteManifest(dataset,
            Row("images/missing.png", "NG", "golden/ref.png", "solder_bridge"));

        var result = CustomerDatasetPreflightService.Validate(dataset, manifest, RelaxedBalance());

        Assert.Equal("FAIL", result.Status);
        Assert.Equal(1, result.MissingImageCount);
        Assert.Contains(result.BlockingFailures, failure => failure.Contains("Image file is missing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MissingGoldenFailsOrWarnsBasedOnCriteria()
    {
        var dataset = CreateDataset();
        WriteImage(dataset, "images/board_0001.png");
        var manifest = WriteManifest(dataset,
            Row("images/board_0001.png", "NG", "golden/missing.png", "solder_bridge"));

        var fail = CustomerDatasetPreflightService.Validate(dataset, manifest, RelaxedBalance());
        var warn = CustomerDatasetPreflightService.Validate(dataset, manifest, new CustomerDatasetPreflightCriteria
        {
            TreatMissingGoldenAsFailure = false,
            DatasetQualityCriteria = new ValidationDatasetQualityCriteria
            {
                MinimumTotalImages = 1,
                MinimumKnownGroundTruthImages = 1,
                MinimumOkImages = 0,
                MinimumNgImages = 1,
                MinimumDefectClasses = 1,
                MinimumImagesPerDefectClass = 1,
                RequireGoldenImageForPixelDifference = false,
            },
        });

        Assert.Equal("FAIL", fail.Status);
        Assert.Contains(fail.BlockingFailures, failure => failure.Contains("Golden image is missing", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("CONDITIONAL", warn.Status);
        Assert.Contains(warn.Warnings, warning => warning.Contains("Golden image is missing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AllOkOrAllNgDatasetFailsAcceptancePreflight()
    {
        var allOk = CreateDataset("all-ok");
        var allOkManifest = WriteBalancedManifest(allOk, okCount: 50, bridgeCount: 0, missingCount: 0);
        var allNg = CreateDataset("all-ng");
        var allNgManifest = WriteBalancedManifest(allNg, okCount: 0, bridgeCount: 25, missingCount: 25);

        var okResult = CustomerDatasetPreflightService.Validate(allOk, allOkManifest);
        var ngResult = CustomerDatasetPreflightService.Validate(allNg, allNgManifest);

        Assert.Equal("FAIL", okResult.Status);
        Assert.Contains(okResult.BlockingFailures, failure => failure.Contains("NG image", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("FAIL", ngResult.Status);
        Assert.Contains(ngResult.BlockingFailures, failure => failure.Contains("OK image", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GoodSyntheticDatasetPasses()
    {
        var dataset = CreateDataset();
        var manifest = WriteBalancedManifest(dataset, okCount: 20, bridgeCount: 15, missingCount: 15);

        var result = CustomerDatasetPreflightService.Validate(dataset, manifest);

        Assert.Equal("PASS", result.Status);
        Assert.Empty(result.BlockingFailures);
        Assert.Equal(50, result.ManifestRows);
        Assert.Equal(20, result.OkCount);
        Assert.Equal(30, result.NgCount);
        Assert.Equal(2, result.DefectClassCount);
    }

    [Fact]
    public void GeneratedDemoImagesFolderPassesPreflightAgainstDatasetManifest()
    {
        var dataset = CreateDataset();
        var manifest = WriteBalancedManifest(dataset, okCount: 20, bridgeCount: 15, missingCount: 15);
        var selectedImagesFolder = Path.Combine(dataset, "images");

        var result = CustomerDatasetPreflightService.Validate(selectedImagesFolder, manifest);

        Assert.Equal("PASS", result.Status);
        Assert.Equal(Path.GetFullPath(dataset), Path.GetFullPath(result.DatasetFolder));
        Assert.Equal(50, result.ManifestRows);
        Assert.Equal(20, result.OkCount);
        Assert.Equal(30, result.NgCount);
    }

    [Fact]
    public void DuplicateFileHashesFailPreflight()
    {
        var dataset = CreateDataset();
        WriteGolden(dataset, "golden/ref_top.png");
        WriteImage(dataset, "images/board_0001_ok.png", "same-image-content");
        WriteImage(dataset, "images/board_0002_bridge.png", "same-image-content");
        var manifest = WriteManifest(dataset,
            Row("images/board_0001_ok.png", "OK", "golden/ref_top.png", "OK"),
            Row("images/board_0002_bridge.png", "NG", "golden/ref_top.png", "solder_bridge"));

        var result = CustomerDatasetPreflightService.Validate(dataset, manifest, RelaxedBalance());

        Assert.Equal("FAIL", result.Status);
        Assert.True(result.DuplicateFileHashCount > 0);
        Assert.Contains(result.BlockingFailures, failure => failure.Contains("duplicate image file hashes", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void IncompleteRoiMetadataFailsPreflight()
    {
        var dataset = CreateDataset();
        WriteGolden(dataset, "golden/ref_top.png");
        WriteImage(dataset, "images/board_0001_bridge.png");
        var manifest = WriteManifest(dataset,
            "images/board_0001_bridge.png,NG,golden/ref_top.png,solder_bridge,top,U1,,pad,LOT-1,TBOX,incomplete ROI metadata");

        var result = CustomerDatasetPreflightService.Validate(dataset, manifest, RelaxedBalance());

        Assert.Equal("FAIL", result.Status);
        Assert.True(result.MissingMetadataCount > 0);
        Assert.Contains(result.BlockingFailures, failure => failure.Contains("ROI/refdes metadata is incomplete", StringComparison.OrdinalIgnoreCase));
    }

    private string CreateDataset(string name = "dataset")
    {
        var dataset = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(dataset, "images"));
        Directory.CreateDirectory(Path.Combine(dataset, "golden"));
        return dataset;
    }

    private static string WriteBalancedManifest(string dataset, int okCount, int bridgeCount, int missingCount)
    {
        WriteGolden(dataset, "golden/ref_top.png");
        var rows = new List<string>();
        for (var i = 0; i < okCount; i++)
        {
            var image = $"images/board_{i:D4}_ok.png";
            WriteImage(dataset, image);
            rows.Add(Row(image, "OK", "golden/ref_top.png", "OK"));
        }

        for (var i = 0; i < bridgeCount; i++)
        {
            var image = $"images/board_bridge_{i:D4}.png";
            WriteImage(dataset, image);
            rows.Add(Row(image, "NG", "golden/ref_top.png", "solder_bridge"));
        }

        for (var i = 0; i < missingCount; i++)
        {
            var image = $"images/board_missing_{i:D4}.png";
            WriteImage(dataset, image);
            rows.Add(Row(image, "NG", "golden/ref_top.png", "missing_component"));
        }

        return WriteManifest(dataset, rows.ToArray());
    }

    private static string WriteManifest(string dataset, params string[] rows)
    {
        var path = Path.Combine(dataset, "customer_validation_manifest.csv");
        File.WriteAllLines(path, new[]
        {
            "image,ground_truth,golden_image,defect_type,side,refdes,roi_id,roi_type,lot_id,board_model,notes",
        }.Concat(rows));
        return path;
    }

    private static string Row(string image, string groundTruth, string golden, string defectType)
        => $"{image},{groundTruth},{golden},{defectType},top,U1,ROI-U1,pad,LOT-1,TBOX,test row";

    private static void WriteImage(string dataset, string relativePath, string? content = null)
    {
        var path = Path.Combine(dataset, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content ?? $"synthetic image {relativePath} {Guid.NewGuid():N}");
    }

    private static void WriteGolden(string dataset, string relativePath)
    {
        var path = Path.Combine(dataset, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"synthetic golden {relativePath}");
    }

    private static CustomerDatasetPreflightCriteria RelaxedBalance()
        => new()
        {
            DatasetQualityCriteria = new ValidationDatasetQualityCriteria
            {
                MinimumTotalImages = 1,
                MinimumKnownGroundTruthImages = 1,
                MinimumOkImages = 0,
                MinimumNgImages = 1,
                MinimumDefectClasses = 1,
                MinimumImagesPerDefectClass = 1,
            },
        };
}
