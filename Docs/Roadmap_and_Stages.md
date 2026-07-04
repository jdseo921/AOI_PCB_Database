# Roadmap & Stages / 로드맵 및 단계

Brief bilingual planning summary. Details: `Docs/Stage_Mapping.md`,
`Docs/Milestone_Status_Stage1_Exit_Stage2_Camera_Pilot.md`, `Docs/Stage1_Acceptance_Checklist.md`.

---

## English

| Stage | Scope | Status |
|---|---|---|
| **1 — Image Upload & AI Learning** (8 wks) | Upload PNG/JPG, offline learning/inference, defect overlays, batch validation, CSV/PNG/PDF evidence export, customer validation | **Code-complete.** Exit is evidence-gated: requires a real customer dataset run and reviewed validation package |
| **2 — AI Vision Camera Integration** (8 wks) | GigE/USB3 cameras, lighting control, real-time acquisition, Top/Side/Bottom views, on-site validation | Architecture seams ready (adapter templates, folder-camera simulation); no real hardware yet |
| **3 — Robot Integration** (12–16 wks, 2027) | Load→Inspect→Unload automation, trigger sync, safety interlock/E-stop | Software simulation panel only |
| **4 — MES/ERP Integration** (12–16 wks, 2027) | REST/OPC UA (target: IPC-CFX), lot traceability, MES authentication | Mock MES boundary only |

**Commercial plan.** 2026: Phase 1 (Stages 1–2) PoC + customer validation. 1Q 2027: first
release (1 customer, 5–10 licenses). 2027: Korea-first rollout, then ASEAN/Japan/Europe.

**Stage-1 exit blockers (the only open items before camera work):** run real customer
images through learning/validation, review the generated evidence package, record model
acceptance (or scope to prototype engine), preserve build/test evidence.

**Post-Stage-1 technical priorities** (from `Docs/AOI_Industry_Viability_Assessment.md`):
trained anomaly model via the ONNX seam → CAD/BOM-driven programming at scale →
MSA capability study → IPC-CFX/Hermes integration → 3D acquisition.

---

## 한국어

**쉽게 말해.** 이 프로젝트는 "사진만으로 검증 → 진짜 카메라 연결 → 로봇 자동화 →
공장 시스템 연동"의 네 단계로 성장합니다. 지금은 1단계가 사실상 완성된 상태이며,
남은 일은 코드가 아니라 **실제 고객 보드 사진으로 성능을 입증하는 것**입니다.

| 단계 | 하는 일 | 현재 상태 |
|---|---|---|
| **1 — 사진 기반 학습·검증** (8주) | 보드 사진(PNG/JPG)을 올려 정상을 학습하고, 불량 의심 부위를 표시하고, 결과를 CSV/PNG/PDF 증빙으로 내보내 고객이 직접 확인 | **코드 완성.** 실제 고객 사진으로 돌려 보고 그 증빙을 검토받는 절차만 남음 |
| **2 — 실제 카메라 연결** (8주) | 산업용 카메라(GigE/USB3)와 조명을 붙여 실시간으로 촬영·검사, 윗면/옆면/아랫면 보기, 현장 검증 | 연결 통로(어댑터 틀, 폴더 카메라 시뮬레이션)는 준비됨. 실물 장비는 아직 |
| **3 — 로봇 자동화** (12–16주, 2027) | 보드 투입→검사→배출을 로봇이 자동 처리, 카메라와 동작 동기화, 안전장치(비상정지) | 화면상 시뮬레이션만 제공 |
| **4 — 공장 시스템(MES/ERP) 연동** (12–16주, 2027) | 생산 이력 추적, 검사 결과 자동 전송, 공장 계정 연동(목표 표준: IPC-CFX) | 모의(Mock) 연동만 제공 |

**사업 일정.** 2026년: 1~2단계 시제품(PoC) 완성과 고객 검증. 2027년 초: 첫 정식 출시
(고객 1개사, 5–10 라이선스 목표). 이후 한국 시장을 먼저 다진 뒤 동남아·일본·유럽으로
확장하는 계획입니다.

**1단계를 "끝났다"고 말하기 위한 조건** (카메라 없이 지금 할 수 있는 일):
실제 고객 보드 사진을 학습·검증 워크플로에 통과시키고, 자동 생성된 증빙 패키지를
검토·승인받고, 사용한 모델의 승인 기록(또는 시제품 엔진임을 명시한 범위 한정)과
빌드/테스트 증빙을 보존하는 것입니다.

**1단계 이후 기술 우선순위** (자세한 근거: `Docs/AOI_Industry_Viability_Assessment.md`):
① 제대로 학습된 AI 이상 검출 모델을 ONNX 통로로 연결 → ② 부품 설계 데이터(CAD/BOM)로
검사 프로그램 자동 생성 → ③ 측정 신뢰성 검증(MSA, 반복·재현성 연구) → ④ 공장 표준
통신(IPC-CFX/Hermes) 연동 → ⑤ 높이·부피까지 재는 3D 검사 도입.
