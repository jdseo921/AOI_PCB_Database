# Sample Data

Place small local demo images here when preparing a Stage 1 walkthrough.

Recommended layout:

```text
SampleData/
  samples/
    sample_001.png
    sample_002.jpg
  golden/
    golden_001.png
  batch/
    board_a_001.png
    board_a_002.png
  ground_truth/
    stage1_ground_truth.csv
```

Guidelines:

- Use small PNG/JPG/JPEG files that are appropriate for sharing.
- Do not commit large image datasets to GitHub.
- Do not commit customer-confidential, production, or personally identifiable data.
- Keep large or private datasets outside the repository and import them locally through the app.
- The app copies imported images into `%LOCALAPPDATA%\AOI_Monitor\image_vault\`.
- Batch test folders can be selected from any local path; they do not need to live inside this repository.

Suggested demo set:

- One sample image for `Image Library > Open Record`.
- One matching golden/reference image for `Compare Golden`.
- Three to ten small images for `AI Model Test > Run Batch Inspection`.
- Optional ground-truth CSV for batch validation.

This folder intentionally contains documentation only. Add images only when they are small, non-confidential, and appropriate for source control.
