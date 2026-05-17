import sqlite3
from pathlib import Path

DB_PATH = Path("data/database/aoi_pcb_library.sqlite")

DB_PATH.parent.mkdir(parents=True, exist_ok=True)

conn = sqlite3.connect(DB_PATH)
cursor = conn.cursor()

cursor.execute("""
CREATE TABLE IF NOT EXISTS images (
    image_id TEXT PRIMARY KEY,
    original_filename TEXT NOT NULL,
    file_path TEXT NOT NULL,
    file_hash TEXT UNIQUE,
    image_width INTEGER,
    image_height INTEGER,
    image_source TEXT,
    board_id TEXT,
    inspection_side TEXT,
    capture_method TEXT,
    upload_date TEXT,
    status TEXT DEFAULT 'pending'
)
""")

cursor.execute("""
CREATE TABLE IF NOT EXISTS defect_labels (
    label_id INTEGER PRIMARY KEY AUTOINCREMENT,
    image_id TEXT NOT NULL,
    defect_category TEXT,
    defect_type TEXT,
    severity TEXT,
    detection_method TEXT,
    component_id TEXT,
    roi_id TEXT,
    label_status TEXT DEFAULT 'pending',
    reviewer TEXT,
    notes TEXT,
    FOREIGN KEY (image_id) REFERENCES images(image_id)
)
""")

cursor.execute("""
CREATE TABLE IF NOT EXISTS annotations (
    annotation_id INTEGER PRIMARY KEY AUTOINCREMENT,
    image_id TEXT NOT NULL,
    label_id INTEGER,
    x_min INTEGER,
    y_min INTEGER,
    x_max INTEGER,
    y_max INTEGER,
    annotation_format TEXT DEFAULT 'bbox',
    annotation_status TEXT DEFAULT 'pending',
    FOREIGN KEY (image_id) REFERENCES images(image_id),
    FOREIGN KEY (label_id) REFERENCES defect_labels(label_id)
)
""")

cursor.execute("""
CREATE TABLE IF NOT EXISTS review_log (
    review_id INTEGER PRIMARY KEY AUTOINCREMENT,
    image_id TEXT NOT NULL,
    action TEXT,
    reviewer TEXT,
    review_date TEXT,
    comment TEXT,
    FOREIGN KEY (image_id) REFERENCES images(image_id)
)
""")

conn.commit()
conn.close()

print(f"Database created successfully at: {DB_PATH}")