import sqlite3
import hashlib
import shutil
from pathlib import Path
from datetime import datetime
from PIL import Image

DB_PATH = Path("data/database/aoi_pcb_library.sqlite")
SOURCE_FOLDER = Path("sample_import")
DEST_FOLDER = Path("data/images/raw")

VALID_EXTENSIONS = {".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff"}

DEST_FOLDER.mkdir(parents=True, exist_ok=True)
SOURCE_FOLDER.mkdir(parents=True, exist_ok=True)


def calculate_file_hash(file_path):
    hash_md5 = hashlib.md5()

    with open(file_path, "rb") as file:
        for chunk in iter(lambda: file.read(4096), b""):
            hash_md5.update(chunk)

    return hash_md5.hexdigest()


def main():
    conn = sqlite3.connect(DB_PATH)
    cursor = conn.cursor()

    image_files = [
        file for file in SOURCE_FOLDER.iterdir()
        if file.suffix.lower() in VALID_EXTENSIONS
    ]

    if not image_files:
        print("No images found in sample_import folder.")
        print("Put a few PCB images into sample_import and run this script again.")
        return

    for image_file in image_files:
        file_hash = calculate_file_hash(image_file)

        cursor.execute(
            "SELECT image_id FROM images WHERE file_hash = ?",
            (file_hash,)
        )

        existing = cursor.fetchone()

        if existing:
            print(f"Duplicate skipped: {image_file.name}")
            continue

        timestamp = datetime.now().strftime("%Y%m%d_%H%M%S_%f")
        image_id = f"PCB_{timestamp}"
        new_filename = f"{image_id}{image_file.suffix.lower()}"
        destination_path = DEST_FOLDER / new_filename

        shutil.copy2(image_file, destination_path)

        with Image.open(destination_path) as img:
            width, height = img.size

        cursor.execute("""
            INSERT INTO images (
                image_id,
                original_filename,
                file_path,
                file_hash,
                image_width,
                image_height,
                image_source,
                board_id,
                inspection_side,
                capture_method,
                upload_date,
                status
            )
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
        """, (
            image_id,
            image_file.name,
            str(destination_path),
            file_hash,
            width,
            height,
            "manual_sample",
            "unknown_board",
            "unknown",
            "manual_upload",
            datetime.now().isoformat(timespec="seconds"),
            "pending"
        ))

        print(f"Imported: {image_file.name} -> {new_filename}")

    conn.commit()
    conn.close()

    print("Image import completed.")


if __name__ == "__main__":
    main()