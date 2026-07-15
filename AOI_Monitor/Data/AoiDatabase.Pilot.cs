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
    public static long CreateCustomerPilotSession(CustomerPilotSessionRecord session)
    {
        EnsureInitialized();
        var now = DateTime.UtcNow;
        session.SessionId = string.IsNullOrWhiteSpace(session.SessionId)
            ? $"PILOT-{now:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6]}"
            : session.SessionId.Trim();
        session.CreatedAtUtc = session.CreatedAtUtc == DateTime.MinValue ? now : session.CreatedAtUtc.ToUniversalTime();
        session.UpdatedAtUtc = now;

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO CustomerPilotSessions
                (SessionId, DeploymentProfile, Status, DatasetFolder, ManifestPath, OperatorId, CreatedAtUtc, UpdatedAtUtc, CompletedAtUtc)
            VALUES
                ($sessionId, $deploymentProfile, $status, $datasetFolder, $manifestPath, $operatorId, $createdAtUtc, $updatedAtUtc, $completedAtUtc);
            SELECT last_insert_rowid();
            """;
        AddCustomerPilotSessionParameters(command, session);
        session.Id = (long)(command.ExecuteScalar() ?? 0L);
        return session.Id;
    }

    public static void UpdateCustomerPilotSession(CustomerPilotSessionRecord session)
    {
        EnsureInitialized();
        session.UpdatedAtUtc = DateTime.UtcNow;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE CustomerPilotSessions
            SET DeploymentProfile = $deploymentProfile,
                Status = $status,
                DatasetFolder = $datasetFolder,
                ManifestPath = $manifestPath,
                OperatorId = $operatorId,
                UpdatedAtUtc = $updatedAtUtc,
                CompletedAtUtc = $completedAtUtc
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", session.Id);
        AddCustomerPilotSessionParameters(command, session);
        command.ExecuteNonQuery();
    }

    public static CustomerPilotSessionRecord? GetCustomerPilotSession(long id)
    {
        EnsureInitialized();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM CustomerPilotSessions WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadCustomerPilotSession(reader) : null;
    }

    public static CustomerPilotSessionRecord? GetLatestIncompleteCustomerPilotSession()
    {
        EnsureInitialized();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT * FROM CustomerPilotSessions
            WHERE Status != 'Completed'
            ORDER BY UpdatedAtUtc DESC, Id DESC
            LIMIT 1;
            """;
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadCustomerPilotSession(reader) : null;
    }

    public static IReadOnlyList<CustomerPilotStepRecord> GetCustomerPilotSteps(long sessionId)
    {
        EnsureInitialized();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM CustomerPilotSteps WHERE SessionId = $sessionId ORDER BY StepOrder, Id;";
        command.Parameters.AddWithValue("$sessionId", sessionId);
        using var reader = command.ExecuteReader();
        var rows = new List<CustomerPilotStepRecord>();
        while (reader.Read())
            rows.Add(ReadCustomerPilotStep(reader));
        return rows;
    }

    public static long UpsertCustomerPilotStep(CustomerPilotStepRecord step)
    {
        EnsureInitialized();
        step.UpdatedAtUtc = DateTime.UtcNow;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO CustomerPilotSteps
                (SessionId, StepKey, StepOrder, Status, EvidencePath, MessagesJson, Waived, WaiverReason, WaivedBy, WaivedAtUtc, UpdatedAtUtc)
            VALUES
                ($sessionId, $stepKey, $stepOrder, $status, $evidencePath, $messagesJson, $waived, $waiverReason, $waivedBy, $waivedAtUtc, $updatedAtUtc)
            ON CONFLICT(SessionId, StepKey) DO UPDATE SET
                StepOrder = excluded.StepOrder,
                Status = excluded.Status,
                EvidencePath = excluded.EvidencePath,
                MessagesJson = excluded.MessagesJson,
                Waived = excluded.Waived,
                WaiverReason = excluded.WaiverReason,
                WaivedBy = excluded.WaivedBy,
                WaivedAtUtc = excluded.WaivedAtUtc,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;
        AddCustomerPilotStepParameters(command, step);
        command.ExecuteNonQuery();

        using var idCommand = connection.CreateCommand();
        idCommand.CommandText = "SELECT Id FROM CustomerPilotSteps WHERE SessionId = $sessionId AND StepKey = $stepKey;";
        idCommand.Parameters.AddWithValue("$sessionId", step.SessionId);
        idCommand.Parameters.AddWithValue("$stepKey", step.StepKey.ToString());
        step.Id = Convert.ToInt64(idCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
        return step.Id;
    }

    public static void SavePilotIssue(PilotIssue issue, string eventType, string message, string operatorId, string previousStatus = "")
    {
        EnsureInitialized();
        var now = DateTime.UtcNow;
        issue.IssueId = string.IsNullOrWhiteSpace(issue.IssueId)
            ? $"ISSUE-{now:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}"
            : issue.IssueId.Trim();
        issue.CreatedAtUtc = issue.CreatedAtUtc == DateTime.MinValue ? now : issue.CreatedAtUtc.ToUniversalTime();
        if (issue.Status is PilotIssueStatus.Closed or PilotIssueStatus.Waived or PilotIssueStatus.Fixed or PilotIssueStatus.Verified)
            issue.ClosedAtUtc ??= now;
        else
            issue.ClosedAtUtc = null;

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO PilotIssues
                (IssueId, CreatedAtUtc, Category, Severity, BoardModel, LotId, ImagePath, PageName, ReproductionSteps,
                 ExpectedBehavior, ActualBehavior, ScreenshotPath, RelatedInspectionId,
                 RelatedAcceptanceRunId, Status, Owner, Notes, Resolution, ClosedAtUtc)
            VALUES
                ($issueId, $createdAtUtc, $category, $severity, $boardModel, $lotId, $imagePath, $pageName, $reproductionSteps,
                 $expectedBehavior, $actualBehavior, $screenshotPath, $relatedInspectionId,
                 $relatedAcceptanceRunId, $status, $owner, $notes, $resolution, $closedAtUtc)
            ON CONFLICT(IssueId) DO UPDATE SET
                Category = excluded.Category,
                Severity = excluded.Severity,
                BoardModel = excluded.BoardModel,
                LotId = excluded.LotId,
                ImagePath = excluded.ImagePath,
                PageName = excluded.PageName,
                ReproductionSteps = excluded.ReproductionSteps,
                ExpectedBehavior = excluded.ExpectedBehavior,
                ActualBehavior = excluded.ActualBehavior,
                ScreenshotPath = excluded.ScreenshotPath,
                RelatedInspectionId = excluded.RelatedInspectionId,
                RelatedAcceptanceRunId = excluded.RelatedAcceptanceRunId,
                Status = excluded.Status,
                Owner = excluded.Owner,
                Notes = excluded.Notes,
                Resolution = excluded.Resolution,
                ClosedAtUtc = excluded.ClosedAtUtc;
            """;
        AddPilotIssueParameters(command, issue);
        command.ExecuteNonQuery();

        using var eventCommand = connection.CreateCommand();
        eventCommand.Transaction = transaction;
        eventCommand.CommandText =
            """
            INSERT INTO PilotIssueEvents
                (IssueId, CreatedAtUtc, EventType, OperatorId, Message, PreviousStatus, NewStatus)
            VALUES
                ($issueId, $createdAtUtc, $eventType, $operatorId, $message, $previousStatus, $newStatus);
            """;
        eventCommand.Parameters.AddWithValue("$issueId", issue.IssueId);
        eventCommand.Parameters.AddWithValue("$createdAtUtc", now.ToString("O", CultureInfo.InvariantCulture));
        eventCommand.Parameters.AddWithValue("$eventType", eventType);
        eventCommand.Parameters.AddWithValue("$operatorId", operatorId);
        eventCommand.Parameters.AddWithValue("$message", message);
        eventCommand.Parameters.AddWithValue("$previousStatus", previousStatus);
        eventCommand.Parameters.AddWithValue("$newStatus", issue.Status.ToString());
        eventCommand.ExecuteNonQuery();
        transaction.Commit();
    }

    public static PilotIssue? GetPilotIssue(string issueId)
    {
        EnsureInitialized();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM PilotIssues WHERE IssueId = $issueId;";
        command.Parameters.AddWithValue("$issueId", issueId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadPilotIssue(reader) : null;
    }

    public static IReadOnlyList<PilotIssue> GetPilotIssues(PilotIssueFilter? filter = null, int limit = 500)
    {
        EnsureInitialized();
        filter ??= new PilotIssueFilter();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var where = new List<string>();
        if (filter.Category is { } category)
        {
            where.Add("Category = $category");
            command.Parameters.AddWithValue("$category", category.ToString());
        }
        if (filter.Status is { } status)
        {
            where.Add("Status = $status");
            command.Parameters.AddWithValue("$status", status.ToString());
        }
        if (filter.OpenOnly)
            where.Add("Status IN ('Open', 'Investigating')");
        if (!string.IsNullOrWhiteSpace(filter.Severity))
        {
            where.Add("Severity = $severity");
            command.Parameters.AddWithValue("$severity", filter.Severity.Trim());
        }
        if (!string.IsNullOrWhiteSpace(filter.BoardModel))
        {
            where.Add("BoardModel LIKE $boardModel");
            command.Parameters.AddWithValue("$boardModel", $"%{filter.BoardModel.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(filter.LotId))
        {
            where.Add("LotId LIKE $lotId");
            command.Parameters.AddWithValue("$lotId", $"%{filter.LotId.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(filter.PageName))
        {
            where.Add("PageName LIKE $pageName");
            command.Parameters.AddWithValue("$pageName", $"%{filter.PageName.Trim()}%");
        }

        command.CommandText =
            $"""
            SELECT * FROM PilotIssues
            {(where.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", where))}
            ORDER BY datetime(CreatedAtUtc) DESC, IssueId DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
        using var reader = command.ExecuteReader();
        var rows = new List<PilotIssue>();
        while (reader.Read())
            rows.Add(ReadPilotIssue(reader));
        return rows;
    }

    public static IReadOnlyList<PilotIssueEvent> GetPilotIssueEvents(string issueId)
    {
        EnsureInitialized();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM PilotIssueEvents WHERE IssueId = $issueId ORDER BY datetime(CreatedAtUtc), Id;";
        command.Parameters.AddWithValue("$issueId", issueId);
        using var reader = command.ExecuteReader();
        var rows = new List<PilotIssueEvent>();
        while (reader.Read())
            rows.Add(ReadPilotIssueEvent(reader));
        return rows;
    }

}
