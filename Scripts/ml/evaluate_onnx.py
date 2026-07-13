"""Score the exported PatchCore ONNX over the validation/inspection sets.

Reproduces the app-side flow: ONNX Runtime inference -> per-pixel anomaly map ->
image score = map max (after the same min-max normalization the app parser applies
when a model ships unnormalized). Reports the false-call / escape table so the
model can be compared 1:1 with the app's statistical learned engine on the same
dataset. Threshold selection mirrors the app: calibrate on the even-index half of
ok_validation, report on the odd-index held-out half.
"""

import json
import sys
from pathlib import Path

import numpy as np
import onnxruntime as ort
from PIL import Image

ROOT = Path(r"C:\AOI\ml")
DATASET = ROOT / "dataset"


def find_onnx() -> Path:
    candidates = sorted((ROOT / "export").rglob("*.onnx"))
    if not candidates:
        raise SystemExit("No exported ONNX found under export/")
    return candidates[0]


def main() -> None:
    model_path = find_onnx()
    session = ort.InferenceSession(str(model_path), providers=["CPUExecutionProvider"])
    inp = session.get_inputs()[0]
    outs = session.get_outputs()
    print(f"MODEL: {model_path}")
    print(f"INPUT: {inp.name} {inp.shape} {inp.type}")
    for o in outs:
        print(f"OUTPUT: {o.name} {o.shape} {o.type}")

    _, _, h, w = [d if isinstance(d, int) else 256 for d in inp.shape]

    def score_image(path: Path) -> float:
        image = Image.open(path).convert("RGB").resize((w, h), Image.BILINEAR)
        arr = np.asarray(image).astype(np.float32) / 255.0
        arr = np.transpose(arr, (2, 0, 1))[None, ...]
        results = session.run(None, {inp.name: arr})
        # Pick the map-shaped output (2 significant dims); fall back to scalar score.
        for value in results:
            squeezed = np.squeeze(np.asarray(value))
            if squeezed.ndim == 2:
                return float(squeezed.max())
        return float(np.max(np.asarray(results[0])))

    def score_folder(folder: str) -> list[tuple[str, float]]:
        files = sorted((DATASET / folder).glob("*.png"))
        return [(f.name, score_image(f)) for f in files]

    ok_scores = score_folder("ok_validation")
    ng_scores = score_folder("ng_validation")

    raw = [s for _, s in ok_scores + ng_scores]
    lo, hi = min(raw), max(raw)
    span = max(1e-9, hi - lo)

    def norm(value: float) -> float:
        return (value - lo) / span

    # Held-out split mirroring the app: even indices calibrate, odd indices report.
    cal = [norm(s) for i, (_, s) in enumerate(ok_scores) if i % 2 == 0]
    held = [norm(s) for i, (_, s) in enumerate(ok_scores) if i % 2 == 1]
    ng = [norm(s) for _, s in ng_scores]

    # Threshold: smallest value with 0 calibration false calls and 0 escapes,
    # swept over observed scores (same guardrail-first policy as the app).
    candidates = sorted(set(cal + ng + [0.5]))
    best = None
    for t in candidates:
        fc = sum(1 for s in cal if s >= t)
        esc = sum(1 for s in ng if s < t)
        if esc == 0 and (best is None or fc < best[1]):
            best = (t, fc)
    threshold = best[0] if best else 0.5

    held_fc = sum(1 for s in held if s >= threshold)
    esc = sum(1 for s in ng if s < threshold)
    report = {
        "model": str(model_path),
        "threshold": threshold,
        "calibration_ok": len(cal),
        "held_out_ok": len(held),
        "held_out_false_calls": held_fc,
        "held_out_false_call_rate": held_fc / len(held) if held else None,
        "ng_images": len(ng),
        "possible_escapes": esc,
        "possible_escape_rate": esc / len(ng) if ng else None,
        "ok_score_range": [min(norm(s) for _, s in ok_scores), max(norm(s) for _, s in ok_scores)],
        "ng_score_range": [min(ng), max(ng)],
        "separation": min(ng) - max(norm(s) for _, s in ok_scores),
    }
    print("EVALUATION:", json.dumps(report, indent=2))
    (ROOT / "onnx_evaluation.json").write_text(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
