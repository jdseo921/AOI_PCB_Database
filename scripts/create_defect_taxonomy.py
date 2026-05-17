import sqlite3
from pathlib import Path

DB_PATH = Path("data/database/aoi_pcb_library.sqlite")

taxonomy_rows = [
    {
        "defect_type": "Good / Normal",
        "defect_category": "Good / Normal",
        "default_severity": "None",
        "detection_method": "AOI / Visual",
        "inspection_stage": "Bare PCB / PCBA",
        "source": "Project taxonomy",
        "active_status": "active",
        "notes": "Acceptable sample with no visible defect."
    },
    {
        "defect_type": "Open Circuit",
        "defect_category": "Electrical / Circuit Defect",
        "default_severity": "Critical",
        "detection_method": "AOI / Electrical Test",
        "inspection_stage": "Bare PCB",
        "source": "DeepPCB / Project taxonomy",
        "active_status": "active",
        "notes": "Broken trace or disconnected copper path."
    },
    {
        "defect_type": "Short Circuit",
        "defect_category": "Electrical / Circuit Defect",
        "default_severity": "Critical",
        "detection_method": "AOI / Electrical Test",
        "inspection_stage": "Bare PCB",
        "source": "DeepPCB / Project taxonomy",
        "active_status": "active",
        "notes": "Unintended connection between conductive areas."
    },
    {
        "defect_type": "Mouse Bite",
        "defect_category": "PCB / Pad / Surface Defect",
        "default_severity": "Major",
        "detection_method": "AOI / Visual",
        "inspection_stage": "Bare PCB",
        "source": "DeepPCB / Project taxonomy",
        "active_status": "active",
        "notes": "Small edge damage or bite-like defect on copper trace."
    },
    {
        "defect_type": "Spur",
        "defect_category": "PCB / Pad / Surface Defect",
        "default_severity": "Major",
        "detection_method": "AOI / Visual",
        "inspection_stage": "Bare PCB",
        "source": "DeepPCB / Project taxonomy",
        "active_status": "active",
        "notes": "Unwanted copper protrusion."
    },
    {
        "defect_type": "Spurious Copper",
        "defect_category": "PCB / Pad / Surface Defect",
        "default_severity": "Major",
        "detection_method": "AOI / Visual",
        "inspection_stage": "Bare PCB",
        "source": "DeepPCB / Project taxonomy",
        "active_status": "active",
        "notes": "Extra unwanted copper residue."
    },
    {
        "defect_type": "Pin Hole",
        "defect_category": "PCB / Pad / Surface Defect",
        "default_severity": "Minor",
        "detection_method": "AOI / Visual",
        "inspection_stage": "Bare PCB",
        "source": "DeepPCB / Project taxonomy",
        "active_status": "active",
        "notes": "Small hole-like copper defect."
    },

    # PCBA-oriented labels prepared for future use
    {
        "defect_type": "Missing Component",
        "defect_category": "Component Placement Defect",
        "default_severity": "Critical",
        "detection_method": "AOI",
        "inspection_stage": "PCBA",
        "source": "Project taxonomy",
        "active_status": "active",
        "notes": "Expected component is absent."
    },
    {
        "defect_type": "Misalignment",
        "defect_category": "Component Placement Defect",
        "default_severity": "Major",
        "detection_method": "AOI",
        "inspection_stage": "PCBA",
        "source": "Project taxonomy",
        "active_status": "active",
        "notes": "Component is shifted from its expected pad location."
    },
    {
        "defect_type": "Tombstone",
        "defect_category": "Component Placement Defect",
        "default_severity": "Major",
        "detection_method": "AOI / Visual",
        "inspection_stage": "PCBA",
        "source": "Project taxonomy",
        "active_status": "active",
        "notes": "One side of a small component is lifted."
    },
    {
        "defect_type": "Polarity Error",
        "defect_category": "Component Placement Defect",
        "default_severity": "Critical",
        "detection_method": "AOI / Visual",
        "inspection_stage": "PCBA",
        "source": "Project taxonomy",
        "active_status": "active",
        "notes": "Polarized component is placed in the wrong orientation."
    },
    {
        "defect_type": "Solder Bridge",
        "defect_category": "Solder-Related Defect",
        "default_severity": "Critical",
        "detection_method": "AOI / Visual",
        "inspection_stage": "PCBA",
        "source": "Project taxonomy",
        "active_status": "active",
        "notes": "Adjacent pads or leads are unintentionally connected by solder."
    },
    {
        "defect_type": "Insufficient Solder",
        "defect_category": "Solder-Related Defect",
        "default_severity": "Major",
        "detection_method": "AOI / 3D AOI",
        "inspection_stage": "PCBA",
        "source": "Project taxonomy",
        "active_status": "active",
        "notes": "Solder amount is insufficient for a reliable joint."
    },
    {
        "defect_type": "Excess Solder",
        "defect_category": "Solder-Related Defect",
        "default_severity": "Major",
        "detection_method": "AOI / 3D AOI",
        "inspection_stage": "PCBA",
        "source": "Project taxonomy",
        "active_status": "active",
        "notes": "Too much solder is present, possibly increasing bridge risk."
    },
    {
        "defect_type": "Cold Joint",
        "defect_category": "Solder-Related Defect",
        "default_severity": "Major",
        "detection_method": "AOI / Visual",
        "inspection_stage": "PCBA",
        "source": "Project taxonomy",
        "active_status": "active",
        "notes": "Dull or poorly formed solder joint caused by insufficient heat or poor wetting."
    },
    {
        "defect_type": "Contamination",
        "defect_category": "PCB / Pad / Surface Defect",
        "default_severity": "Major",
        "detection_method": "AOI / Visual",
        "inspection_stage": "Bare PCB / PCBA",
        "source": "Project taxonomy",
        "active_status": "active",
        "notes": "Dust, residue, flux, oil, or other foreign material."
    },
    {
        "defect_type": "Unknown / Needs Review",
        "defect_category": "Unknown",
        "default_severity": "Unknown",
        "detection_method": "Review Required",
        "inspection_stage": "Bare PCB / PCBA",
        "source": "Project taxonomy",
        "active_status": "active",
        "notes": "Unclear sample requiring manual review."
    },
]

conn = sqlite3.connect(DB_PATH)
cursor = conn.cursor()

cursor.execute("""
CREATE TABLE IF NOT EXISTS defect_taxonomy (
    defect_type TEXT PRIMARY KEY,
    defect_category TEXT NOT NULL,
    default_severity TEXT,
    detection_method TEXT,
    inspection_stage TEXT,
    source TEXT,
    active_status TEXT DEFAULT 'active',
    notes TEXT
)
""")

for row in taxonomy_rows:
    cursor.execute("""
    INSERT OR REPLACE INTO defect_taxonomy (
        defect_type,
        defect_category,
        default_severity,
        detection_method,
        inspection_stage,
        source,
        active_status,
        notes
    )
    VALUES (?, ?, ?, ?, ?, ?, ?, ?)
    """, (
        row["defect_type"],
        row["defect_category"],
        row["default_severity"],
        row["detection_method"],
        row["inspection_stage"],
        row["source"],
        row["active_status"],
        row["notes"]
    ))

conn.commit()
conn.close()

print(f"Created/updated defect_taxonomy table with {len(taxonomy_rows)} defect types.")