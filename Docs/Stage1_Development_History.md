# Stage 1 Development History

Complete step-by-step record of how the Stage 1 build was produced, from the first commit (2026-05-17) to the diagnosis-hardened build (2026-07-13). Dates come from the git history where a commit exists; a few off-repo events (document reviews, tool installations) use reasonable estimated dates and are marked *(approx.)*.

Korean phase summary: see [단계별 요약 (Korean Summary)](#단계별-요약-korean-summary) at the end.

---

## What "Stage 1" Means

Stage 1 is the **image-only validation stage**: prove that the software can learn a PCB's normal appearance from customer-supplied images, inspect new images against that learned model, and report defects with statistically defensible false-call and escape estimates — **before any camera, lighting, robot, or MES hardware exists**. Every later stage builds on this foundation:

| Stage | Scope | Status |
|---|---|---|
| **1 — Image validation** | Learn/inspect from image files only; evidence packages; bilingual HMI | **Complete (this document)** |
| 2 — Camera pilot | Live camera + lighting integration, 2D calibration | Prepared (seams + calibration screens exist) |
| 3 — Robot pilot | Robot handling cycle, safety gates | Prepared (simulated cycle + E-Stop boundary exist) |
| 4 — MES / factory | MES writeback, traceability at line rate | Prepared (mock REST + spool queue exist) |

The app deliberately labels everything simulated ("DEMO / SIM ACTIVE", evidence boundaries) so Stage 1 results are never mistaken for factory-integration claims.

---

## Phase Overview

| # | Phase | Dates | Result |
|---|---|---|---|
| 0 | Inception & prototype | 05-17 → 05-25 | Repo, WPF prototype, first Korean text, visual rework |
| 1 | Prototype feature growth | 06-05 → 06-10 | Feature/visual growth, first runtime test, first docs |
| 2 | PoC foundation rebuild | 06-15 → 06-17 | 12-module console, SQLite, image library, analysis engine, async/cancel |
| 3 | Requirements alignment | 06-17 → 06-20 | Build checked against the two customer specification documents |
| 4 | Validation & evidence discipline | 06-21 | Customer-validation framing, evidence verification, end-to-end tests |
| 5 | Demo builds | 06-23 → 06-24 | First demo packages, GUI clipping fixes, Stage-1 test prep |
| 6 | Architecture + claim guardrails | 07-01 | Layout/architecture pass, PR "readiness claim" guardrails |
| 7 | Image-only PCB learning core | 07-02 → 07-03 | The Stage-1 learning workflow, calibration fixes, GUI milestone review, CI + build workflow |
| 8 | Design system & industry statistics | 07-04 | HMI token system, Clopper-Pearson/PPM stats, hotkeys, centroid auto-ROI, robustness harness |
| 9 | Localization integrity | 07-05 | ComboBox token decoupling, KO persistence healing, docs decision |
| 10 | Tooling, diagnostics & CI restoration | 07-05 → 07-10 *(approx.)* | Claude tooling installed, silent-failure fixes, CI made green, quality hooks |
| 11 | 3D profile realism | 07-11 | Real region-volume computation |
| 12 | Statistical rigor & ML pipeline | 07-13 | Held-out estimation, rotation/photometric alignment, PatchCore→ONNX pipeline |
| 13 | Full-project diagnosis & hardening | 07-13 | 12-screen audit, shell-state root-cause fixes, alarm hygiene, async analyzes, security hardening |

---

## Phase 0 — Inception & Prototype (2026-05-17 → 2026-05-25)

- **05-17** — Repository created (`Initial commit`), README added. Decision: Windows desktop app, git-versioned from day one.
- **05-20** — First working prototype committed (`Prototype initial commit`); application title settled.
- **05-25** — First visual design pass plus the first Korean-language text (`Visuals + KOR`), followed by a full visual rework the same day. Bilingual EN/KO operation was a goal from the very beginning, not a retrofit.

## Phase 1 — Prototype Feature Growth (2026-06-05 → 2026-06-10)

- **06-05** — Combined visual + feature expansion on the prototype.
- **06-09/10** — Iteration batch ("June 09 changes"), the first **runtime test with fixes**, and the first **project documentation** committed alongside a refreshed README. This is where the project moved from "code that runs" to "code with a paper trail."

## Phase 2 — PoC Foundation Rebuild (2026-06-15 → 2026-06-17)

The prototype was rebuilt into the real Stage-1 console architecture in a three-day push:

- **06-15** — `Foundation for AOI PoC`: the 12-module workflow shell (Home map → Board & Images, Run Inspection, Golden Compare, Defect Review, Recipe Rules, AI/Models, Yield Analytics, Export & Trace, Calibration, 3D Profile, Hardware Readiness, System Settings). Same day: improved image library (SQLite-backed vault with hashing), a refactored image-analysis engine, the first "Stage 1 AI Model" slot, reports/navigation improvements, and a wording/label cleanup pass.
- **06-16** — Stage-1 adjustments; inspection + AI model refinement; **simulations, role permissions, and error handling** (operator/engineer/admin roles, simulated hardware boundaries); a dedicated **test project** and guided export flows; the main inspection screen initialized as an embedded build.
- **06-17** — Main-inspection fixes and permission updates; **async behavior, progress reporting, and cancellation** patterns introduced; interface adjustments; inspection and stability improvements.

## Phase 3 — Requirements Alignment (2026-06-17 → 2026-06-20)

- **06-17** — `Requirements + consistency check`: the build was systematically compared against the **two customer specification documents** (GUI requirements and product/workflow requirements) *(documents first reviewed approx. mid-June)*.
- **06-20** — `Requirements check-up`: second pass closing the gaps found in the first. From this point, "does it satisfy the spec bullets" became a recurring gate, re-run after every major feature (later re-verified 2026-07-07 *(approx.)* with every Stage-1 bullet passing).

## Phase 4 — Validation & Evidence Discipline (2026-06-21)

A single-day, seven-commit hardening push that converted the PoC into a **customer-validation instrument**:

- PoC usability + architecture improvements.
- `Validations` and `PoC improvement as customer-validation` — the batch-validation workflow (test image folder + optional ground-truth CSV → TP/TN/FP/FN, accuracy/precision/recall).
- `Evidence verification` — exports get SHA-256 checksums and a verification registry so a customer can prove a report wasn't altered.
- `Full build/test verification`, `End2end, sdk templates, configuration`, `Pilot execution discipline works` — end-to-end test coverage and pilot-run discipline (numbered evidence steps).

## Phase 5 — Demo Builds (2026-06-23 → 2026-06-24)

- **06-23** — First demo packages (`Demo`, `Demo June23`).
- **06-24** — GUI clipping fixes found during demo prep; `Test 1 implementation initiate`, `Stage 1 work June24`, and `Dev stage 1 test preparation` — the first structured Stage-1 self-test rehearsal.

## Phase 6 — Architecture + Claim Guardrails (2026-07-01)

- Architecture and layout adjustments across the shell.
- `Add PR readiness claim guardrails` — automated checks that block over-claiming language (e.g., absolute "zero false calls" phrasing) from entering the repo. Honest-evidence wording became machine-enforced, not just policy.

## Phase 7 — Image-Only PCB Learning Core (2026-07-02 → 2026-07-03)

The defining Stage-1 feature landed here:

- **07-02** — `Add image-only PCB learning workflow`: learn a per-pixel reference model from OK images (mean reference + per-pixel tolerance map), inspect new images against it, calibrate a decision threshold from OK/NG validation sets, and persist the learned artifacts so calibration and runtime behave identically. Reinforcements followed the same day.
- **07-02** — `Add GUI and Stage 1 image-learning milestone review report`: a formal milestone review (Docs/Milestone_Review_GUI_and_Image_Learning.md) graded every screen and the learning math; geometry defects and demo-evidence semantics found by that review were fixed the same day. PR quality gate satisfied (rewording + chip sizing), CI line-ending normalization added. Merged as PR #1.
- **07-02 → 07-03** — `Fix image-learning calibration defects L1-L3 with regression tests` (merged as PR #2): threshold-sweep and calibration-parity defects found by review, each pinned by a regression test.
- **07-03** — `Complete GUI spec + non-hardware project completion` (PR #3): the remaining GUI-spec items closed. `Maintainability` pass (PR #4). Polish + handoff: accessibility names for assistive tech across Settings/Monitor inputs, a **camera drop-in seam contract test** (documented seam where Stage-2 cameras plug in), UI smoke-test hardening, image-learning quickstart doc.
- **07-03** — Delivery infrastructure: `Add downloadable Windows build workflow + manual test plan` (GitHub Actions "Build Windows App" producing a runnable artifact) and a Windows dev-setup guide.

## Phase 8 — Design System & Industry Statistics (2026-07-04)

Four large commits in one day, driven by a "boldly, coldly evaluate against industry AOI standards" review:

- `Harden env + fix UI-thread crashes and image-learning P0s` — priority-zero crash fixes.
- `Finish HMI design-system layer` — spacing tokens, shared action-band/form-row styles, all screens migrated, plus an **enforcement gate** that fails the build on hardcoded margins/widths so the design system cannot rot.
- `Industry-standard evidence stats, capability boundaries, operator hotkeys` — **Clopper-Pearson 95% confidence intervals**, DPMO/PPM reporting, minimum-sample-size gates, explicit capability-boundary messaging, and operator hotkeys (F5 start / F6 stop / F7 next / Ctrl+S save) for verification-station ergonomics.
- `Stage-1 competitiveness: centroid auto-ROI, robustness study, alignment, EN/KO parity, cleanup` — centroid-CSV auto-ROI import (pick-and-place data → ROIs), an **MSA-adapted robustness/stability study harness** (brightness/offset/noise perturbation families), image alignment improvements, and a localization-parity audit with drift tests.

## Phase 9 — Localization Integrity (2026-07-05)

- `Remove self-referential Home tile` — menu #1 pointed at itself; removed after user review.
- Three-commit localization integrity chain: `Decouple ComboBox display text from persisted/compared values` (invariant tokens), translate the freed recipe combo literals, and `Heal recipes persisted with Korean tokens by pre-ComboBoxTokens builds` — meaning switching the UI language can never corrupt saved recipes, and old data self-heals.
- `Scrub external business-plan specifics from public docs` followed by its **revert** the same day — a deliberate owner decision to keep the roadmap docs intact on the repo.

## Phase 10 — Tooling, Diagnostics & CI Restoration (2026-07-05 → 2026-07-10)

- **07-05 *(approx.)*** — Development tooling installed and wired up: .NET analysis/test plugins, a desktop-automation MCP for driving the real WPF app (screenshots + UIA), Stryker mutation-testing CLI (blocked on WPF projects; documented), a project `stage1-gate` release-quality skill, and a `wpf-ui-verifier` agent. GitHub MCP added for CI visibility.
- **07-10** — `Detection quality + silent-failure fixes from tool-driven diagnostics`: the new tooling was used to sweep the codebase for silently-swallowed exceptions and detection-quality issues.
- **07-10** — `Fix order-dependent flake in ClientDemoReadinessGateTests` (cross-process artifact race made deterministic).
- **07-10** — `Fix .NET CI: format drift, CA2000, and NotImplementedException Dispose stubs; add quality hooks` — CI had been red since 07-05 in three layers (format drift → analyzer promotion → seven `Dispose()` stubs throwing). All fixed; **push-gate and stop-build hooks** added so a red push cannot happen silently again. From this commit forward, CI green is continuously verified.

## Phase 11 — 3D Profile Realism (2026-07-11)

- `3D Profile: real region volume replaces the height-deviation placeholder` — the 3D height-map viewer's volume metric became a real flood-fill region-volume computation instead of a placeholder, with tooltips explaining the acceptance math.

## Phase 12 — Statistical Rigor & ML Pipeline (2026-07-13)

A deliberate audit asked: *"which parts of the learning pipeline are 'vibe programming' rather than defensible engineering?"* Seven weaknesses were identified and all seven closed:

- `Learning rigor: held-out false-call estimation + small-angle rotation alignment`
  - **#2 Held-out methodology**: calibration now splits OK-validation images (even/odd, minimum 10) so the headline false-call rate is measured on images the threshold never saw; status only reads OK when the held-out rate meets target.
  - **#3 Rotation**: rigid alignment extended beyond translation with a ±2° small-angle rotation search (0.5° coarse, ±0.25° refine, bilinear sampling, bit-identical at 0°).
- `ONNX slot accepts anomaly heat-map models + anomalib training pipeline`
  - **#1 Learned model**: a real ML path — anomalib **PatchCore** (resnet18, 5% coreset) trained on the benchmark dataset and exported to ONNX; the app auto-detects heat-map outputs (`AnomalyHeatmapOutputParser`) and converts anomaly maps into region detections. Benchmark: **0/15 held-out false calls, 0/20 escapes, AUROC 0.9999**.
- `Learning pipeline rigor #4-#7: photometrics, sampling, small-N stability, perturbation coverage`
  - **#4 Photometrics**: global mean-brightness normalization + large-kernel illumination flattening, recorded in the model artifact and honored identically at inspection time.
  - **#5 Sampling**: box-average downsampling replaces nearest-neighbor (no aliasing).
  - **#6 Small-N stability**: James-Stein-style variance shrinkage (pseudo-count pooling) so tiny training sets don't produce degenerate tolerance maps.
  - **#7 Perturbation coverage**: robustness study extended with rotation and blur families (15 variants across 5 families, deterministic noise).
  - After #4–#6 the *statistical* engine also reached **0/15 held-out false calls** on the 640-px benchmark dataset.
- Python side: `Scripts/ml/train_patchcore.py`, `Scripts/ml/evaluate_onnx.py`, and `Docs/ONNX_Model_Training.md` document the training environment (uv-managed Python 3.11, torch CPU, anomalib 2.5) end to end.

## Phase 13 — Full-Project Diagnosis & Hardening (2026-07-13)

A comprehensive diagnosis walked **all 12 screens** of the running build with desktop automation (screenshot evidence per screen) plus code-level security/reliability/scalability checks, then fixed everything found (`Diagnosis fixes: shell state integrity, alarm hygiene, layout clipping, UI-thread analyzes, hardening`):

1. **Shell state integrity (root cause)** — the localization walker stored each text element's first-seen text and rewrote it on every re-apply, so any runtime status text (alarm counts, mode badge, footer, even the console title) was stomped back to its XAML default whenever preferences re-applied — visibly corrupting the header after merely opening System Settings. Fixed by tracking the last-applied value and re-keying when the app rewrote the text; Settings page load made side-effect free; dynamic chrome refreshed after preference changes. Regression tests added.
2. **Alarm hygiene** — alarms persisted forever (a healthy demo booted with a red "35 alarm" banner). Now: startup auto-expiry of non-critical alarms older than 14 days, an **Acknowledge All** action, snapshot pruning of resolved alarms older than 90 days, and header flyouts close on navigation.
3. **Layout clipping on six screens** — AI/Models timing metrics unclipped; Recipe ROI panel widened with scrollbar padding; Export & Trace filter row, date pickers, and readiness panel fixed, tab headers now wrap instead of hiding off-screen; Defect Review risk badges unclipped; Calibration Browse button unclipped; Home engine chip shortened so all eight status chips fit one row.
4. **UI-thread freezes** — every heavy image analysis moved off the UI thread (Recipe Test Run, Compare Show Difference, Library Compare Golden, Monitor RunInspection including a robot-cycle path that only *looked* async), with re-entry guards.
5. **Security hardening** — single-instance mutex (two instances would race the shared SQLite/JSON state), PBKDF2 iterations raised 120k → 600k for new password hashes (per-record backward compatible). The accompanying audit confirmed: zero vulnerable NuGet packages, fully parameterized SQL, no plaintext secrets, no insecure HTTP, constant-time password comparison.
6. **Documentation** — data-growth/retention boundary documented in `Docs/Database_Schema.md` (what auto-prunes vs. what needs a Stage-2 retention plan).

Verification for the final build: Release build 0 errors, **508 unit + 12 UI tests, 0 failures**, repo quality gates PASS, synthetic image-learning evidence smoke PASS (100% false-call reduction on the synthetic set), self-contained publish + launch smoke PASS, **both CI workflows green** on `75eb99e`.

---

## Quality & Delivery Infrastructure (accumulated across phases)

- **Tests**: 520 total (508 unit including localization parity, statistics, learning-pipeline and alarm regression suites; 12 STA UI tests including an HMI layout audit).
- **CI**: `.NET CI` (build + format verify + analyzers + full tests) and `Build Windows App` (downloadable artifact) on every push to `main`.
- **Local gates**: `Scripts/run-quality-gates.ps1` (hygiene, PR-quality wording, code quality, package validation), `Scripts/check-code-quality.ps1`, push-gate hook (blocks `git push` unless build+hygiene+quality pass), stop-build hook, and the `/stage1-gate` release loop (Release build → both suites → gates → synthetic evidence smoke → publish + launch smoke).
- **Delivery**: self-contained single-file `win-x64` publish (~154 MB, no .NET install required), test build maintained at `Desktop\AOI_Monitor_TestBuild\AOI_Monitor.exe`; app data lives in `%LOCALAPPDATA%\AOI_Monitor`.

## Evidence Snapshot (as of 2026-07-13)

| Metric | Statistical engine | PatchCore ONNX |
|---|---|---|
| Held-out false calls (benchmark set) | 0 / 15 | 0 / 15 |
| Escapes (NG detection) | 0 / 20 | 0 / 20 |
| Separation / AUROC | threshold sweep with guardrail | 0.40 separation, AUROC 0.9999 |
| Confidence reporting | Clopper-Pearson 95% CI + PPM + min-N gates | same reporting path |

*(Benchmark: local 640-px dataset; numbers are dataset-specific evidence, not universal claims — the in-app wording enforces this.)*

## Current Status & Known Boundaries

- **Stage 1 is complete and verified**: every bullet of the two customer specification documents passes; the learning pipeline is statistically defensible; the HMI is bilingual and design-system enforced; CI is green; a runnable build is delivered.
- **Known boundaries** (tracked, not hidden): image vault / learning tables grow without automatic pruning (retention plan needed before Stage-2 volumes); Stryker mutation testing blocked on WPF project structure; camera/lighting/robot/MES paths are simulated seams awaiting Stage-2+ hardware; xunit v3 migration available.

---

## 단계별 요약 (Korean Summary)

| 단계 | 기간 | 내용 |
|---|---|---|
| 0. 시작 및 프로토타입 | 05-17 ~ 05-25 | 저장소 생성, WPF 프로토타입, 첫 한국어 텍스트, 비주얼 개편 |
| 1. 프로토타입 기능 확장 | 06-05 ~ 06-10 | 기능/비주얼 확장, 첫 런타임 테스트, 첫 문서화 |
| 2. PoC 기반 재구축 | 06-15 ~ 06-17 | 12개 모듈 콘솔, SQLite 이미지 보관소, 분석 엔진, 비동기/취소 처리 |
| 3. 요구사항 정합성 | 06-17 ~ 06-20 | 고객 사양 문서 2종 대비 전수 점검 |
| 4. 검증·증빙 체계 | 06-21 | 고객 검증 워크플로, SHA-256 증빙 검증, 엔드투엔드 테스트 |
| 5. 데모 빌드 | 06-23 ~ 06-24 | 첫 데모 패키지, GUI 잘림 수정, 1단계 자체 테스트 준비 |
| 6. 아키텍처·표현 가드레일 | 07-01 | 레이아웃 정비, 과장 표현 차단 자동 검사 |
| 7. 이미지 전용 PCB 학습 핵심 | 07-02 ~ 07-03 | 학습 워크플로(기준 이미지+허용오차 맵), 보정 결함 수정, 마일스톤 리뷰, CI/빌드 배포 체계 |
| 8. 디자인 시스템·산업 통계 | 07-04 | HMI 토큰 시스템+강제 게이트, Clopper-Pearson 95% 신뢰구간·PPM, 단축키, 센트로이드 자동 ROI, 견고성 스터디 |
| 9. 현지화 무결성 | 07-05 | 콤보박스 토큰 분리(언어 전환이 저장 데이터를 훼손하지 않음), 기존 데이터 자동 복구 |
| 10. 도구·진단·CI 복구 | 07-05 ~ 07-10 | 진단 도구 도입, 침묵 실패 수정, CI 3중 결함 복구 + 품질 훅 |
| 11. 3D 프로파일 실측화 | 07-11 | 영역 체적 실계산 도입 |
| 12. 통계적 엄밀성·ML 파이프라인 | 07-13 | 홀드아웃 검증, 회전/조명 정렬, PatchCore→ONNX 학습 파이프라인 (홀드아웃 오검출 0/15, 미검출 0/20) |
| 13. 전면 진단·하드닝 | 07-13 | 12개 화면 전수 감사, 셸 상태 근본 원인 수정, 알람 위생, UI 스레드 분석 이동, 보안 강화, CI 그린 |

**현재 상태**: 1단계 완료·검증됨 — 사양 문서 전 항목 충족, 테스트 520건 통과, CI 그린, 실행 가능한 단일 파일 빌드 제공 (`Desktop\AOI_Monitor_TestBuild\AOI_Monitor.exe`).
