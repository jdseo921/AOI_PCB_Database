import sqlite3
from pathlib import Path
import pandas as pd

DB_PATH = Path("data/database/aoi_pcb_library.sqlite")
DEEPPBC_ROOT = Path("external_datasets/DeepPCB")

print("Current working directory:", Path.cwd())
print("Database exists:", DB_PATH.exists())
print("DeepPCB folder exists:", DEEPPBC_ROOT.exists())

if not DB_PATH.exists():
    raise FileNotFoundError(f"Database not found at {DB_PATH}")

conn = sqlite3.connect(DB_PATH)

images_df = pd.read_sql_query("SELECT * FROM images", conn)
labels_df = pd.read_sql_query("SELECT * FROM defect_labels", conn)
annotations_df = pd.read_sql_query("SELECT * FROM annotations", conn)

print("\n=== IMAGE TABLE SUMMARY ===")
print("Total image rows:", len(images_df))

if len(images_df) > 0:
    print("\nFirst 10 original filenames:")
    print(images_df["original_filename"].head(10).to_string(index=False))

    print("\nFilename pattern counts:")
    print("_test.jpg:", images_df["original_filename"].str.lower().str.endswith("_test.jpg").sum())
    print("_temp.jpg:", images_df["original_filename"].str.lower().str.endswith("_temp.jpg").sum())

    if "dataset_name" in images_df.columns:
        print("\nDataset name counts:")
        print(images_df["dataset_name"].value_counts(dropna=False))
    else:
        print("\nWARNING: dataset_name column does not exist.")

    if "dataset_original_id" in images_df.columns:
        print("\nRows with dataset_original_id:")
        print(images_df["dataset_original_id"].notna().sum())
    else:
        print("\nWARNING: dataset_original_id column does not exist.")

print("\n=== LABEL / ANNOTATION SUMMARY ===")
print("Total labels:", len(labels_df))
print("Total annotations:", len(annotations_df))

print("\n=== DEEPPCB FILE CHECK ===")
test_images_found = list(DEEPPBC_ROOT.rglob("*_test.jpg"))
temp_images_found = list(DEEPPBC_ROOT.rglob("*_temp.jpg"))
txt_files_found = list(DEEPPBC_ROOT.rglob("*.txt"))

print("DeepPCB *_test.jpg files found:", len(test_images_found))
print("DeepPCB *_temp.jpg files found:", len(temp_images_found))
print("DeepPCB .txt files found:", len(txt_files_found))

conn.close()