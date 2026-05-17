# AOI PCB Defect Database Schema

## Purpose

The database stores PCB/PCBA image records, defect labels, bounding-box annotations, taxonomy information, and review history for future AI training and GUI integration.

## Tables

### images

Stores one row per imported image.

Key fields:
- image_id
- original_filename
- file_path
- file_hash
- image_width
- image_height
- image_source
- dataset_name
- dataset_original_id
- inspection_stage
- annotation_available
- status

### defect_labels

Stores defect labels connected to images.

Key fields:
- label_id
- image_id
- defect_category
- defect_type
- severity
- detection_method
- component_id
- roi_id
- label_status
- reviewer
- notes

### annotations

Stores bounding-box annotation coordinates.

Key fields:
- annotation_id
- image_id
- label_id
- x_min
- y_min
- x_max
- y_max
- annotation_format
- annotation_status

### defect_taxonomy

Stores the approved list of defect labels.

Key fields:
- defect_type
- defect_category
- default_severity
- detection_method
- inspection_stage
- source
- active_status
- notes

### review_log

Stores review history.

Key fields:
- review_id
- image_id
- action
- reviewer
- review_date
- comment