import sqlite3
import random
from pathlib import Path
from datetime import datetime

DB_PATH = Path("data/database/aoi_pcb_library.sqlite")

TRAIN_RATIO = 0.70
VAL_RATIO = 0.15
TEST_RATIO = 0.15

random.seed(42)

conn = sqlite3.connect(DB_PATH)
cursor = conn.cursor()

cursor.execute("""
CREATE TABLE IF NOT EXISTS dataset_splits (
    split_id INTEGER PRIMARY KEY AUTOINCREMENT,
    image_id TEXT NOT NULL,
    dataset_original_id TEXT,
    split_name TEXT NOT NULL,
    split_method TEXT,
    created_at TEXT,
    notes TEXT,
    FOREIGN KEY (image_id) REFERENCES images(image_id)
)
""")

# Clear old split records before regenerating
cursor.execute("DELETE FROM dataset_splits")

cursor.execute("""
SELECT image_id, dataset_original_id
FROM images
WHERE dataset_name = 'DeepPCB'
ORDER BY dataset_original_id, image_id
""")

rows = cursor.fetchall()

if not rows:
    print("No DeepPCB image records found.")
    conn.close()
    exit()

# Group images by dataset_original_id to avoid splitting paired images
groups = {}

for image_id, dataset_original_id in rows:
    if dataset_original_id is None:
        dataset_original_id = image_id

    groups.setdefault(dataset_original_id, []).append(image_id)

group_ids = list(groups.keys())
random.shuffle(group_ids)

total_groups = len(group_ids)
train_end = int(total_groups * TRAIN_RATIO)
val_end = train_end + int(total_groups * VAL_RATIO)

train_groups = set(group_ids[:train_end])
val_groups = set(group_ids[train_end:val_end])
test_groups = set(group_ids[val_end:])

created_at = datetime.now().isoformat(timespec="seconds")

inserted = 0

for group_id, image_ids in groups.items():
    if group_id in train_groups:
        split_name = "train"
    elif group_id in val_groups:
        split_name = "validation"
    else:
        split_name = "test"

    for image_id in image_ids:
        cursor.execute("""
        INSERT INTO dataset_splits (
            image_id,
            dataset_original_id,
            split_name,
            split_method,
            created_at,
            notes
        )
        VALUES (?, ?, ?, ?, ?, ?)
        """, (
            image_id,
            group_id,
            split_name,
            "grouped_by_dataset_original_id",
            created_at,
            "DeepPCB template/test pairs kept in same split to reduce leakage."
        ))

        inserted += 1

conn.commit()

cursor.execute("""
SELECT split_name, COUNT(*)
FROM dataset_splits
GROUP BY split_name
ORDER BY split_name
""")

summary = cursor.fetchall()

conn.close()

print(f"Inserted {inserted} dataset split records.")

print("\n=== SPLIT SUMMARY ===")
for split_name, count in summary:
    print(f"{split_name}: {count}")