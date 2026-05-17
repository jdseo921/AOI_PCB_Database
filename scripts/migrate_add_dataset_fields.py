import sqlite3
from pathlib import Path

DB_PATH = Path("data/database/aoi_pcb_library.sqlite")

columns_to_add = {
    "dataset_name": "TEXT",
    "dataset_original_id": "TEXT",
    "dataset_split": "TEXT",
    "inspection_stage": "TEXT",
    "annotation_available": "TEXT",
    "license_note": "TEXT"
}

conn = sqlite3.connect(DB_PATH)
cursor = conn.cursor()

cursor.execute("PRAGMA table_info(images)")
existing_columns = [row[1] for row in cursor.fetchall()]

for column_name, column_type in columns_to_add.items():
    if column_name not in existing_columns:
        cursor.execute(f"ALTER TABLE images ADD COLUMN {column_name} {column_type}")
        print(f"Added column: {column_name}")
    else:
        print(f"Column already exists: {column_name}")

conn.commit()
conn.close()

print("Dataset metadata migration completed.")