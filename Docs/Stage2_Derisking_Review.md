# Stage 2–4 De-risking Architecture Review

Future-proofing review of every integration seam ahead of the Stage 2 camera pilot —
verifying that the boundaries stay boundaries, and that a real vendor adapter can drop
in without breaking UI, services, configuration, or persisted data. No Stage 2–4
capability was implemented.

| Field | Value |
|---|---|
| Review date | 2026-07-30 |
| Method | 18-agent audit: one auditor per seam (camera, lighting, 3D profile, robot/PLC/safety, MES/traceability, central sync) plus cross-seam configuration/versioning, a written SQLite→PostgreSQL assessment, and localization readiness — each followed by an adversarial verifier requiring a concrete pilot-day failure scenario. 105 findings raised: 101 confirmed, 3 risk/effort-adjusted, 1 refuted. |
| Baseline | commit `6a7f922` |
| Outcome | 12 cheap high-risk fixes shipped with this review (§3); the rest filed as follow-ups DR-01..DR-20 (§5) |

## 1. Verified Strengths (what already holds)

These were re-verified in code by the audit, not assumed:

- **Template hygiene is structural.** All four `Templates/` adapter projects are in
  `AOI_PCB_Database.slnx`, so every Release build compiles them against current
  contracts; `VendorAdapterTemplateTests` additionally loads the built camera/lighting
  templates through the real plugin loaders and enforces zero `PackageReference` in
  templates and no vendor SDK names in the app.
- **Simulation evidence cannot be laundered into hardware claims.** `IsSimulated`
  propagates unaltered through `GenericVisionCameraSource.NormalizeFrame`; camera,
  lighting, 3D, and robot acceptance all force `NOT VALIDATED` for simulated sources;
  the package validator demotes template/fake-marked adapters; all test-pinned.
- **Fail-safe defaults everywhere.** `IntegrationBoundaryRegistry` defaults every seam
  to an honest Null implementation; the Null PLC reports an active fault (absence of
  safety hardware blocks motion); robot safety bypass is opt-in, inert when a PLC is
  configured, and audited; broken plugins degrade to diagnostic null adapters.
- **The MES seam is the most production-ready boundary**: https-only validation, DPAPI
  secrets (test-locked end-to-end), response-schema validation, spool retry/backoff
  with Admin-only abandonment, and a factory-injectable REST client test seam.
- **Persistence discipline**: 30 ordered transactional additive migrations; central-sync
  payloads serialized once at enqueue (`central-sync/v1`) so FileDrop bytes are the
  contract a future REST/PostgreSQL consumer reads; versioned configuration backups
  with restore preview and automatic rollback package.
- **Localization core is language-agnostic**: the visual-tree walker, persist-canonical/
  render-localized seam (byte-level Hangul-free persistence tests), and collision-safe
  reverse mapping all carry over to a third language unchanged.

## 2. Highest-Risk Findings (confirmed)

Ranked by pilot-day impact. "Fixed" = closed by this change set (§3); "DR-xx" = filed follow-up.

| # | Seam | Finding | Status |
|---|---|---|---|
| H1 | Camera | Frame-on-disk contract implicit: pipeline requires `SourcePath` as a readable file, but acceptance never checked it, the template returned `SourcePath: ""`, and the vendor guide omitted it — a metadata-only vendor adapter passes acceptance yet delivers nothing inspectable | **Fixed** (warning + template + guide + tests); hard-fail criteria & pixel-buffer bridging → DR-01 |
| H2 | Robot | `MonitorView` unconditionally overwrote `IntegrationBoundaryRegistry.RobotController`/e-stop with its simulators — a commissioned real controller was silently replaced by opening Main Inspection | **Fixed** (guarded install over Null defaults, previous restored on unload; test-pinned) |
| H3 | Robot | With app-default registry (Null PLC = active fault), every simulated robot command faulted while the panel claimed availability | **Fixed** (simulated PLC accompanies the simulated robot, Null-guarded; test-pinned) |
| H4 | Camera | Real-hardware positive path (`IsRealHardware=true`, Ready) never executed by any test — classification would run first on pilot day | **Fixed** (Ready-source acceptance test incl. DB round-trip) |
| H5 | Lighting | `TcpTextLightingController` — the only viable real Stage-2 transport — had zero tests | **Fixed** (loopback success-bytes/refused/timeout tests) |
| H6 | Central sync | Audit-event feedback loop: sync bookkeeping re-queued itself (~+100 items/pass on an idle line) | **Fixed** (CENTRAL_SYNC_* excluded; regression test) |
| H7 | Central sync | Retry drain silently dropped eligible items once the queue exceeded 1000 rows ("attempted=0" with a growing backlog) | **Fixed** (fetch-by-id; >1000-row test) |
| H8 | 3D | Acceptance hard-failed on ANY NaN height — real profilometers always have dropout, so every real acquisition would fail with no escape | **Fixed** (`MaxNaNFractionPercent` criteria, default 5%, warn below; tests) |
| H9 | MES | Exported payload contract omitted `defectCodes` and had no drift lock against `TraceabilityPayload` | **Fixed** (field added + reflection drift-lock test) |
| H10 | Camera | Soak endurance evidence could never use a real camera (`FolderCameraSource` hard-coded; factory-evidence fields unreachable) | **Fixed** (injectable source, Ready accepted; tests) |
| H11 | Camera | View switch while acquiring never reconnects the adapter, and `NormalizeFrame` relabels whatever arrives with the newly selected view — Top-camera images could be recorded as Side evidence | **DR-02** |
| H12 | Lighting | Vendor lighting plugin loader has no production activation path (no settings field, no factory branch, no UI) — the template + guide point vendors at a dead end | **DR-03** |
| H13 | Lighting | Lighting sync failure is fail-open: acquisition and inspection proceed on stale/wrong illumination with only a status line | **DR-04** |
| H14 | 3D | No 3D adapter template, factory, plugin loader, or persisted source setting — a vendor 3D adapter cannot drop in without rebuilding the app | **DR-05** |
| H15 | Robot | State machine untested against slow/hanging/throwing/mid-cycle-faulting adapters; no per-command timeout exists; e-stop is polled only at command edges (VOL11 N-2) | **DR-06** |
| H16 | Safety | Safety-fault acceptance evidence only producible from the built-in simulator (hard casts), and readiness waives interlock proof exactly for "Real" sources | **DR-07** |
| H17 | Config | Restoring recipe revisions rewrites their identity (new revision IDs/timestamps, duplicates on re-restore, threshold-profile traceability dangles) — untested path | **DR-08** |
| H18 | Config | Corrupt `storage_root_settings.json` silently boots the app against an empty default root ("everything is gone"); corrupt operating-mode/auth files silently downgrade to Demo mode / password-less selector | **DR-09** (needs a product decision on fail-closed startup UX; recommended design in the register) |
| H19 | Localization | Parity scan covers 4 of 18 views; ~560 literals silently render English in Korean mode with no gate (VOL12 LOC-002 unimplemented) | **DR-10** |
| H20 | Safety | `TcpTextPlcSafetyController` reported Ready without any I/O (VOL11 Nonconformity 3) | **Fixed** (class deleted — unreferenced; register update rides the next standard amendment) |

## 3. Fixed With This Review

All shipped with contract tests (`AOI_Monitor.Tests/Stage2DeriskingSeamTests.cs`, 13 tests):

1. `MonitorView` installs its simulators only over Null defaults, restores the previous
   registration on unload, re-registers on Loaded (cached-page navigation), and now
   registers a `SimulatedPlcSafetyController` so the simulation cycle passes the
   fail-safe interlock honestly.
2. `SoakTestService` accepts an injected `ICameraSource` (default unchanged) and accepts
   Ready sources, making real-camera endurance evidence reachable in Stage 2.
3. Camera acceptance warns per view when frames lack a readable on-disk `SourcePath`;
   the template adapter now persists a placeholder frame and documents the obligation;
   the vendor guide lists `SourcePath` as a frame requirement.
4. Real-hardware camera classification (Ready + non-simulated + on-disk frames →
   `IsRealHardware=true`, `FactoryReadinessStatus=PASS`, summary + persistence) is
   test-locked, as is the no-SourcePath WARN path.
5. `TcpTextLightingController` transport contract locked by loopback tests (exact bytes
   on success, operator-safe refused-connection failure, bounded timeout).
6. MES payload contract export gained `defectCodes` and a reflection test that fails on
   any future drift from `TraceabilityPayload`.
7. Central-sync retries fetch queue rows by exact id (`GetCentralSyncItemsByIds`), and
   audit queueing skips CENTRAL_SYNC_* bookkeeping categories (feedback loop closed);
   both regression-tested (incl. a 1050-row backlog draining its oldest item).
8. 3D acceptance tolerates sensor dropout up to a configurable NaN fraction (default
   5%, warn below, fail above; persisted criteria rows default compatibly).
9. `TcpTextPlcSafetyController` removed; configuration backups now list calibration
   profiles in `ExcludedData` so the restore preview is honest about the gap.

## 4. Written Assessment: SQLite → PostgreSQL (spec-allowed option; assessment only)

- **Shape today**: all provider code is confined to `AOI_Monitor/Data` (10 partial
  files, ~9,200 lines) except one `SqliteException` catch in `SettingsView.Refresh.cs`;
  ~600 call sites across Services/Views funnel through ~153 static `AoiDatabase`
  methods. Roughly **70–75% of the DML is portable SQL**; the SQLite-specific surface
  is bounded: `PRAGMA` (integrity/journal), `INSERT OR IGNORE`, `last_insert_rowid`
  patterns, `datetime()` text-date comparisons over ISO-8601 columns, `LIMIT` forms,
  WAL assumptions, and file-size sampling (batch-soak DB metric, DB-health screens).
- **Minimal seam, no rewrite**: a connection + dialect provider behind the existing
  facade (connection factory, id-retrieval helper, upsert/limit/date dialect helpers,
  and a translated `DataStoreException`), leaving all 153 method signatures unchanged.
  Bounded mechanical job on the order of the facade itself — **not** a repository-layer
  rewrite, and it should not start until a customer actually requires PostgreSQL.
- **Do not do now**: no Npgsql reference, no repository abstraction, no dual-dialect
  testing matrix. The central-production-database boundary
  (`NullCentralProductionDatabaseClient`) already models the Stage 4 seam correctly.
- **Adopted discipline for new code** (until the standard's next amendment absorbs it):
  new SQL avoids `INSERT OR IGNORE` (prefer `ON CONFLICT DO NOTHING`), obtains new-row
  ids through the existing helper pattern, keeps provider exception types out of
  Services/Views (the one `SettingsView` catch is queued in DR-20), and treats
  DB-file-size metrics as explicitly SQLite-scoped diagnostics.

## 5. Follow-up Register

Priorities: P1 = before any Stage 2 pilot commitment · P2 = before the relevant stage · P3 = opportunistic.

| ID | Pri | Effort | Item |
|---|---|---|---|
| DR-01 | P1 | moderate | Decide the camera pixel-transport rule: hard-fail acceptance on missing `SourcePath` via criteria (tests updated), or `GenericVisionCameraSource` bridges buffer frames to the image vault. Today's WARN is a stopgap. |
| DR-02 | P1 | moderate | `GenericVisionCameraSource`: reconnect (or refuse frames) on `SelectedView` change while acquiring; never relabel an adapter frame whose view mismatches the request; contract test with a recording adapter. |
| DR-03 | P1 | moderate | Lighting vendor path: either wire `AdapterFolder`/external mode through `LightingControllerFactory` + Settings (mirror camera), or retitle the loader and correct guide/template so vendors target the TCP/serial text protocol. |
| DR-04 | P1 | moderate | Lighting sync failure policy (`BlockAcquisitionOnSyncFailure`, default on for real transports): halt the cycle with an alarm instead of inspecting under wrong illumination. |
| DR-05 | P2 (S2 w/ 3D scope) | moderate | 3D seam parity: `Profile3DAdapterTemplate`, factory + manifest plugin loader, persisted source setting incl. backup coverage; ProfileView `LoadFrame` path so a live sensor is visible in the viewer. |
| DR-06 | P2 (S3) | moderate | Robot state-machine hardening: misbehaving-adapter test matrix (delay/hang/throw/reject/e-stop mid-command), `MaxCommandDuration` with linked cancellation (closes VOL11 N-2). |
| DR-07 | P2 (S3) | moderate | Safety fault-injection contract interface replacing `Simulated*` hard casts in both acceptance harnesses; replace the `SafetySourceKind=="Real"` waiver with recorded hardware-in-the-loop fault evidence. |
| DR-08 | P1 | moderate | Recipe-revision restore preserving identity (idempotent upsert on RecipeName+Revision incl. CreatedAtUtc/Operator/Notes); round-trip test; keeps threshold-profile traceability intact. |
| DR-09 | P1 | cheap+decision | Fail-closed corrupt-settings startup for storage-root / operating-mode / authentication files: block with an explicit operator decision + audit event instead of silently defaulting (storage root) or downgrading to Demo/password-less (security posture). Needs a product decision on lockout UX. |
| DR-10 | P2 (2H-2027) | moderate | Extend localization parity scan to all operator views (grow the honest ledger first — it quantifies the ~560-literal backlog), and extend the extraction regex to Header=/ToolTip=. |
| DR-11 | P2 | moderate | Settings robustness bundle: `schemaVersion` on all settings POCOs, atomic temp-write-then-replace via a shared writer, string-enum serialization, audit-event (not Trace) on load-fallback. |
| DR-12 | P2 | cheap | Adapter manifest `contractVersion` handshake (camera + lighting loaders) with an actionable rebuild message. |
| DR-13 | P2 | moderate | TCP lighting optional ACK/response verification (`ResponseTimeoutMs` currently times a write-only path); serial mode reports unavailable-in-this-build instead of Ready. |
| DR-14 | P2 (S4) | moderate | Central sync: persisted high-watermark queueing (replaces newest-100 windows), `MaxRetryCount` enforcement + abandon action, retention for queue/attempt tables, RestApi-mode honesty (selectable but permanently null today), wire-or-remove `SyncIntervalSeconds`, https-only alignment, doc regeneration from code. |
| DR-15 | P2 (S4) | moderate | MES: spool failed image uploads (retry arm currently unreachable), multipart/auth-header tests, MockMesClient wire parity, status-string constants, background drain decision, per-secret DPAPI fallback with alarm. |
| DR-16 | P2 (S2 w/ 3D) | moderate | 3D measurement honesty: THD-005 (reject non-positive pitch instead of clamping to 1 µm), THD-010 (INVALID verdict for zero-valid-sample ROIs), surface "Height/Volume thresholds are not evaluated until Stage 2 3D" in RecipeView, broaden acceptance exception capture. |
| DR-17 | P2 | cheap | Camera seam small bundle: stop outgoing source on `SetActiveSource`/shutdown, FrameId-uniqueness check, HardwareTrigger semantics documented + tested, unified manifest discovery between loader and package validator. |
| DR-18 | P2 (S3) | cheap | Robot template canonicalization (deprecation banner on the legacy template), e-stop monitor registration line in both templates, reserved `RobotIntegrationSettings` schema, registry-default contract test. |
| DR-19 | P2 | cheap | DB downgrade guard: refuse startup when the database schema version is newer than the build (backup path already has this check). |
| DR-20 | P3 | cheap | Bundle: schemaVersion on 3D/robot acceptance JSON exports, height-map CSV metadata header, ProfileView CSV-parser dedup, move the `SqliteException` catch behind the facade, OPC UA write-rejection assert, `MesUploadResponseContract` single-sourcing, spool/readiness COUNT queries + Sent-row retention, `CentralSyncSettingsService.ResetForTests` in AoiDatabaseTests, FF-LOC-03 scan-term correction. |

## 6. Localization Readiness (2H-2027 third language)

**Honest cost: a moderate structural refactor plus a large translation backlog — not
"a dictionary away", not a rewrite.** What carries over: the language-agnostic walker,
canonical-persistence seam, enum-safe preferences (a `Language=3` value degrades safely
in old builds), and the standard's locale-entry gates (LOC-001/002/011/012, UTF-8-only,
font-fallback, +35% layout expansion rule). What does not: (1) the dictionary is keyed
by literal English strings — one translation per string app-wide, silent orphaning on
copy edits, with a live semantic-shift example in the Compact/Standard/Large preset
entries; (2) 70+ call sites hard-code bilingual ternaries (`korean ? "..." : "..."`)
that need migration to keyed lookups; (3) alarm messages, recommended actions, and
MessageBox text are free-form English persisted at raise time — the LOC-012/013
message-ID catalog is a prerequisite; (4) `DefectTaxonomyEntry` has no localized-name
facet (schema addition should ride the next taxonomy migration so 2027 is data entry,
not migration); (5) evidence reports are EN-only by documented decision (OD-VOL12-2)
with an owner and a revisit deadline. Sequencing: do the structural moves (keyed text
API, centralized language metadata, extended parity scan, taxonomy facet) before
translating anything.

## 7. Configuration & Versioning: Stage 2 Pilot Survival

Verdict: **no destructive migration is required for a Stage 2 pilot** — the schema
migration chain is additive and transactional, model/threshold/recipe/taxonomy stores
are revisioned, and MES/camera settings tolerate field additions. The pilot-day risks
are instead: silent-default settings loads (H18/DR-09, DR-11), the calibration backup
gap (now disclosed; full coverage in DR-08's neighborhood), recipe-restore identity
loss (DR-08), and the absence of a manifest contract-version handshake (DR-12). The
single most likely migration-pain source is the settings-file family (unversioned,
non-atomic writes, tolerant defaults) — DR-11 addresses it as one bundle.
