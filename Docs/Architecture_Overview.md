# Architecture Overview / 아키텍처 개요

Brief bilingual entry point. Details: `DESIGN.md`, `Docs/Architecture_Extension_Guide.md`,
`Docs/Integration_Boundaries.md`, `Docs/Database_Schema.md`, `IMPLEMENTED_FEATURES.md`.

---

## English

**What it is.** A Windows WPF (.NET 10) desktop console for Stage-1, image-only PCB AOI:
load board images, learn "normal" from OK samples, flag anomalies, disposition defects,
and export customer evidence. Camera, robot, and MES are simulated or mocked by design.

**Shell & screens.** `MainWindow` hosts 13 focused workflow pages (Home, Run Inspection,
Golden Compare, Defect Review, Recipe Rules, AI/Models, Yield Analytics, Export & Trace,
Calibration, 3D Profile, Hardware Readiness, Board & Images, System Settings). Shared
session state lives in `WorkflowState`. Visual consistency comes from the shared design
system (`Styles/FactoryHmiLayout.xaml`: color/spacing tokens, button tiers, action bands).

**Inspection engines.** All engines implement `IInspectionEngine`:
- *Pixel Difference Prototype* (default): golden-vs-sample difference; deterministic, labeled prototype.
- *Learned PCB Visual Model v1*: statistical template learning — align (coarse-to-fine),
  brightness-normalize, per-pixel tolerance map, k-th-largest anomaly score, threshold
  calibrated against OK/NG validation sets.
- *ONNX Runtime engine* (optional): runs a locally configured trained model; the seam for a
  future production ML detector (e.g. anomalib-exported). No model ships by default.

**Programming.** Recipes hold normalized ROIs (`RecipeDocument`), drawn by hand or
auto-generated from pick-and-place centroid CSVs (`CentroidRoiImportService`, approximate
placement, review required).

**Evidence & statistics.** False-call/escape rates are reported with exact Clopper-Pearson
95% confidence intervals and PPM (`BinomialConfidence`). `RobustnessStudyService` runs an
MSA-adapted perturbation stability study. Validation packages, threshold sweeps, audit
trail, and export verification are first-class services.

**Data.** Local SQLite (`AoiDatabase`, ~40 tables: inspections, defects, reviews, recipes,
models, audits, exports) plus a managed image vault and per-run export folders. No cloud,
no central DB in Stage 1.

**Quality gates.** Windows CI builds and tests (450+ unit / UI tests), HMI layout audit
(clipping/DPI), PR gates (font/size floors, fixed-width warnings, overclaim wording checks,
repo hygiene), EN/KO localization parity test.

**Extension seams (Stage 2+).** Vendor camera/lighting/robot adapters plug in via the
template projects under `Templates/`; MES integration is a mock boundary awaiting IPC-CFX;
the ONNX slot accepts a validated production model. See `Docs/Stage_Mapping.md`.

---

## 한국어

**쉽게 말해.** AOI(자동 광학 검사)란 사람 눈 대신 카메라 영상으로 전자제품 기판(PCB)의
불량을 자동으로 찾아내는 기술입니다. 이 프로그램은 그 첫 단계로, 카메라 없이 **이미 찍어 둔
사진만으로** 검사 흐름 전체를 검증하는 Windows 데스크톱 프로그램(.NET 10, WPF)입니다.
정상 보드 사진들로 "정상이 어떤 모습인지"를 학습하고, 새 사진에서 정상과 다른 부분을
찾아 표시하며, 검사원이 판정한 결과를 기록하고, 고객에게 제출할 증빙 자료를 만들어 냅니다.
카메라·로봇·생산관리시스템(MES) 연동은 아직 실물이 아닌 시뮬레이션(모의 동작)입니다.

**화면 구성.** 메인 창(`MainWindow`) 아래에 13개의 작업 화면이 있습니다 — 홈, 검사 실행,
기준 비교, 결함 검토, 레시피 규칙, AI/모델, 수율 분석, 내보내기/추적, 캘리브레이션,
3D 프로파일, 하드웨어 준비성, 보드/이미지, 시스템 설정. 화면 전체의 색상·간격·버튼 규격은
공용 디자인 시스템(`Styles/FactoryHmiLayout.xaml`)에서 한 번에 관리하므로 어느 화면이든
같은 모양과 감각을 유지합니다.

**검사 엔진(불량을 찾는 두뇌).** 세 가지가 있고 모두 같은 인터페이스(`IInspectionEngine`)를 따릅니다:
- *픽셀 차이 프로토타입*(기본): 정상 기준 사진(골든 이미지)과 검사 대상 사진을 픽셀 단위로
  비교해 차이가 큰 곳을 찾습니다. 시제품 수준임을 화면에 명시합니다.
- *학습형 PCB 시각 모델 v1*: 정상 사진 여러 장에서 "정상 범위"를 통계적으로 학습합니다.
  사진 위치를 자동으로 맞추고(정렬), 밝기 차이를 보정한 뒤, 픽셀마다 허용 범위를 두고
  그 범위를 벗어난 정도로 이상 점수를 계산합니다. 판정 기준값은 검증용 사진들로 조율합니다.
- *ONNX 엔진*(선택): 별도로 학습시킨 AI 모델 파일을 꽂아 쓰는 자리입니다. 나중에 진짜
  양산용 AI 검출기를 연결하기 위한 통로이며, 기본 모델은 들어 있지 않습니다.

**검사 프로그램 작성.** 레시피(어디를 어떻게 검사할지 정한 설정 묶음)는 검사 영역(ROI)을
담습니다. 직접 그려 넣을 수도 있고, 부품 배치 좌표 파일(픽앤플레이스 센트로이드 CSV)을
불러와 부품마다 검사 영역을 자동 생성할 수도 있습니다(`CentroidRoiImportService`).
자동 생성 위치는 근사치이므로 저장 전 눈으로 확인하는 것이 원칙입니다.

**증빙과 통계.** "멀쩡한 보드를 불량으로 잘못 판정한 비율"(허위 검출률)과 "불량을 놓친
비율"(유출률)을 단순 퍼센트가 아니라 **신뢰구간**(표본이 적을수록 넓어지는, 통계적으로
믿을 수 있는 범위)과 PPM(백만 개당 건수)으로 함께 보고합니다(`BinomialConfidence`).
`RobustnessStudyService`는 사진에 밝기 변화·위치 이동·노이즈를 일부러 가한 뒤에도 판정이
흔들리지 않는지 확인하는 안정성 시험을 수행합니다.

**데이터 저장.** 모든 기록(검사, 결함, 검토, 레시피, 모델, 감사, 내보내기 등 약 40개
테이블)은 PC 안의 SQLite 데이터베이스와 이미지 보관 폴더에 저장됩니다. 1단계에서는
클라우드나 중앙 서버를 쓰지 않습니다.

**품질 관리 장치.** 코드를 고칠 때마다 자동으로 빌드와 테스트(단위/UI 450개 이상)가 돌고,
화면 글자 잘림/배율(DPI) 문제를 잡는 레이아웃 감사, 과장 표현·저장소 위생을 막는 검사,
영어·한국어 번역 누락을 잡는 정합성 테스트가 함께 실행됩니다.

**앞으로의 연결 통로(2단계 이후).** 실제 카메라·조명·로봇은 `Templates/`의 어댑터 틀에
맞춰 연결하고, MES 연동은 국제 표준(IPC-CFX)을 대비한 모의 경계로 준비되어 있습니다.
자세한 단계 계획은 `Docs/Stage_Mapping.md`를 참조하세요.
