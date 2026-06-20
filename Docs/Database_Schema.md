# AOI Monitor Database Schema

## Schema Version

The SQLite database is versioned through the `SchemaInfo` table.

- `SchemaInfo.Key = SchemaVersion`
- Current baseline: `2`
- Runtime database path: `%LOCALAPPDATA%\AOI_Monitor\aoi_monitor.sqlite` by default, or the configured storage root.

## Migration Policy

Schema changes must be added to `AOI_Monitor/Data/AoiDatabaseMigrations.cs` as ordered migrations. Each migration has a version, description, and transactional `Apply` step.

Migration rules:

- Migrations must be idempotent. Re-running startup against the same database must be safe.
- Additive changes should use compatibility helpers such as `TableExists`, `ColumnExists`, `IndexExists`, and `AddColumnIfMissing`.
- Existing customer data must not be deleted or destructively rewritten during normal startup.
- The schema version is updated only after the migration transaction succeeds.
- New databases are created at the latest schema version.
- Unversioned existing databases are treated as version `0` and upgraded to the current baseline.

## Tables

Current application tables:

- `SchemaInfo` - key/value metadata for schema versioning.
- `Images` - imported image vault records and source metadata.
- `InspectionResults` - persisted inspection decisions and timing evidence.
- `Defects` - defect rows associated with inspections or images.
- `ReviewEvents` - operator review and disposition events.
- `AuditEvents` - traceable user and system actions.
- `RecipeRevisions` - saved recipe definitions and revision metadata.
- `TrainingSamples` - local training/evaluation sample references.
- `CalibrationProfiles` - calibration profile summary records.
- `CalibrationPoints` - calibration point mappings for a profile.
- `BatchTestRuns` - AI model/batch validation run summaries.
- `BatchTestResults` - per-image validation results.
- `ModelRegistry` - locally registered ONNX model metadata and active selection state.
- `ExportHistory` - CSV/report/package export audit records.
- `ExportVerification` - export artifact verification status, SHA-256 checksums, and messages.
- `ValidationPackages` - generated customer validation package records.
- `MesUploadAttempts` - MES/mock/REST upload attempt audit trail.
- `MesSpoolQueue` - offline MES REST retry queue.
- `LogArchive` - copy-only archival index for older audit/log rows.

## Indexes

Indexes are created with `CREATE INDEX IF NOT EXISTS` during initialization. They are part of the baseline schema and are safe to re-run. New index additions should either be included in a migration or added to the baseline after the migration that guarantees required columns exists.

## Data Handling Warning

Do not commit customer images, runtime SQLite databases, export packages, MES payloads, model files, or image vault contents to git. They are runtime artifacts under the configured storage root and may contain customer or process-sensitive data.
