using AOI_Monitor.Services;

namespace AOI_Monitor.Tools;

/// <summary>
/// Converts a third-party PCB image dataset into the Stage 1 dataset contract.
///
/// Thin CLI shell only: parsing, console rendering, exit codes.
/// 0 = prepared with no warnings, 1 = prepared with warnings (read them before using the data),
/// 2 = usage or input error.
/// </summary>
public static class PrepareDatasetCommand
{
    private static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "source",
        "output",
        "layout",
        "golden",
        "golden-folder",
        "board",
        "lot",
        "max-ok",
        "max-ng-per-class",
        "seed",
    };

    public static int Execute(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || !string.Equals(args[0], "prepare-dataset", StringComparison.OrdinalIgnoreCase))
        {
            error.WriteLine("FAIL prepare-dataset command was not selected.");
            WriteUsage(error);
            return 2;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rest = args.Skip(1).ToArray();
        for (var i = 0; i < rest.Length; i++)
        {
            var key = rest[i];
            if (!key.StartsWith("--", StringComparison.Ordinal))
            {
                error.WriteLine($"FAIL Unexpected argument: {key}");
                WriteUsage(error);
                return 2;
            }

            var name = key[2..];
            if (name.Equals("emit-learning", StringComparison.OrdinalIgnoreCase))
            {
                flags.Add(name);
                continue;
            }

            if (!ValueOptions.Contains(name))
            {
                error.WriteLine($"FAIL Unknown option: {key}");
                WriteUsage(error);
                return 2;
            }

            if (i + 1 >= rest.Length || rest[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                error.WriteLine($"FAIL Missing value for {key}.");
                WriteUsage(error);
                return 2;
            }

            values[name] = rest[++i];
        }

        foreach (var required in new[] { "source", "output" })
        {
            if (!values.ContainsKey(required))
            {
                error.WriteLine($"FAIL Missing required option --{required}.");
                WriteUsage(error);
                return 2;
            }
        }

        if (!TryParseLayout(values.GetValueOrDefault("layout"), out var layout))
        {
            error.WriteLine($"FAIL Unknown --layout value: {values["layout"]}. Use auto, mvtec, visa, class-folders, or paired-template.");
            return 2;
        }

        if (!TryParseGolden(values.GetValueOrDefault("golden"), out var goldenStrategy))
        {
            error.WriteLine($"FAIL Unknown --golden value: {values["golden"]}. Use auto, paired, per-board, from-normal, or none.");
            return 2;
        }

        try
        {
            var result = DatasetPreparationService.Prepare(new DatasetPreparationRequest
            {
                SourceFolder = values["source"],
                OutputFolder = values["output"],
                Layout = layout,
                GoldenStrategy = goldenStrategy,
                GoldenFolder = values.GetValueOrDefault("golden-folder", string.Empty),
                BoardModel = values.GetValueOrDefault("board", "CUSTOMER-BOARD"),
                LotId = values.GetValueOrDefault("lot", "LOT-EVAL"),
                MaxOkImages = ParseInt(values, "max-ok"),
                MaxNgImagesPerClass = ParseInt(values, "max-ng-per-class"),
                Seed = ParseInt(values, "seed", 20260808),
                EmitLearningLayout = flags.Contains("emit-learning"),
            });

            output.WriteLine("OK Stage 1 dataset prepared.");
            output.WriteLine($"Layout detected : {result.DetectedLayout}");
            output.WriteLine($"Golden strategy : {result.GoldenStrategy}");
            output.WriteLine($"Images          : {result.OkCount} OK, {result.NgCount} NG, {result.GoldenCount} golden reference(s)");
            output.WriteLine($"Dataset folder  : {result.DatasetFolder}");
            output.WriteLine($"Manifest        : {result.ManifestPath}");
            if (!string.IsNullOrEmpty(result.LearningFolder))
                output.WriteLine($"Learning folder : {result.LearningFolder}");

            output.WriteLine();
            output.WriteLine("Defect classes:");
            if (result.Classes.Count == 0)
                output.WriteLine("  (none - dataset has no known-defect images)");
            foreach (var item in result.Classes)
            {
                var taxonomy = item.IsKnownToTaxonomy ? item.CanonicalClass : $"{item.CanonicalClass}  [NOT IN TAXONOMY]";
                output.WriteLine($"  {item.DefectClass,-28} -> {taxonomy}  ({item.Count})");
            }

            output.WriteLine();
            if (result.Warnings.Count == 0)
            {
                output.WriteLine("Warnings: none.");
            }
            else
            {
                output.WriteLine($"Warnings ({result.Warnings.Count}) - read these before trusting any run on this dataset:");
                foreach (var warning in result.Warnings)
                    output.WriteLine($"  - {warning}");
            }

            output.WriteLine();
            output.WriteLine("Limitations:");
            foreach (var limitation in result.Limitations)
                output.WriteLine($"  - {limitation}");

            output.WriteLine();
            output.WriteLine("Next:");
            output.WriteLine($"  {result.NextCommand}");

            return result.Warnings.Count == 0 ? 0 : 1;
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"FAIL Dataset preparation failed: {ex.Message}");
            return 2;
        }
    }

    private static int ParseInt(IReadOnlyDictionary<string, string> values, string name, int fallback = 0)
        => values.TryGetValue(name, out var text) &&
           int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    private static bool TryParseLayout(string? value, out DatasetSourceLayout layout)
    {
        layout = DatasetSourceLayout.Auto;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        switch (value.Trim().ToLowerInvariant().Replace("_", "-", StringComparison.Ordinal))
        {
            case "auto": layout = DatasetSourceLayout.Auto; return true;
            case "mvtec": layout = DatasetSourceLayout.MvTec; return true;
            case "visa": layout = DatasetSourceLayout.Visa; return true;
            case "class-folders": case "classfolders": layout = DatasetSourceLayout.ClassFolders; return true;
            case "paired-template": case "paired": layout = DatasetSourceLayout.PairedTemplate; return true;
            default: return false;
        }
    }

    private static bool TryParseGolden(string? value, out GoldenAssignmentStrategy strategy)
    {
        strategy = GoldenAssignmentStrategy.Auto;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        switch (value.Trim().ToLowerInvariant().Replace("_", "-", StringComparison.Ordinal))
        {
            case "auto": strategy = GoldenAssignmentStrategy.Auto; return true;
            case "paired": strategy = GoldenAssignmentStrategy.Paired; return true;
            case "per-board": case "perboard": strategy = GoldenAssignmentStrategy.PerBoard; return true;
            case "from-normal": case "fromnormal": strategy = GoldenAssignmentStrategy.FromNormal; return true;
            case "none": strategy = GoldenAssignmentStrategy.None; return true;
            default: return false;
        }
    }

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("Usage:");
        writer.WriteLine("  AOI_Monitor.Tools prepare-dataset --source <folder> --output <folder>");
        writer.WriteLine("        [--layout auto|mvtec|visa|class-folders|paired-template]");
        writer.WriteLine("        [--golden auto|paired|per-board|from-normal|none] [--golden-folder <folder>]");
        writer.WriteLine("        [--board <name>] [--lot <id>] [--max-ok <n>] [--max-ng-per-class <n>]");
        writer.WriteLine("        [--seed <n>] [--emit-learning]");
        writer.WriteLine();
        writer.WriteLine("  Converts a PCB image dataset you already have into the Stage 1 dataset contract");
        writer.WriteLine("  (images/, golden/, customer_validation_manifest.csv). It downloads nothing and only");
        writer.WriteLine("  reads --source. Licensing of the source images is your responsibility.");
        writer.WriteLine();
        writer.WriteLine("  Exit codes: 0 = prepared cleanly, 1 = prepared with warnings, 2 = usage/input error.");
    }
}
