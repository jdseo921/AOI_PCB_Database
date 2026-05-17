import sqlite3
from pathlib import Path

DB_PATH = Path("data/database/aoi_pcb_library.sqlite")

conn = sqlite3.connect(DB_PATH)
cursor = conn.cursor()

cursor.execute("""
SELECT image_id, original_filename
FROM images
WHERE dataset_name = 'DeepPCB'
  AND original_filename LIKE '%_temp.jpg'
""")

template_images = cursor.fetchall()
inserted = 0

for image_id, original_filename in template_images:
    cursor.execute("""
    SELECT COUNT(*)
    FROM defect_labels
    WHERE image_id = ?
      AND defect_type = 'Good / Normal'
    """, (image_id,))

    already_exists = cursor.fetchone()[0]

    if already_exists:
        continue

    cursor.execute("""
    INSERT INTO defect_labels (
        image_id,
        defect_category,
        defect_type,
        severity,
        detection_method,
        component_id,
        roi_id,
        label_status,
        reviewer,
        notes
    )
    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
    """, (
        image_id,
        "Good / Normal",
        "Good / Normal",
        "None",
        "Template comparison",
        "N/A",
        "full_image",
        "pending",
        "auto_import",
        f"Automatically labeled from DeepPCB template image filename: {original_filename}"
    ))

    inserted += 1

conn.commit()
conn.close()

print(f"Inserted {inserted} Good / Normal labels for DeepPCB template images.")