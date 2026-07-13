# Training a Real Anomaly Model for the ONNX Slot

The app's ONNX engine now accepts **anomaly heat-map models** (anomalib PatchCore/PaDiM/
FastFlow exports) in addition to detection-row models. This is the upgrade path from the
statistical prototype engines to a learned detector — trained from **OK images only**, same
as the Stage-1 workflow, no defect labels needed.

**한국어 요약:** 이제 ONNX 슬롯에 실제 학습형 이상 검출 모델(anomalib)을 연결할 수
있습니다. 양품 사진만으로 학습하며(결함 라벨 불필요), 합성 데이터 기준으로 통계 엔진보다
허위 검출이 크게 낮았습니다(보류 검증 0/15 대 1/15, 유출 0/20). 아래 절차는 학습 →
ONNX 내보내기 → 앱 설정까지의 전체 과정입니다.

## One-time environment (Windows, CPU-only)

```powershell
winget install astral-sh.uv
uv python install 3.11
mkdir C:\AOI\ml; cd C:\AOI\ml
uv venv --python 3.11 .venv
uv pip install --python .venv\Scripts\python.exe torch torchvision --index-url https://download.pytorch.org/whl/cpu
uv pip install --python .venv\Scripts\python.exe anomalib onnx onnxruntime pillow
```

## Train and export

Dataset layout is the same as `learn-from-images` (`ok_learning/`, `ok_validation/`,
`ng_validation/`). For a synthetic dry run: `pwsh SampleData/generate_image_learning_demo_project.ps1 -OutputRoot C:\AOI\ml\dataset`.

```powershell
cd C:\AOI\ml
.venv\Scripts\python.exe <repo>\Scripts\ml\train_patchcore.py    # trains + exports ONNX
.venv\Scripts\python.exe <repo>\Scripts\ml\evaluate_onnx.py      # held-out false-call/escape table
```

Output: `C:\AOI\ml\export\weights\onnx\model.onnx` (~17 MB with resnet18 + 5% coreset).
The evaluator mirrors the app's methodology: threshold calibrated on the even half of
ok_validation, rates reported on the untouched odd half.

## Wire it into the app

Settings → AI → ONNX model configuration:

| Field | Value |
|---|---|
| Model path | `C:\AOI\ml\export\weights\onnx\model.onnx` |
| Input tensor | `input` |
| Output tensor | `anomaly_map` |
| Input width / height | 256 / 256 |
| Confidence threshold | 0.5 (anomalib exports embed normalization; 0.5 = learned threshold) |

Run the Settings readiness test — it should report *"output shape […] (parsed as anomaly
heat map)"*. The engine converts map regions into defect detections automatically
(`AnomalyHeatmapOutputParser`). Formal adoption still goes through the model-acceptance
gate with a labeled validation set, same as any model.

## Benchmark (synthetic 640px demo dataset, identical data + methodology)

| Metric | Statistical learned engine | PatchCore ONNX |
|---|---|---|
| Held-out false calls | 1/15 (6.7%) | **0/15 (0%)** |
| Possible escapes | 0/20 | 0/20 |
| OK vs NG score margin | threshold-tuned | **0.40 normalized gap** |
| Test AUROC | — | 0.9999 |

Synthetic-data evidence proves the pipeline, not customer acceptance — rerun both on real
customer images before claiming production accuracy.
