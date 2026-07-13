"""Train a PatchCore anomaly model on OK board images and export it to ONNX.

Stage-1 pipeline proof for the AOI Monitor ONNX slot: learns from ok_learning/
images only (no defect labels), evaluates on ok_validation/ + ng_validation/,
and exports an ONNX whose output is a per-pixel anomaly heat map — the format
the app's AnomalyHeatmapOutputParser consumes.
"""

from pathlib import Path

import torch
from anomalib.data import Folder
from anomalib.engine import Engine
from anomalib.models import Patchcore

ROOT = Path(r"C:\AOI\ml")
DATASET = ROOT / "dataset"
EXPORT = ROOT / "export"

torch.manual_seed(42)


def main() -> None:
    datamodule = Folder(
        name="aoi_board",
        root=DATASET,
        normal_dir="ok_learning",
        abnormal_dir="ng_validation",
        normal_test_dir="ok_validation",
        train_batch_size=4,
        eval_batch_size=4,
        num_workers=0,
    )

    # resnet18 + small coreset keeps the exported memory bank compact for CPU inference.
    model = Patchcore(
        backbone="resnet18",
        layers=["layer2", "layer3"],
        coreset_sampling_ratio=0.05,
    )

    engine = Engine(max_epochs=1, accelerator="cpu", devices=1)
    engine.fit(model=model, datamodule=datamodule)

    test_results = engine.test(model=model, datamodule=datamodule)
    print("TEST_RESULTS:", test_results)

    export_path = engine.export(
        model=model,
        export_type="onnx",
        export_root=EXPORT,
    )
    print("ONNX_EXPORTED:", export_path)


if __name__ == "__main__":
    main()
