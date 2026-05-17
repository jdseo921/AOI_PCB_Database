import sqlite3
import re
from pathlib import Path

DB_PATH = Path("data/database/aoi_pcb_library.sqlite")
DEEPPBC_ROOT = Path("external_datasets/DeepPCB")

CLASS_MAP = {
    1: "Open Circuit",
    2: "Short Circuit",
    3: "Mouse Bite",
    4: "Spur",
    5: "Spurious Copper",
    6: "Pin Hole"
}

CATEGORY_MAP = {
    "Open Circuit": "Electrical / Circuit Defect",
    "Short Circuit": "Electrical / Circuit Defect",
    "Mouse Bite": "PCB / Pad / Surface Defect",
    "Spur": "PCB / Pad / Surface Defect",
    "Spurious Copper": "PCB / Pad / Surface Defect",
    "Pin Hole": "PCB / Pad / Surface Defect"
}


def get_dataset_original_id(filename: str) -> str:
    filename = filename.lower()
    filename = filename.replace("_test.jpg", "")
    filename = filename.replace("_test.jpeg", "")
    filename = filename.replace("_temp.jpg", "")
    filename = filename.replace("_temp.jpeg", "")
    return filename


def find_annotation_file(dataset_original_id: str):
    matches = list(DEEPPBC_ROOT.rglob(f"{dataset_original_id}.txt"))

    if matches:
        return matches[0]

    return None


def annotation_already_imported(cursor, image_id):
    cursor.execute(
        "SELECT COUNT(*) FROM annotations WHERE image_id = ?",
        (image_id,)
    )
    return cursor.fetchone()[0] > 0


def parse_annotation_line(line: str):
    parts = re.split(r"[,\s]+", line.strip())

    if len(parts) != 5:
        return None

    return list(map(int, parts))


def main():
    print("Current folder:", Path.cwd())
    print("Database exists:", DB_PATH.exists())
    print("DeepPCB folder exists:", DEEPPBC_ROOT.exists())

    if not DB_PATH.exists():
        raise FileNotFoundError(f"Database not found: {DB_PATH}")

    if not DEEPPBC_ROOT.exists():
        raise FileNotFoundError(f"DeepPCB folder not found: {DEEPPBC_ROOT}")

    txt_count = len(list(DEEPPBC_ROOT.rglob("*.txt")))
    print(f"DeepPCB annotation .txt files found: {txt_count}")

    conn = sqlite3.connect(DB_PATH)
    cursor = conn.cursor()

    cursor.execute("""
    SELECT image_id, original_filename, dataset_original_id
    FROM images
    WHERE dataset_name = 'DeepPCB'
      AND (
            LOWER(original_filename) LIKE '%_test.jpg'
         OR LOWER(original_filename) LIKE '%_test.jpeg'
      )
    """)

    test_images = cursor.fetchall()

    print(f"DeepPCB test image rows found in database: {len(test_images)}")

    total_labels = 0
    total_annotations = 0
    skipped_existing = 0
    missing_annotation_files = []
    invalid_lines = []

    for image_id, original_filename, dataset_original_id in test_images:
        if annotation_already_imported(cursor, image_id):
            skipped_existing += 1
            continue

        if not dataset_original_id:
            dataset_original_id = get_dataset_original_id(original_filename)

        annotation_file = find_annotation_file(dataset_original_id)

        print(f"\nImage: {original_filename}")
        print(f"Expected annotation ID: {dataset_original_id}")
        print(f"Annotation file found: {annotation_file}")

        if annotation_file is None:
            missing_annotation_files.append(original_filename)
            continue

        with open(annotation_file, "r", encoding="utf-8") as file:
            lines = [line.strip() for line in file.readlines() if line.strip()]

        for line in lines:
            parsed = parse_annotation_line(line)

            if parsed is None:
                invalid_lines.append((str(annotation_file), line))
                continue

            x_min, y_min, x_max, y_max, class_id = parsed

            defect_type = CLASS_MAP.get(class_id, "Unknown / Needs Review")
            defect_category = CATEGORY_MAP.get(defect_type, "Unknown")

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
                defect_category,
                defect_type,
                "Unknown",
                "AOI / Visual",
                "unknown",
                "bbox",
                "pending",
                "auto_import",
                f"Imported from DeepPCB annotation file: {annotation_file.name}"
            ))

            label_id = cursor.lastrowid
            total_labels += 1

            cursor.execute("""
            INSERT INTO annotations (
                image_id,
                label_id,
                x_min,
                y_min,
                x_max,
                y_max,
                annotation_format,
                annotation_status
            )
            VALUES (?, ?, ?, ?, ?, ?, ?, ?)
            """, (
                image_id,
                label_id,
                x_min,
                y_min,
                x_max,
                y_max,
                "bbox",
                "pending"
            ))

            total_annotations += 1

    conn.commit()
    conn.close()

    print("\n=== IMPORT RESULT ===")
    print(f"Skipped images that already had annotations: {skipped_existing}")
    print(f"Imported labels: {total_labels}")
    print(f"Imported annotations: {total_annotations}")

    if missing_annotation_files:
        print("\nMissing annotation files for:")
        for name in missing_annotation_files:
            print(f" - {name}")

    if invalid_lines:
        print("\nInvalid annotation lines:")
        for file_name, line in invalid_lines[:10]:
            print(f" - {file_name}: {line}")


if __name__ == "__main__":
    main()