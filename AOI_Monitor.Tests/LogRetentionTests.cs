using System.Globalization;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class LogRetentionTests : IDisposable
{
    private readonly string _root;

    public LogRetentionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Retention_Tests", Guid.NewGuid().ToString("N"));
        AoiDatabase.ConfigureStorageRoot(_root);
        LogRetentionSettingsService.ResetForTests();
    }

    public void Dispose()
    {
        LogRetentionSettingsService.ResetForTests();
        StorageRootSettingsService.ConfigureSettingsDirectoryForTests(null);
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException ex)
        {
            System.Diagnostics.Trace.WriteLine($"Retention test cleanup skipped: {ex.Message}");
        }
    }

    private static SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={AoiDatabase.DatabasePath}");
        connection.Open();
        return connection;
    }

    private static long ExecInsert(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql + " SELECT last_insert_rowid();";
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }

    private static long Count(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }

    private static string Iso(int daysAgo) =>
        DateTime.UtcNow.AddDays(-daysAgo).ToString("O", CultureInfo.InvariantCulture);

    private static long InsertInspection(SqliteConnection c, string sample, string when) => ExecInsert(
        c,
        "INSERT INTO InspectionResults (SampleImagePath, DifferenceScore, MeanBrightness, Verdict, Confidence, SuggestedDefect, PolicyName, ModelVersion, DecisionReason, HotspotX, HotspotY, HotspotWidth, HotspotHeight, CreatedAtUtc) " +
        "VALUES ($s,0,0,'PASS',0,'none','policy','v1','reason',0,0,0,0,$t);",
        ("$s", sample), ("$t", when));

    private static long InsertDefect(SqliteConnection c, long inspectionId, string when) => ExecInsert(
        c,
        "INSERT INTO Defects (InspectionResultId, DefectType, Severity, CreatedAtUtc) VALUES ($ir,'Missing','High',$t);",
        ("$ir", inspectionId), ("$t", when));

    private static long InsertExport(SqliteConnection c, string when) => ExecInsert(
        c,
        "INSERT INTO ExportHistory (ExportType, FilePath, Status, CreatedAtUtc) VALUES ('csv','/p','OK',$t);",
        ("$t", when));

    private static long InsertVerification(SqliteConnection c, long exportId, string when) => ExecInsert(
        c,
        "INSERT INTO ExportVerification (ExportHistoryId, CheckedAtUtc, ExportType, ExportPath, Status) VALUES ($e,$t,'csv','/p','OK');",
        ("$e", exportId), ("$t", when));

    private static long InsertReview(SqliteConnection c, string message, string when) => ExecInsert(
        c,
        "INSERT INTO ReviewEvents (Category, Message, EventTimeUtc) VALUES ('CAT',$m,$t);",
        ("$m", message), ("$t", when));

    private static long InsertAudit(SqliteConnection c, string detail, string when) => ExecInsert(
        c,
        "INSERT INTO AuditEvents (TimestampUtc, LocalTimestamp, ActionCategory, ActionDetail) VALUES ($t,$t,'CAT',$d);",
        ("$t", when), ("$d", detail));

    [Fact]
    public void RunLogRetentionArchivesAndPurgesOldRowsIncludingChildren()
    {
        AoiDatabase.Initialize();

        long oldInspection, oldDefect, oldExport, oldVerification, oldReview, oldAudit;
        long recentInspection, recentDefect, recentAudit;
        using (var seed = Open())
        {
            oldInspection = InsertInspection(seed, "old-sample.png", Iso(40));
            oldDefect = InsertDefect(seed, oldInspection, Iso(40));
            oldExport = InsertExport(seed, Iso(40));
            oldVerification = InsertVerification(seed, oldExport, Iso(40));
            oldReview = InsertReview(seed, "old-review", Iso(40));
            oldAudit = InsertAudit(seed, "old-audit", Iso(40));

            recentInspection = InsertInspection(seed, "recent-sample.png", Iso(1));
            recentDefect = InsertDefect(seed, recentInspection, Iso(1));
            recentAudit = InsertAudit(seed, "recent-audit", Iso(1));
        }

        var result = AoiDatabase.RunLogRetention(new LogRetentionPolicy { Enabled = true, RetentionDays = 30, WarningLeadDays = 7 });

        Assert.True(result.PurgedRowCount >= 6, $"expected >= 6 purged, got {result.PurgedRowCount}");
        Assert.True(result.ArchivedRowCount >= 6, $"expected >= 6 archived, got {result.ArchivedRowCount}");

        using var verify = Open();

        // Old rows (including children) are removed from the live tables.
        Assert.Equal(0L, Count(verify, "SELECT COUNT(*) FROM InspectionResults WHERE Id=$id;", ("$id", oldInspection)));
        Assert.Equal(0L, Count(verify, "SELECT COUNT(*) FROM Defects WHERE Id=$id;", ("$id", oldDefect)));
        Assert.Equal(0L, Count(verify, "SELECT COUNT(*) FROM ExportHistory WHERE Id=$id;", ("$id", oldExport)));
        Assert.Equal(0L, Count(verify, "SELECT COUNT(*) FROM ExportVerification WHERE Id=$id;", ("$id", oldVerification)));
        Assert.Equal(0L, Count(verify, "SELECT COUNT(*) FROM ReviewEvents WHERE Id=$id;", ("$id", oldReview)));
        Assert.Equal(0L, Count(verify, "SELECT COUNT(*) FROM AuditEvents WHERE Id=$id;", ("$id", oldAudit)));

        // Recent rows survive.
        Assert.Equal(1L, Count(verify, "SELECT COUNT(*) FROM InspectionResults WHERE Id=$id;", ("$id", recentInspection)));
        Assert.Equal(1L, Count(verify, "SELECT COUNT(*) FROM Defects WHERE Id=$id;", ("$id", recentDefect)));
        Assert.Equal(1L, Count(verify, "SELECT COUNT(*) FROM AuditEvents WHERE Id=$id;", ("$id", recentAudit)));

        // Archived rows are recoverable: present in LogArchive with a non-empty payload.
        Assert.Equal(1L, Count(verify,
            "SELECT COUNT(*) FROM LogArchive WHERE SourceTable='InspectionResults' AND SourceId=$id AND PayloadJson <> '';",
            ("$id", oldInspection)));
        Assert.Equal(1L, Count(verify,
            "SELECT COUNT(*) FROM LogArchive WHERE SourceTable='Defects' AND SourceId=$id AND PayloadJson <> '';",
            ("$id", oldDefect)));
        Assert.Equal(1L, Count(verify,
            "SELECT COUNT(*) FROM LogArchive WHERE SourceTable='ExportVerification' AND SourceId=$id AND PayloadJson <> '';",
            ("$id", oldVerification)));

        // The payload preserves the original column values.
        Assert.Equal(1L, Count(verify,
            "SELECT COUNT(*) FROM LogArchive WHERE SourceTable='InspectionResults' AND SourceId=$id AND PayloadJson LIKE '%old-sample.png%';",
            ("$id", oldInspection)));

        // Recent rows are NOT archived.
        Assert.Equal(0L, Count(verify,
            "SELECT COUNT(*) FROM LogArchive WHERE SourceTable='InspectionResults' AND SourceId=$id;",
            ("$id", recentInspection)));
    }

    [Fact]
    public void RunLogRetentionIsNoOpWhenDisabled()
    {
        AoiDatabase.Initialize();

        long oldInspection;
        using (var seed = Open())
            oldInspection = InsertInspection(seed, "keep-me.png", Iso(90));

        var result = AoiDatabase.RunLogRetention(new LogRetentionPolicy { Enabled = false, RetentionDays = 30 });

        Assert.Equal(0, result.PurgedRowCount);
        Assert.Equal(0, result.ArchivedRowCount);

        using var verify = Open();
        Assert.Equal(1L, Count(verify, "SELECT COUNT(*) FROM InspectionResults WHERE Id=$id;", ("$id", oldInspection)));
    }

    [Fact]
    public void CountRowsNearingPurgeCountsOnlyRowsInsideTheWarningWindow()
    {
        AoiDatabase.Initialize();

        using (var seed = Open())
        {
            InsertAudit(seed, "nearing", Iso(26));   // inside 23..30 day window -> counted
            InsertAudit(seed, "already-past", Iso(40)); // older than retention -> not counted (would be purged)
            InsertAudit(seed, "fresh", Iso(1));         // newer than warning window -> not counted
        }

        var nearing = AoiDatabase.CountRowsNearingPurge(retentionDays: 30, leadDays: 7);

        Assert.Equal(1, nearing);
    }

    [Fact]
    public void LogArchiveHasRecoverablePayloadColumn()
    {
        AoiDatabase.Initialize();

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(LogArchive);";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
            columns.Add(reader.GetString(1));

        Assert.Contains("PayloadJson", columns);
    }

    [Fact]
    public void LogRetentionSettingsNormalizeClampsRetentionAndWarningLead()
    {
        AoiDatabase.Initialize();

        var settings = new LogRetentionSettings { Enabled = true, RetentionDays = 0, WarningEnabled = true, WarningLeadDays = 999 };
        LogRetentionSettingsService.Save(settings, "TEST");

        var loaded = LogRetentionSettingsService.LoadSettings();
        Assert.Equal(1, loaded.RetentionDays);          // clamped up from 0
        Assert.Equal(1, loaded.WarningLeadDays);        // clamped down to <= retention days

        var policy = LogRetentionSettingsService.LoadPolicy();
        Assert.True(policy.Enabled);
        Assert.Equal(1, policy.RetentionDays);
    }
}
