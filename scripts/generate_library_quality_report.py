import sqlite3
from pathlib import Path
from datetime import datetime
import pandas as pd

DB_PATH = Path("data/database/aoi_pcb_library.sqlite")
REPORT_PATH = Path("data/exports/reports/library_quality_report.md")

REPORT_PATH.parent.mkdir(parents=True, exist_ok=True)

conn = sqlite3.connect(DB_PATH)

images_df = pd.read_sql_query("SELECT * FROM images", conn)
labels_df = pd.read_sql_query("SELECT * FROM defect_labels", conn)
annotations_df = pd.read_sql_query("SELECT * FROM annotations", conn)

try:
    taxonomy_df = pd.read_sql_query("SELECT * FROM defect_taxonomy", conn)
except Exception:
    taxonomy_df = pd.DataFrame()

try:
    splits_df = pd.read_sql_query("SELECT * FROM dataset_splits", conn)
except Exception:
    splits_df = pd.DataFrame()

conn.close()

total_images = len(images_df)
total_labels = len(labels_df)
total_annotations = len(annotations_df)

broken_paths = 0
for _, row in images_df.iterrows():
    if not Path(row["file_path"]).exists():
        broken_paths += 1

duplicate_hashes = images_df["file_hash"].duplicated().sum() if "file_hash" in images_df.columns else "N/A"

missing_dimensions = images_df[
    images_df["image_width"].isna() | images_df["image_height"].isna()
].shape[0]

if len(labels_df) > 0:
    labeled_image_ids = set(labels_df["image_id"])
    unlabeled_images = len([
        image_id for image_id in images_df["image_id"]
        if image_id not in labeled_image_ids
    ])
else:
    unlabeled_images = total_images

label_counts = labels_df["defect_type"].value_counts() if len(labels_df) > 0 else pd.Series(dtype=int)
dataset_counts = images_df["dataset_name"].value_counts(dropna=False) if "dataset_name" in images_df.columns else pd.Series(dtype=int)
split_counts = splits_df["split_name"].value_counts() if len(splits_df) > 0 else pd.Series(dtype=int)

report_lines = []

report_lines.append("# AOI PCB Defect Library Quality Report")
report_lines.append("")
report_lines.append(f"Generated at: {datetime.now().isoformat(timespec='seconds')}")
report_lines.append("")

report_lines.append("## 1. Database Summary")
report_lines.append("")
report_lines.append(f"- Total images: {total_images}")
report_lines.append(f"- Total labels: {total_labels}")
report_lines.append(f"- Total annotations: {total_annotations}")
report_lines.append(f"- Total taxonomy entries: {len(taxonomy_df)}")
report_lines.append(f"- Total dataset split records: {len(splits_df)}")
report_lines.append("")

report_lines.append("## 2. Validation Summary")
report_lines.append("")
report_lines.append(f"- Broken image paths: {broken_paths}")
report_lines.append(f"- Duplicate image hashes: {duplicate_hashes}")
report_lines.append(f"- Missing image dimensions: {missing_dimensions}")
report_lines.append(f"- Images without labels: {unlabeled_images}")
report_lines.append("")

report_lines.append("## 3. Images by Dataset")
report_lines.append("")
if len(dataset_counts) > 0:
    for dataset, count in dataset_counts.items():
        report_lines.append(f"- {dataset}: {count}")
else:
    report_lines.append("- No dataset information available.")
report_lines.append("")

report_lines.append("## 4. Labels by Defect Type")
report_lines.append("")
if len(label_counts) > 0:
    for label, count in label_counts.items():
        report_lines.append(f"- {label}: {count}")
else:
    report_lines.append("- No labels available.")
report_lines.append("")

report_lines.append("## 5. Dataset Split Summary")
report_lines.append("")
if len(split_counts) > 0:
    for split, count in split_counts.items():
        report_lines.append(f"- {split}: {count}")
else:
    report_lines.append("- Dataset split has not been created yet.")
report_lines.append("")

report_lines.append("## 6. Notes")
report_lines.append("")
report_lines.append("- Imported annotations remain pending until manual review.")
report_lines.append("- DeepPCB is useful for database and annotation testing, but additional PCBA-specific data is still required.")
report_lines.append("- Future work should include PCBA defects such as solder bridge, missing component, tombstone, polarity error, and misalignment.")

REPORT_PATH.write_text("\n".join(report_lines), encoding="utf-8")

print(f"Library quality report generated: {REPORT_PATH}")