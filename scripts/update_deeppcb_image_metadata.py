import sqlite3
from pathlib import Path

DB_PATH = Path("data/database/aoi_pcb_library.sqlite")

conn = sqlite3.connect(DB_PATH)
cursor = conn.cursor()

cursor.execute("""
SELECT image_id, original_filename
FROM images
WHERE original_filename LIKE '%_test.jpg'
   OR original_filename LIKE '%_temp.jpg'
""")

rows = cursor.fetchall()

for image_id, original_filename in rows:
    if original_filename.endswith("_test.jpg"):
        annotation_available = "yes"
        status = "pending"
    elif original_filename.endswith("_temp.jpg"):
        annotation_available = "no"
        status = "pending"
    else:
        annotation_available = "unknown"
        status = "pending"

    dataset_original_id = (
        original_filename
        .replace("_test.jpg", "")
        .replace("_temp.jpg", "")
    )

    cursor.execute("""
    UPDATE images
    SET
        image_source = ?,
        dataset_name = ?,
        dataset_original_id = ?,
        dataset_split = ?,
        inspection_stage = ?,
        annotation_available = ?,
        license_note = ?,
        status = ?
    WHERE image_id = ?
    """, (
        "public_dataset",
        "DeepPCB",
        dataset_original_id,
        "unknown",
        "bare_pcb_surface",
        annotation_available,
        "DeepPCB public GitHub dataset; verify use conditions before commercial use",
        status,
        image_id
    ))

conn.commit()
conn.close()

print(f"Updated DeepPCB metadata for {len(rows)} image records.")