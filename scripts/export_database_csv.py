import sqlite3
from pathlib import Path
import pandas as pd

DB_PATH = Path("data/database/aoi_pcb_library.sqlite")
EXPORT_DIR = Path("data/exports/csv")

EXPORT_DIR.mkdir(parents=True, exist_ok=True)

if not DB_PATH.exists():
    raise FileNotFoundError(f"Database not found at: {DB_PATH}")

conn = sqlite3.connect(DB_PATH)

tables = [
    "images",
    "defect_labels",
    "annotations",
    "review_log",
    "defect_taxonomy",
    "dataset_splits"
]

for table in tables:
    df = pd.read_sql_query(f"SELECT * FROM {table}", conn)
    export_path = EXPORT_DIR / f"{table}.csv"
    df.to_csv(export_path, index=False, encoding="utf-8-sig")
    print(f"Exported {table}: {len(df)} rows -> {export_path}")

conn.close()

print("CSV export completed.")