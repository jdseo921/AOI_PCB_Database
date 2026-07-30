# Stage 1 Soak Test Procedure (8-Hour Batch-Inspection Stability Evidence)

This procedure produces auditable evidence for the customer acceptance criterion
"stable operation for 8-hour continuous PoC testing" (gap-audit ID ACC-11-03) using the
headless batch-inspection soak harness (`batch-soak` in `AOI_Monitor.Tools`).

## Evidence Boundary (read first)

> Stage 1 uploaded-image batch-inspection soak evidence. Frames come from a local image
> folder processed by the offline batch-inspection pipeline. This is **not** live camera
> acquisition, lighting, robot/PLC, safety, or MES evidence, and it does **not** satisfy
> Stage 2–4 hardware readiness gates.

This scope statement is embedded in every generated report and in the console banner.
Do not present a batch soak report as camera or factory-automation stability evidence.
Live-source soak testing remains the separate Admin soak tool in `Export & Trace`
(Folder Camera Simulation) and, from Stage 2 on, real camera sources.

## What the harness does

Each *pass* runs the real batch-inspection pipeline — the same one used by the AI Model
Test screen and the `stage1-exit` CLI — over every PNG/JPG/JPEG in the configured folder
(with an optional ground-truth manifest), then records:

- pass duration; per-image inspection timing (average, max, count over 1 second)
- verdict counts (OK / NG / REVIEW / ERROR)
- managed memory (sampled after a full GC so the series reflects surviving objects),
  working set, handle count, thread count
- SQLite database size (including WAL/SHM sidecars)
- error events, plus any alarm-service events raised during the run window

The run **fails** on any of:

| Condition | Report fail reason |
|---|---|
| Unhandled exception escaping the pipeline | `UnhandledException` |
| An inspection exceeding the stuck-iteration watchdog (default 5 min) | `StuckIteration` |
| Sustained managed-memory growth trend (default: slope > 64 MB/h over the second half of samples AND total growth > 256 MB; evaluated only after a warm-up of a quarter of the requested duration so short runs stay informational) | `MemoryGrowthTrend` |
| No readable images in the folder | `NoImagesFound` |
| Every image in a pass erroring | `EveryImageFailed` |
| Process crash | non-zero exit + `crash_marker.txt` in the run folder |

Expected per-file errors (an unreadable image among readable ones) are recorded and
tolerated, matching the batch pipeline's bad-file-skip convention. A SQLite
persistence failure mid-run is recorded as an error and disables further batch-run
persistence instead of aborting the run, so the evidence survives. Durations are
measured monotonically (Stopwatch), so a system clock step cannot inflate the 8-hour
claim.

Evidence artifacts per run (written to a timestamped `batch_soak_<stamp>` subfolder):

- `batch_soak_report_<stamp>.html` — human-readable report (run ID, software version,
  engine/model configuration incl. detection priority and threshold profile, dataset
  file-list fingerprint, stability metrics, failure conditions, pass samples)
- `batch_soak_report_<stamp>.json` — full machine-readable result including every pass
- `batch_soak_passes_<stamp>.csv` — per-pass metric series; the leading `#` comment
  lines carry the scope statement and run identity so the file stays truthful when
  detached from the run folder
- `soak_debug.txt` — engineer-facing full exception traces, written only when an
  unhandled failure occurred (operator-facing reports carry type + message only)
- An `ExportHistory` + `ExportVerification` record (SHA-256) is written to the local
  SQLite database for the run folder.

By default each pass is also persisted as a batch test run in SQLite (realistic
sustained database load; this is what makes the SQLite-growth metric meaningful).
`--no-persist-batch-runs` disables that. Note that the AI / Models screen shows the
latest persisted batch run, so after a soak the "latest run" will be a soak pass;
re-run your validation batch afterwards if needed.

## Prerequisites

1. Windows 10/11 machine with the repository built in Release
   (`dotnet build AOI_PCB_Database.slnx --configuration Release`), or a published Tools binary.
2. A dataset folder of PNG/JPG/JPEG board images. For a rehearsal you can generate one:
   `pwsh SampleData/demo_dataset_generator.ps1` (use `SampleData/DemoSet_Quick/images`).
   An optional customer-validation manifest CSV adds ground-truth labels to each pass.
3. **Disable sleep/hibernation and Windows Update restarts for the run window**
   (Settings > System > Power: set sleep to Never while plugged in). A machine that
   sleeps mid-run invalidates the evidence.
4. Enough disk space for image processing plus database growth (check the smoke run's
   reported growth and extrapolate; the 8-hour run multiplies pass count accordingly).
5. Decide the storage root: the harness writes to the same local database as the app
   (default `%LOCALAPPDATA%\AOI_Monitor`, or set `AOI_MONITOR_STORAGE_ROOT` to isolate
   the soak from your working data).

## Step 1 — Smoke rehearsal (5 minutes, required before any 8-hour claim)

```powershell
dotnet run --project AOI_Monitor.Tools -c Release -- batch-soak `
  --images SampleData/DemoSet_Quick/images `
  --output TestResults/batch-soak `
  --operator <your-id> `
  --profile smoke
```

Confirm: exit code 0, `PASS` in the console summary, and the three report files in the
new `TestResults/batch-soak/batch_soak_<stamp>/` folder. Open the HTML report and check
the scope banner, engine/model configuration, and pass table are populated.

## Step 2 — Full 8-hour run

```powershell
dotnet run --project AOI_Monitor.Tools -c Release -- batch-soak `
  --images <dataset-folder> `
  --manifest <manifest.csv> `
  --output TestResults/batch-soak `
  --operator <your-id> `
  --profile eight-hour
```

Operational notes:

- The console prints one progress line per pass (elapsed, remaining, images, errors,
  memory, handles). Leave the window open; do not log off.
- Ctrl+C requests cancellation: the in-flight image is abandoned, partial evidence is
  written, and the report is labeled `CANCELED` (a canceled run is **not** 8-hour
  evidence).
- Useful options: `--duration-minutes <n>` (custom duration), `--delay-seconds <n>`
  (pause between passes, default 2), `--engine pixel-difference|onnx|learned-pcb-visual`
  (default is the configured engine selection; the pixel-difference prototype unless an
  ONNX/learned model is configured and ready), `--priority balanced|minimize-false-positives|maximize-defect-recall`
  (default balanced; recorded in the evidence), `--stuck-timeout-minutes <n>`,
  `--memory-slope-fail-mb-per-hour <n>` / `--memory-growth-fail-mb <n>` (trend failure
  thresholds), `--board-model <name>` / `--lot-id <id>`. Unknown or duplicated options
  are rejected with exit code 2.

## Step 3 — Interpret and preserve the evidence

1. Exit code 0 and `Result: PASS` with `8-hour uploaded-image PoC evidence: YES` in the
   report means the acceptance-criterion run completed with no failure conditions.
   Any `FAIL` reason, `CANCELED` status, or `crash_marker.txt` means the run is not
   acceptance evidence — investigate, fix, and re-run.
2. Review in the HTML report: managed-memory trend line (should be "within bounds"),
   handle count start/end/peak (should be flat-ish, not monotonically climbing),
   SQLite growth (should be roughly linear with passes when persistence is on),
   count over 1 second, and the alarm-events section.
3. Preserve the entire `batch_soak_<stamp>` folder with the release-candidate evidence
   set (e.g., alongside the Stage 1 validation package). The export-verification record
   in SQLite ties the folder to a SHA-256 at generation time.
4. Record the run in your Stage 1 evidence log with: run ID, software version, engine +
   model configuration (all in the report header), dataset used, and operator.

## Relationship to other stability evidence

- `Export & Trace > Soak Test` (in-app, Admin): single-image inspection cycles over
  Folder Camera Simulation — exercises the camera-source seam; complements this harness.
- `Export & Trace > UI Stability Soak`: drives the WPF shell navigation — UI-level
  stability; the client-demo gate consumes it.
- This `batch-soak` harness: headless, batch-pipeline, machine-parseable metrics with
  crash/memory-trend/stuck watchdog failure semantics — the artifact intended for the
  8-hour acceptance criterion at Stage 1 scope.

Automated regression coverage for the harness itself lives in
`AOI_Monitor.Tests/BatchSoakTestServiceTests.cs` (short-duration smoke, memory-trend
evaluation, stuck-iteration watchdog, unhandled-exception handling, truthful-labeling
checks, and CLI argument validation).
