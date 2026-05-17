import sqlite3
from pathlib import Path
import pandas as pd

DB_PATH = Path("data/database/aoi_pcb_library.sqlite")

conn = sqlite3.connect(DB_PATH)

images_df = pd.read_sql_query("SELECT * FROM images", conn)
labels_df = pd.read_sql_query("SELECT * FROM defect_labels", conn)
annotations_df = pd.read_sql_query("SELECT * FROM annotations", conn)

print("\n=== DATABASE SUMMARY ===")
print(f"Total images: {len(images_df)}")
print(f"Total labels: {len(labels_df)}")
print(f"Total annotations: {len(annotations_df)}")

print("\n=== Images by Dataset ===")
if "dataset_name" in images_df.columns:
    print(images_df["dataset_name"].value_counts(dropna=False))
else:
    print("dataset_name column not found")

print("\n=== Images by Original Filename Type ===")
print("Template images:", images_df["original_filename"].str.endswith("_temp.jpg").sum())
print("Test images:", images_df["original_filename"].str.endswith("_test.jpg").sum())

print("\n=== Labels by Defect Type ===")
if len(labels_df) > 0:
    print(labels_df["defect_type"].value_counts(dropna=False))
else:
    print("No labels found.")

print("\n=== Annotation Status ===")
if len(annotations_df) > 0:
    print(annotations_df["annotation_status"].value_counts(dropna=False))
else:
    print("No annotations found.")

conn.close()