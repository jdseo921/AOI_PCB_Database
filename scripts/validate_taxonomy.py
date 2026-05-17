import sqlite3
from pathlib import Path
import pandas as pd

DB_PATH = Path("data/database/aoi_pcb_library.sqlite")
EXPORT_PATH = Path("data/exports/csv/taxonomy_validation_report.csv")

EXPORT_PATH.parent.mkdir(parents=True, exist_ok=True)

conn = sqlite3.connect(DB_PATH)

labels_df = pd.read_sql_query("SELECT * FROM defect_labels", conn)
taxonomy_df = pd.read_sql_query("SELECT * FROM defect_taxonomy", conn)

approved_labels = set(taxonomy_df["defect_type"])
used_labels = set(labels_df["defect_type"])

unapproved_labels = sorted(list(used_labels - approved_labels))
unused_taxonomy_labels = sorted(list(approved_labels - used_labels))

report_rows = []

report_rows.append({
    "check": "Total label records",
    "result": len(labels_df),
    "status": "INFO"
})

report_rows.append({
    "check": "Unique defect labels used",
    "result": len(used_labels),
    "status": "INFO"
})

report_rows.append({
    "check": "Unapproved labels found",
    "result": len(unapproved_labels),
    "status": "PASS" if len(unapproved_labels) == 0 else "FAIL"
})

report_rows.append({
    "check": "Approved taxonomy labels not yet used",
    "result": len(unused_taxonomy_labels),
    "status": "INFO"
})

report_df = pd.DataFrame(report_rows)
report_df.to_csv(EXPORT_PATH, index=False, encoding="utf-8-sig")

print("\n=== TAXONOMY VALIDATION ===")
print(report_df)

if unapproved_labels:
    print("\nUnapproved labels:")
    for label in unapproved_labels:
        print(f"- {label}")

if unused_taxonomy_labels:
    print("\nApproved labels not yet used:")
    for label in unused_taxonomy_labels:
        print(f"- {label}")

conn.close()

print(f"\nTaxonomy validation report exported to: {EXPORT_PATH}")