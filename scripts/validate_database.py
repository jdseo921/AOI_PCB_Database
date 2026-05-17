import sqlite3
from pathlib import Path
import pandas as pd

DB_PATH = Path("data/database/aoi_pcb_library.sqlite")
EXPORT_PATH = Path("data/exports/csv/database_validation_report.csv")

EXPORT_PATH.parent.mkdir(parents=True, exist_ok=True)

conn = sqlite3.connect(DB_PATH)

images_df = pd.read_sql_query("SELECT * FROM images", conn)
labels_df = pd.read_sql_query("SELECT * FROM defect_labels", conn)

validation_results = []

# Check total images
validation_results.append({
    "check": "Total image records",
    "result": len(images_df),
    "status": "INFO"
})

# Check broken file paths
broken_paths = []

for _, row in images_df.iterrows():
    if not Path(row["file_path"]).exists():
        broken_paths.append(row["image_id"])

validation_results.append({
    "check": "Broken image file paths",
    "result": len(broken_paths),
    "status": "PASS" if len(broken_paths) == 0 else "FAIL"
})

# Check duplicate hashes
duplicate_hashes = images_df["file_hash"].duplicated().sum()

validation_results.append({
    "check": "Duplicate file hashes",
    "result": int(duplicate_hashes),
    "status": "PASS" if duplicate_hashes == 0 else "FAIL"
})

# Check missing image size
missing_size = images_df[
    images_df["image_width"].isna() | images_df["image_height"].isna()
]

validation_results.append({
    "check": "Missing image dimensions",
    "result": len(missing_size),
    "status": "PASS" if len(missing_size) == 0 else "FAIL"
})

# Check images without labels
if len(labels_df) == 0:
    unlabeled_count = len(images_df)
else:
    labeled_image_ids = set(labels_df["image_id"])
    unlabeled_count = len([
        image_id for image_id in images_df["image_id"]
        if image_id not in labeled_image_ids
    ])

validation_results.append({
    "check": "Images without defect labels",
    "result": unlabeled_count,
    "status": "WARNING" if unlabeled_count > 0 else "PASS"
})

report_df = pd.DataFrame(validation_results)
report_df.to_csv(EXPORT_PATH, index=False)

conn.close()

print(report_df)
print(f"Validation report exported to: {EXPORT_PATH}")