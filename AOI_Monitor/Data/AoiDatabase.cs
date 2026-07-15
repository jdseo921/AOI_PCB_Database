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
    private const string AppFolderName = "AOI_Monitor";
    private static readonly HashSet<string> SupportedImportExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg",
    };

    private static bool _initialized;
    private static string _storageRoot = ResolveStorageRoot();

    public static Func<string>? AuditOperatorProvider { get; set; }
    public static Func<string>? AuditUserIdProvider { get; set; }
    public static Func<string>? AuditUserRoleProvider { get; set; }
    public static Func<string>? AuditStationProvider { get; set; }

    public static string StorageRoot => _storageRoot;
    public static string DefaultStorageRoot => ResolveStorageRoot();
    public static string DatabasePath => Path.Combine(StorageRoot, "aoi_monitor.sqlite");
    public static string ImageVaultPath => Path.Combine(StorageRoot, "image_vault");
    public static string TrainingVaultPath => Path.Combine(ImageVaultPath, "training");
    public static int LatestSchemaVersion => AoiDatabaseMigrations.LatestVersion;

    public static void ConfigureStorageRoot(string storageRoot)
    {
        if (string.IsNullOrWhiteSpace(storageRoot))
            throw new ArgumentException("Storage root is required.", nameof(storageRoot));

        _storageRoot = Path.GetFullPath(storageRoot);
        _initialized = false;
    }

    public static void Initialize()
    {
        Directory.CreateDirectory(StorageRoot);
        Directory.CreateDirectory(ImageVaultPath);
        Directory.CreateDirectory(TrainingVaultPath);

        using var connection = OpenConnection();
        var hasExistingSchema = HasExistingUserSchema(connection);
        if (hasExistingSchema)
        {
            EnsureSchemaCompatibility(connection);
            ExecuteSchemaSql(connection);
        }
        else
        {
            ExecuteSchemaSql(connection);
            EnsureSchemaCompatibility(connection);
        }

        // Log retention (archive + purge) is NOT run here: it is a configurable, potentially
        // destructive maintenance step driven by user settings and executed once at application
        // startup via LogRetentionService, so tests and re-initializations never purge data.
        _initialized = true;
    }

}
