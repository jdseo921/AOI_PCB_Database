# Image-Learning Quickstart — Test It Yourself

A linear, hands-on walkthrough for exercising the machine-learning-with-images path end to end and confirming it "folds out nicely." Start with the zero-data smoke test (no dataset required), then run your own board images, then do the same thing through the GUI.

This is a Stage 1, **image-only** learning path. It learns what a good board looks like from OK images and calibrates a false-call threshold. It is not live camera inspection and does not claim production ML acceptance. Deeper references: `Image_Only_PCB_Learning_Workflow.md`, `Client_Image_Learning_Demo_Guide.md`, `Customer_Dataset_Validation_Kit.md`.

---

## 0. One-time setup

- Windows 10/11 x64 with the .NET 10 SDK (to run from source) — or use a published build.
- Point the app/CLI at a clean, writable storage root so each run is reproducible and nothing lands in the repo:

```powershell
setx AOI_MONITOR_STORAGE_ROOT "C:\AOI\Data\AOI_Monitor"
# reopen the terminal so the variable is picked up
```

All commands below run the CLI project from the repo root:

```powershell
dotnet run --project AOI_Monitor.Tools -- <command> <options>
```

(If you built a release, run `AOI_Monitor.Tools.exe <command> <options>` from the publish folder instead.)

---

## 1. Zero-data smoke test — prove the pipeline works (no dataset needed)

This generates synthetic labeled images, learns a visual model from them, calibrates the false-call threshold, produces anomaly overlays, and writes a report. It is the fastest way to see the whole ML-with-images path run green.

```powershell
dotnet run --project AOI_Monitor.Tools -- client-image-learning-demo --synthetic --output .\MlDemoOut --operator you
```

**What to expect:** the command prints a summary (images learned, OK-validation count, recommended threshold, false-call rate, possible-escape status) and writes an evidence folder to `.\MlDemoOut`.

**Confirm it folded out nicely** — open `.\MlDemoOut` and check:

- `visual_learning_report.html` opens and shows the learned reference, tolerance/anomaly summary, and a false-call calibration section.
- `learned_reference.png` and the tolerance map look like a coherent "golden" board, not noise.
- An overlays / `visual_evidence` folder contains anomaly overlays that highlight the seeded defects on the NG samples.
- The report is clearly labeled **synthetic / demo** — that label is correct and expected; synthetic evidence is not customer acceptance.

If all four are present and readable, the learning, calibration, overlay, and reporting stages are all working on your machine.

---

## 2. Learn from your own board images

Lay out one project folder. Image groups map to subfolders (PNG/JPG/JPEG):

```text
LearnProject/
  golden/          approved reference image(s) of a good board
  ok_learning/     >= 5 known-good boards the model learns "normal" from
  ok_validation/   known-good boards held out to calibrate the false-call threshold
  inspection/      boards to inspect after learning (mix of OK and suspect)
  ng_validation/   known-defect boards (optional but needed to prove missed-defect rate)
```

Rules of thumb: at least 5 OK-learning images, at least one OK-validation image (more is better — the false-call threshold is only as trustworthy as this set), and include `ng_validation/` if you want possible-escape evidence.

Run it:

```powershell
dotnet run --project AOI_Monitor.Tools -- learn-from-images `
  --project-folder .\LearnProject --output .\LearnOut `
  --operator you --false-call-target 0.05 --board-model YOUR-BOARD
```

**Read the results in `.\LearnOut`:**

- `visual_learning_report.html` — the human-readable summary. This is the one to review first.
- `learned_reference.png`, `learned_tolerance_map.png` — what the model considers normal and how much variation it tolerates per region.
- `before_after_false_call_report.html` + `threshold_sweep.csv` — how the recommended threshold trades off false calls vs. possible escapes across the sweep.
- `inspection_results.csv` — per-image verdicts for the `inspection/` set.
- `visual_evidence/` — anomaly overlays.

**"Folds out nicely" looks like:** OK-validation images pass at the recommended threshold, the false-call rate is at or under your `--false-call-target`, overlays land on the real defects in NG samples (not random background), and — if you supplied `ng_validation/` — the report gives a possible-escape status instead of "cannot be proven." If `ng_validation/` was empty, the report will honestly say missed-defect rate cannot yet be proven; that is expected, add NG images to close it.

Tuning toward near-zero false positives: raise `--false-call-target` tolerance only after reviewing the sweep; prefer letting borderline boards fall into REVIEW rather than hard-NG. More OK-validation images sharpen the threshold.

---

## 3. Do the same thing in the GUI

1. Launch the app, set your role to **Engineer** or **Admin** (image learning is gated to those roles).
2. Open **AI / Models**, then **AI Training Setup**.
3. Point each image group (Golden / OK Learning / OK Validation / Inspection / optional NG Validation) at the corresponding folder.
4. Run the learning step and review the on-screen result, then open the exported `visual_learning_report.html`.
5. Set the learned model active from **Settings → AI → Learned PCB Visual Models → Set as Active Inspection Model** if you want to inspect with it. (Setting it active does not claim live-camera validation — the label says so.)

To then measure it against a labeled set, use **AI / Models → Run Dataset Preflight → Run Batch Inspection → Analyze False Calls**, and export the Stage 1 package. That batch/validation path is covered step by step in `Customer_Dataset_Validation_Kit.md`.

---

## 4. Quick troubleshooting

- "Missing Golden / Reference or OK Learning images" — add at least one image to `golden/` or at least 5 to `ok_learning/`.
- "Missing OK Validation images" — add at least one image to `ok_validation/`; the false-call threshold needs it.
- "No NG Validation images were provided; possible escapes cannot yet be fully proven" — expected when `ng_validation/` is empty; add known-defect images to prove missed-defect rate.
- Nothing wrote to the output folder — confirm the output path is writable and that `AOI_MONITOR_STORAGE_ROOT` points somewhere you can write.

---

## 5. What this does and does not prove

A clean run here shows the image-only learning, false-call calibration, overlay, and reporting stages work on your data. It is **Stage 1, image-based** evidence. It does not prove real camera acquisition, lighting sync, 3D profiling, robot/PLC, or production MES — those are Stage 2+ and are gated on the hardware you will integrate later. When that camera arrives, the software is ready to receive its frames through the existing camera seam without changing this learning workflow.
