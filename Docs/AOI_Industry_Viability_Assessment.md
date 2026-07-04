# AOI Industry Viability & Usability Assessment (cold review)

Scope: a strict, unsentimental evaluation of AOI_Monitor against how real Automated Optical
Inspection is built, benchmarked, and deployed. Written to be useful, not flattering. It also
records the targeted improvements made in this pass and where they sit against industry practice.

Benchmarks referenced: **IPC-A-610** (acceptability of electronic assemblies), **IPC-CFX / IPC-2591**
(Connected Factory Exchange), **IPC-Hermes-9852** (board hand-off), **IPC-2581 / DPMX** (design data),
**AIAG MSA / Gage R&R** (measurement validation), **DPMO/PPM + Clopper-Pearson** (performance
statistics). Open-source / commercial reference points: **Intel anomalib** (PatchCore/PaDiM
unsupervised anomaly detection), **OpenPnP** (fiducials, CvPipeline), **OpenCV/HALCON/VisionPro**,
and 2D/3D AOI vendors (Koh Young, CyberOptics, Omron, TRI, Saki, ViTrox, Mirtec, GÖPEL).

---

## Bottom line

- **As a production AOI machine: not viable — and it correctly does not claim to be.** The gap is
  large and structural, not a matter of polish.
- **As a Stage‑1, image‑only proof of concept and customer‑evidence tool: viable, and unusually
  disciplined.** Its honesty about what it is not is a genuine competitive asset in a market full of
  over‑claiming. But two of its core pieces — the shipped detection engine and hand‑drawn recipe
  programming — are **placeholders that must be replaced, not incrementally improved.**

The realistic route to a real product is: swap the statistical engine for a trained/validated model
via the existing ONNX seam → add CAD/BOM‑driven programming → Stage 2 camera + lighting + 3D →
run an MSA capability study → integrate via IPC‑CFX/Hermes. Everything after Stage 1 is genuinely
hard and mostly unbuilt (by design).

---

## The bar: what real AOI does

1. **CAD/BOM/Gerber‑driven programming.** The inspection program is auto‑generated from design data
   (IPC‑2581/ODB++, centroid/pick‑place files) against a **per‑package algorithm library** — not drawn
   by hand. A real board has hundreds to thousands of components.
2. **2D *and* 3D.** Side‑angle and height/volume measurement for solder joints (fillet, coplanarity,
   volume) — a large fraction of IPC‑A‑610 solder defects are dimensional and invisible to a single
   top‑down 2D image.
3. **Measurement validation.** Fiducial alignment, lighting calibration, and a **Gage R&R / MSA**
   capability study proving repeatability *and* reproducibility before production.
4. **Performance stated statistically.** Escape rate and false‑call rate as **DPMO/PPM with confidence
   intervals**, from capability studies — never a bare percentage. Production false‑call targets are
   typically in the low hundreds of DPM.
5. **Throughput matched to line takt** (cm²/s or components/s), with closed‑loop feedback to SPI /
   placement.
6. **Standard data exchange.** IPC‑CFX (IPC‑2591, MQTT) to MES, IPC‑Hermes‑9852 board hand‑off,
   OPC UA, SPC control charts, and a verification/repair‑station loop with operator hot‑keys and
   defect codes.

---

## Cold gap analysis

### A. Detection method — the deepest gap
- The **default engine is pixel‑difference** (golden vs. sample). That is 1990s reference‑comparison
  AOI: extremely sensitive to lighting and alignment (→ high false calls), and it **cannot classify**
  a defect — only "this region differs." Modern AOI abandoned pure reference‑comparison for
  package‑based algorithms + AI.
- The **"learned visual model" is a statistical mean + tolerance map** (anomaly by deviation). It is a
  crude approximation of what unsupervised anomaly detection does properly. The honest, high‑leverage
  path is already wired: **train Intel `anomalib` (PatchCore / PaDiM) on OK boards, export to ONNX,
  and plug it into the existing `OnnxInspectionEngine`.** As shipped, the statistical engine is a
  placeholder, not a competitive detector.
- **No 3D.** Solder Volume, Coplanarity, Pin/Component Height cannot be measured from 2D at all. This
  pass now encodes that boundary explicitly (see Improvements → capability reference).

### B. Programming / recipe — the biggest *usability* gap
- **No CAD/Gerber/BOM import.** ROIs are drawn by hand. That does not scale to a production board and
  is simply not how engineers program AOI. This is the single largest usability gap versus industry.
- Board alignment is a pixel search radius, not fiducial‑based registration.

### C. Measurement validation — industry‑critical, mostly absent
- **No Gage R&R / MSA.** The engine is deterministic (same image → same score, so raw repeatability is
  trivially perfect), but there is no reproducibility study across boards, lighting, or placement.
- Performance reporting **used to be a bare percentage** — the amateur tell in any AOI evaluation.
  This pass replaces it with exact **Clopper‑Pearson confidence intervals + PPM** (see Improvements).
  What remains missing is an NG dataset large enough to *bound the escape rate* meaningfully.

### D. Factory integration — deferred, long road
- Only **mock MES JSON**. Industry standard is **IPC‑CFX** to MES and **IPC‑Hermes‑9852** for line
  hand‑off; neither exists. SPC is prototype data, not live control charts. (Correctly scoped to
  Stage 4, but it is a substantial build.)

### E. Usability / HMI — mixed, improved this pass
- **Good:** disciplined labeling, high‑contrast industrial HMI, and now a consistent spacing design
  system plus **operator hot‑keys** on the Inspection and Review screens (verification‑station
  ergonomics).
- **Still missing vs. industry stations:** CAD‑linked board‑map navigation, taxonomy‑linked defect
  quick‑codes at the verification step, a live yield/throughput dashboard on the operator screen, and
  a repair‑loop routing model.

### F. Throughput — unproven
- The tool inspects uploaded images with no throughput budget or takt alignment. Real AOI quotes
  cm²/s. Until Stage 2, throughput is simply not a defined quantity here.

---

## What is genuinely good (cold but fair)

- **Scope honesty is a real asset.** Guardrails, evidence‑gated stage exits, and "do not over‑claim"
  discipline are rare and valuable — most vendors' marketing fails this bar.
- **Solid engineering underneath:** 400+ automated tests, Windows CI + quality gates, clean
  architecture, and — importantly — the **ONNX and adapter seams are placed exactly where Stage 2
  needs them.** The scaffolding is real.
- **An IPC‑A‑610‑aligned defect taxonomy already exists** (14 canonical classes, aliases, MES codes).

---

## Improvements made in this pass (tied to the standards above)

1. **Statistical performance reporting — `BinomialConfidence` / `RateEstimate`.** False‑call and
   escape rates are now reported as **exact Clopper‑Pearson 95% confidence intervals + PPM/DPMO**,
   the way MSA/Six‑Sigma and every serious AOI capability study report them. On small samples the
   interval widens honestly instead of hiding or over‑stating a percentage. (Backend, unit‑tested
   against published reference intervals.)
2. **Per‑defect detection‑capability reference — `DefectDetectionCapability`.** Each IPC‑A‑610 class is
   tagged with what is actually required to detect it: 2D anomaly, trained classifier, or 3D hardware.
   This is the honest equivalent of a vendor capability sheet and prevents the app from ever implying
   it can detect a 3D‑only defect (Solder Volume, Coplanarity, Pin Height) from images. (Backend,
   unit‑tested.)
3. **Operator‑station ergonomics.** Hot‑keys with on‑screen hints — Inspection (F5 Start / F6 Stop /
   F7 Next / Ctrl+S Save) and Review (1 Confirm NG / 2 False Call / 3 Possible Escape / 4 Hold) —
   suppressed while typing. Plus the design‑system spacing pass across screens. (Frontend.)

---

## Prioritized recommendations (highest leverage first)

1. **Replace the statistical engine with a trained anomaly model.** Train `anomalib` (PatchCore) on OK
   boards, export ONNX, run it through the existing `OnnxInspectionEngine`. Single biggest jump in
   detection quality, and it reuses the seam already built. *(ref: Intel anomalib, MVTec‑AD‑style
   unsupervised inspection)*
2. **CAD/BOM‑driven programming.** Import IPC‑2581/Gerber + centroid to auto‑place ROIs per component;
   fiducial‑based registration. Removes the hand‑ROI scaling wall. *(ref: OpenPnP, IPC‑2581, OpenCV)*
3. **MSA capability harness.** A repeatability + reproducibility (Gage R&R) run and report, gating any
   "accuracy" claim. *(ref: AIAG MSA)*
4. **IPC‑CFX (IPC‑2591) messaging** to MES in place of mock JSON; **IPC‑Hermes‑9852** for line
   hand‑off. *(ref: IPC‑CFX SDK)*
5. **3D acquisition path (Stage 2)** to unlock the defect classes the capability reference now flags as
   3D‑only.
6. **Lighting calibration + fiducial alignment** to attack the false‑call rate at its root cause
   (illumination/registration variance). *(ref: OpenCV, OpenPnP)*

---

## Verdict, stated plainly

Viable **today** as a Stage‑1 image‑based demonstrator and customer‑evidence tool, with better
engineering discipline and honesty than much of the market. **Not** viable as production AOI, and a
long, genuinely hard road (Stages 2–4 + a real detector + CAD programming + MSA + CFX) separates the
two. The scaffolding and the honesty are the assets worth building on; the shipped detector and the
hand‑drawn programming are the parts to replace outright.
