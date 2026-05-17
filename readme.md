# AOI PCB Failure Detection Database

This repository is intended to support the development of an AOI-based PCB/PCBA failure detection system by providing a structured foundation for defect image library management, dataset preparation, annotation storage, and future GUI or AI integration.

## Purpose

The goal of this repository is to organize PCB/PCBA inspection image data in a way that is reliable, scalable, and suitable for future machine learning or deep learning work.

The repository is designed to help manage:

- PCB/PCBA image records
- defect labels
- bounding-box annotations
- defect taxonomy
- metadata
- review status
- dataset validation
- train / validation / test preparation
- CSV/report export
- future GUI access

The database-first approach is used so that the image library can become a stable foundation before GUI development or model training begins.

## Intended Use

This repository is intended to become a working defect-library backend for an AOI software proof of concept.

It should support the following future workflows:

1. Import PCB/PCBA sample images into a structured local library.
2. Store image metadata such as file path, source, board information, inspection stage, and annotation status.
3. Record defect labels using a controlled taxonomy.
4. Store bounding-box annotations for object detection tasks.
5. Track review and approval status for dataset quality control.
6. Export database records for reporting, review, and future model preparation.
7. Prepare train, validation, and test splits for AI model development.
8. Provide a data backend that a future GUI can access.

## Project Direction

The repository is designed around the idea that a reliable AOI software system needs a strong dataset foundation first.

Before building the GUI or training models, the project should ensure that the defect library can answer basic questions such as:

- What image is this?
- Where did it come from?
- What defect does it show?
- Has the label been reviewed?
- Is there an annotation?
- What defect taxonomy does the label belong to?
- Can this image be used for training, validation, or testing?
- Can the records be exported for review?

## Planned Repository Structure

```text
AOI_PCB_Database/
  data/
    images/
      raw/
      reviewed/
      rejected/
      roi_crops/
    annotations/
      yolo/
      coco/
    database/
    exports/
      csv/
      reports/
      annotated_previews/
  documentation/
  scripts/
  sample_import/
  external_datasets/
  README.md
  requirements.txt