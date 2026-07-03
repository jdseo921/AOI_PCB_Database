# Manual Test Plan — Run It Yourself

What to test, in order, and the pass criteria that let you confidently say the Stage 1 (image-based) software works. It also lists the states that **look** like problems but are correct-by-design boundaries, so you don't flag them as failures.

Scope: this validates the local prototype — the operator console, the image inspection workflow, and the image-only machine-learning path. It does **not** validate real camera, lighting, robot/PLC, 3D sensor, or production MES; those are hardware/Stage 2+ and stay simulated or Not Connected on purpose.

---

## 0. Get and launch the build

1. Open the GitHub Actions run for **Build Windows App** on branch `claude/aoi-pcb-gui-review-qpqo05` and download the artifact **`AOI_Monitor-windows-x64`**.
2. Unzip to a writable folder, e.g. `C:\AOI\AOI_Monitor`.
3. Run `AOI_Monitor.exe` (self-contained — no .NET install needed). See `HOW_TO_RUN.txt` inside the zip.

**PASS 0:** the window opens maximized at 1920×1080 without an install step and without a crash. First launch creates local storage silently.

---

## 1. Shell smoke test — nothing is broken

- Click every module tile on **Home** and confirm each page opens without an error card or freeze.
- Resize the window; confirm dense pages scroll rather than clip.
- Switch **Role** (Access panel: Operator / Engineer / Admin).

**PASS 1:** all 13 modules open; no page throws the red "Recoverable page error" card; text is readable, not clipped; role switch is reflected in the header.

---

## 2. Core inspection workflow — the heart of the app

1. Open **Image Library → Open Record**, import a PNG/JPG board image (any board photo works for a smoke test).
2. Open **Main Inspection**. Select a view (Top/Side/Bottom). Click **Start**, then **Next Board**.
3. Confirm the image renders in the viewport and the **Defect List** grid populates (No, Type, ROI, Score, Severity, Side, X, Y).
4. Read the **Result** indicator (green OK / red NG / amber REVIEW).
5. Click **Save Result**.
6. Open **Golden Compare**, pick a golden/reference image, and run a comparison; confirm score, verdict, decision reason, and a hotspot overlay appear.

**PASS 2:** an image inspects end to end, the defect list and verdict populate, Save Result persists (visible later in Log & Export), and Golden Compare returns a scored verdict with an overlay.

---

## 3. Machine learning with images — your priority path

Fastest check first (no dataset needed), then the GUI.

**3a. Zero-data pipeline run (from the source tree or a dev box with the SDK):**
```powershell
dotnet run --project AOI_Monitor.Tools -- client-image-learning-demo --synthetic --output .\MlDemoOut --operator you
```
Open `.\MlDemoOut\visual_learning_report.html` and the `visual_evidence`/overlays.

**3b. GUI learning:** Engineer/Admin role → **AI / Models → AI Training Setup** → point Golden / OK Learning / OK Validation / Inspection (and optional NG Validation) at folders of your board images → run → review the on-screen result and the exported `visual_learning_report.html`.

**3c. Batch validation:** **AI / Models → Run Dataset Preflight → Run Batch Inspection → Analyze False Calls**, then **Export Stage 1 Validation Package**.

**PASS 3:** the learning run completes and writes a `visual_learning_report.html`; anomaly overlays land on the actual defects (not random background); the false-call calibration reports a rate at or under the target; batch validation produces accuracy/precision/recall/false-call metrics and an export package. (Full walkthrough: `Docs/Image_Learning_Quickstart_Test.md`.)

---

## 4. Recipe, Calibration, Settings — persistence round-trips

- **Recipe Editor** (Engineer/Admin): load an image, draw an ROI, set type + AI threshold + the tolerance rules (X/Y tolerance, rotation, IPC class, lighting profile, false-call policy), **Save Recipe**, then reload — confirm the ROI and all tolerance values come back. Click **Test Run** with an unsaved edit and confirm the status says it ran against current edits.
- **Calibration** (Engineer/Admin): add ≥2 image↔board point pairs, **Save Profile**, reload — confirm points and the transform summary persist.
- **Settings**: change storage path / engine / a threshold, **Apply**, reopen — confirm it stuck; **Cancel** restores last saved.

**PASS 4:** every save/reload round-trips losslessly; Test Run reflects unsaved edits; Apply/Cancel behave as labeled.

---

## 5. Log & Export, retention, and roles

- Open **Log & Export** as **Operator** — confirm you can *view* Inspection History, Review Events, Export History, and Audit Trail (read-only).
- As **Admin**, export an inspection-history CSV and an audit CSV; confirm files are written.
- As **Operator/Engineer**, confirm export/delete actions are blocked with a permission message (and logged).
- **Settings → Data Retention**: confirm the controls (enable purge, retention days, pre-purge warning) load and Apply.

**PASS 5:** Operator/Engineer can view but not export; Admin can export; retention settings persist; permission denials are recorded in the audit trail.

---

## 6. 3D Profile Viewer — sample-data mode

1. Open **3D Profile Viewer**; confirm it clearly shows **Sample Data Mode** and **3D Camera Not Connected**.
2. Click **Load Height CSV**, load a CSV with `x,y,height` columns.
3. Left-drag to rotate, wheel to zoom, right-drag to pan; click **Reset View**.
4. Click a point on the surface or the 2D inset; confirm the selection, the height slice with peak markers, and the feature/defect list stay in sync. Accept/Reject a feature.

**PASS 6:** the 3D surface renders and is interactive; selection syncs across surface, inset, slice, and list; Accept/Reject records a review event.

---

## 7. Boundaries that are correct — do NOT flag these as failures

These are intentional and honest; seeing them means the app is labeling reality correctly:

- **Camera** shows *No Camera Connected* / Folder Simulation. There is no real camera in this build.
- **Lighting, Robot/PLC, MES** show *Not Connected* / *Simulated* / *Mock*.
- The default engine is the **Pixel Difference Prototype Engine**, not a trained production model; ONNX shows `REVIEW` safely if no valid model is configured.
- Any learned-model or synthetic evidence is labeled **synthetic / not customer acceptance**.
- 3D is **sample CSV only**.

If any of these ever claimed real hardware readiness, *that* would be the bug. Staying labeled is the pass.

---

## 8. When can you confidently say "it works"?

You can confidently state the **Stage 1, image-based software works** when **PASS 0–6 all hold** and the boundaries in section 7 remain clearly labeled. That statement covers: the console and navigation, local image inspection and golden compare, the image-only ML learning + false-call calibration + validation export, recipe/calibration/settings persistence, audit logging with role gating and retention, and the interactive 3D sample viewer.

It does **not** cover real camera acquisition, lighting sync, robot/PLC/safety, live 3D, or production MES — those require the hardware integration and their own acceptance runs, and are correctly out of scope for this build.

Found a defect? Use **Access → Report Issue** or **Export Support Bundle** (redacts paths by default) and note which PASS step failed.
