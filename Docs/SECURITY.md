OpenAI/Codex and numerous other coding agents will review your output once you are done.

# Security: Image Data Retention and Network Access

Read this when you handle inspection data, customer images, exports, central sync, or credentials in this repository. It is a practical map of where data lives and what leaves the station; the canonical, binding rules are the standard volumes cited below (Docs/standard VOL07, VOL08, VOL16) — this file does not restate or replace them. The project is standards-aligned, not formally certified.

## Data at rest

- The station is local-first: inspection evidence lives in a local SQLite database (default `%LOCALAPPDATA%\AOI_Monitor\aoi_monitor.sqlite`, or the configured storage root) plus a managed `image_vault/` under the same root that receives a copy of every imported image, hashed with SHA-256 for duplicate detection. Results, defects, review/audit events, recipe revisions, calibration profiles, learned-model artifacts, export history, and MES upload attempts are all SQLite tables (see `Docs/DATA_PIPELINE.md`).
- **Customer IP.** Customer board images, image vault contents, learned visual models and their artifacts, runtime SQLite databases, export packages, and MES payloads may contain customer or process-sensitive data. They are runtime artifacts under the configured storage root and must never be committed to git.
- **Repo enforcement (`.gitignore`).** The exclusion rules are explicit: local databases and sidecars (`*.sqlite`, `*.sqlite3`, `*.db`, `*.db3`, `*-shm`/`*-wal`); runtime data roots (`exports/`, `image_vault/` including `image_vault/training/`, `training_set/`, `training_sets/`, `data/`, `model_registry/`); local settings files that can carry endpoints or paths (`first_run_settings.json`, `storage_root_settings.json`, `inspection_model_config.json`, `camera_source_settings.json`, `lighting_settings.json`, `mes_integration_settings.json`); and image payloads (`SampleData/**` image and archive extensions, `CustomerData/`, `customer_images/`, `customer_datasets/`, `datasets/customer/`, `large_datasets/`, `*.heic`, `*.raw`). `SampleData/README.md` instructions stay in the repo; image payloads do not.
- Canonical governance: Docs/standard VOL16 §46 — data classification (Table 46-1), handling rules and current-state honesty, data flows and control points, cross-border transfers, and the PRI-001–PRI-025 requirements. Untrusted-input handling for image files, paths, and serialized data is owned by VOL08 §29 (image ingestion hard limits, path/filesystem rules, no code-executing deserializers).

## Retention

- Retention, deletion, and export control are governed by Docs/standard VOL16 §46 (PRI-006–PRI-009). The bullets below are current implemented behavior, not the governing rule.
- Startup log retention (System Settings > Data Retention, Engineer/Admin) archives and purges only the four log tables: `InspectionResults`, `ExportHistory`, `ReviewEvents`, `AuditEvents`. Rows older than the configured window are first copied with their full payload into the recoverable local `LogArchive`, then purged from the live tables, so audit history stays reconstructable. The configurable window, pre-purge operator warning, and their defaults are documented in `Docs/USER_MANUAL.md`.
- The alarm snapshot (`exports/alarm_events/alarm_events_state.json`) drops resolved alarms older than 90 days at load and auto-resolves non-critical active alarms older than 14 days at startup.
- Everything else grows without automatic pruning — `image_vault/` binaries, `Images`/`TrainingSamples`/image-learning rows and learned artifacts, `exports/` package folders. This is a known Stage 2 scalability boundary: before pilot-line volumes, plan an orphan-file sweep against the `Images` table, per-project artifact cleanup on project delete, and an export-folder age policy.

## Network posture

- **Offline-first.** Local SQLite is the offline source of truth; inspection, review, and local export need no network connectivity.
- **Central sync is optional, outbound reporting only.** Modes: Disabled (no central aggregation attempted), FileDrop (JSON payloads to a configured folder), RestApi (boundary only in this build; no production client installed), and PostgreSqlBoundary (interface boundary only; no Npgsql package or production database writer bundled). Raw customer images are not uploaded by default; image references are included only when central sync settings explicitly allow image inclusion, and image/package paths can be redacted. Sync failures leave queue items pending for retry; failed central connectivity must not remove or modify local SQLite records.
- **MES/REST is mock/boundary-only in the current build.** Configured modes are Not Connected, Mock REST, or Future Production (planned). The Admin-only mock upload builds a MES-style traceability payload and POSTs it to a configured mock endpoint or writes local JSON when no endpoint is set; every attempt is audited in `MesUploadAttempts`, and failed REST uploads queue in `MesSpoolQueue`. This is local interface evidence only, not production MES/ERP integration; MES authentication is planned for Stage 4.
- Canonical governance: security architecture and the S1–S4 stage threat models in VOL07 §27; transport security and certificate validation in VOL08 §30.4 (CRY-018–CRY-024); protocol and message robustness in VOL08 §29.7.

## Identity and roles

- The current build uses a simple local role selector — Operator, Engineer, Admin — visibly labeled as local demo role selection. It is not MES login and not production authentication. Restricted actions show permission-denied messages and are recorded in the local event log; export, delete, Mock MES upload, and Soak Test actions remain Admin-only. The full role/permission table is in `Docs/USER_MANUAL.md`.
- Canonical governance: VOL07 §28 — roles and identities (§28.1), permissions matrix (§28.2), and authentication, authorization, session, lockout, offline, break-glass, and service identities (§28.3).

## Secrets and credentials

- Canonical rules: Docs/standard VOL08 §30 — secret inventory and exposure prohibitions (CRY-001–CRY-005), secret storage at rest via the DPAPI envelope (§30.2), credential lifecycle (§30.3), transport and certificates (§30.4), cryptographic primitives and key management (§30.5), and secret detection, redaction, and in-memory handling (§30.6).
- Repo and runtime practice: local settings files that can carry endpoints or credentials are git-ignored (see Data at rest); secrets and endpoints are redacted from exported central-sync queue reports when configured; MES queue reports must not expose raw passwords, API keys, bearer tokens, or other secrets.

## Incident response and claim discipline

- Incident response and vulnerability handling are governed by VOL16 §54 (severity model, process and timelines, containment/revocation, notification clocks, IR-001–IR-022). The compliance and standards applicability matrix is VOL16 §55, and its claim-discipline requirements (COM-001–COM-002) are why this repository never claims certification: the project is standards-aligned, not ISO, IEC, ISA, safety, cybersecurity, or regulatory certified, and no wording may imply certification or validated production capability without the required real evidence and formal process.

## Related documents

- `Docs/standard/VOL07_Security_Identity.md` (§27–§28), `Docs/standard/VOL08_Input_Serialization_Crypto.md` (§29–§30), `Docs/standard/VOL16_Privacy_IncidentResponse_Compliance.md` (§46, §54, §55) — the binding rules.
- `Docs/DATA_PIPELINE.md` — schema, storage layout, migration and growth boundaries.
- `Docs/USER_MANUAL.md` — retention settings and role permissions as operated.
- `Docs/DEPLOYMENT.md` and `Docs/RUNBOOK.md` — station setup and operational response.
- `SampleData/README.md` — sample-data handling instructions.
