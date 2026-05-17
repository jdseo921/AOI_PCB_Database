import sqlite3
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

DB_PATH = Path("data/database/aoi_pcb_library.sqlite")
OUTPUT_DIR = Path("data/exports/annotated_previews")

OUTPUT_DIR.mkdir(parents=True, exist_ok=True)

conn = sqlite3.connect(DB_PATH)
cursor = conn.cursor()

cursor.execute("""
SELECT
    images.image_id,
    images.original_filename,
    images.file_path,
    defect_labels.defect_type,
    annotations.x_min,
    annotations.y_min,
    annotations.x_max,
    annotations.y_max
FROM annotations
JOIN images ON annotations.image_id = images.image_id
JOIN defect_labels ON annotations.label_id = defect_labels.label_id
ORDER BY images.image_id
""")

rows = cursor.fetchall()
conn.close()

if not rows:
    print("No annotations found. Nothing to preview.")
    exit()

# Group annotations by image
image_annotations = {}

for row in rows:
    image_id, original_filename, file_path, defect_type, x_min, y_min, x_max, y_max = row

    if image_id not in image_annotations:
        image_annotations[image_id] = {
            "original_filename": original_filename,
            "file_path": file_path,
            "annotations": []
        }

    image_annotations[image_id]["annotations"].append({
        "defect_type": defect_type,
        "bbox": (x_min, y_min, x_max, y_max)
    })

preview_count = 0

for image_id, info in image_annotations.items():
    image_path = Path(info["file_path"])

    if not image_path.exists():
        print(f"Missing image file: {image_path}")
        continue

    image = Image.open(image_path).convert("RGB")
    draw = ImageDraw.Draw(image)

    for ann in info["annotations"]:
        defect_type = ann["defect_type"]
        x_min, y_min, x_max, y_max = ann["bbox"]

        # Draw bounding box
        draw.rectangle(
            [(x_min, y_min), (x_max, y_max)],
            outline="red",
            width=3
        )

        # Draw label background and text
        text = defect_type
        text_position = (x_min, max(0, y_min - 15))

        draw.rectangle(
            [text_position, (text_position[0] + len(text) * 7, text_position[1] + 14)],
            fill="red"
        )

        draw.text(
            text_position,
            text,
            fill="white"
        )

    output_filename = f"{image_id}_preview.jpg"
    output_path = OUTPUT_DIR / output_filename
    image.save(output_path)

    preview_count += 1
    print(f"Saved preview: {output_path}")

print(f"\nGenerated {preview_count} annotated preview images.")