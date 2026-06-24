# Customer Dataset Validation Kit

Use this kit when an engineer needs to run repeatable Stage 1 customer-data validation and management needs a package they can review without rerunning the software.

## Required Folder Structure

Keep customer data outside the repository. Use one dataset folder per customer/run:

```text
CustomerDataset/
  images/
    board_0001_top_ok.png
    board_0002_top_bridge.png
  golden/
    board_ref_top.png
    board_ref_bottom.png
  customer_validation_manifest.csv
  README.txt
```

`images/` contains the inspection samples. `golden/` contains approved reference images used by the Pixel Difference prototype engine. The manifest may reference files relative to the dataset folder, for example `images/board_0001_top_ok.png` and `golden/board_ref_top.png`.

## Manifest CSV Schema

Start from `SampleData/customer_validation_manifest_template.csv`.

Required columns:

```text
image, ground_truth, golden_image, defect_type, side, refdes, roi_id, roi_type, lot_id, board_model, notes
```

Column meanings:

| Column | Required | Expected Values |
|---|---:|---|
| image | Yes | Relative or absolute path to a PNG/JPG/JPEG sample image. |
| ground_truth | Yes | `OK` or `NG`. Unknown labels do not support acceptance metrics. |
| golden_image | Yes | Relative or absolute path to the approved golden/reference image. |
| defect_type | Yes for NG | Defect class such as `solder_bridge`, `missing_component`, `polarity`. Use `OK` for OK samples. |
| side | Yes | `top`, `bottom`, or `side`. |
| refdes | Strongly recommended | Reference designator such as `U10`, `R24`, `J1`. |
| roi_id | Strongly recommended | Stable ROI identifier from the recipe or labeling tool. |
| roi_type | Strongly recommended | ROI category such as `pad`, `component`, `lead`, `marking`. |
| lot_id | Yes | Customer lot or dataset batch identifier. |
| board_model | Yes | Board/model/program name. |
| notes | Optional | Labeling comments and customer limitations. |

## Dataset Balance Gates

The default acceptance gates require:

- Minimum total images: 50.
- Minimum known ground-truth images: 50.
- Minimum OK images: 20.
- Minimum NG images: 20.
- Maximum unknown-label rate: 5%.
- All-OK and all-NG datasets fail preflight because precision/recall/false-call metrics cannot be reviewed fairly.

## Defect-Class Coverage

The default gate requires at least 2 NG defect classes, with at least 5 images per defect class. Use normalized names consistently: `solder_bridge` and `Solder Bridge` are treated as the same class, but inconsistent naming makes review harder.

## Golden-Image Requirement

Every sample should have a golden reference image. Missing golden images are blocking failures in dataset preflight by default because the Stage 1 Pixel Difference prototype needs a verified reference to produce actionable comparison evidence.

If a model-only acceptance run intentionally does not use golden images, document that waiver in `notes` and run preflight with criteria that treat missing golden images as warnings. Do not describe that evidence as Pixel Difference golden-compare validation.

## Image Naming Conventions

Use stable, sortable, non-confidential names:

```text
{board_model}_{lot_id}_{serial}_{side}_{label_or_defect}.png
tbox_lot07_0001_top_ok.png
tbox_lot07_0042_top_solder_bridge.png
```

Avoid spaces, customer secrets, operator names, and timestamps that can identify private production activity. Prefer PNG for reference reproducibility; JPG/JPEG is accepted when supplied by the customer.

## Run Dataset Preflight

1. Open `AI / Models`.
2. Select the customer dataset `images/` folder.
3. Select `customer_validation_manifest.csv`.
4. Click `Run Dataset Preflight`.
5. Resolve all blocking failures before running acceptance.

Preflight checks folder structure, manifest columns, image existence, golden-image existence, duplicate image rows, OK/NG balance, and defect-class coverage.
It also checks duplicate image file hashes, side/view metadata, ROI/refdes completeness when any ROI metadata is supplied, and image names that are hard to audit.

The AI Model Test screen shows a preflight result card:

- `PASS`: no blocking failures or warnings.
- `CONDITIONAL`: no blocking failures, but warnings require management/customer review.
- `FAIL`: blocking failures must be fixed before acceptance evidence can be considered repeatable.

Use `Open Manifest Template` on the AI / Models screen to open `SampleData/customer_validation_manifest_template.csv`.

## Run AI Model Test

1. Confirm preflight is `PASS` or consciously accepted as `CONDITIONAL`.
2. Click `Run Batch Inspection`.
3. Review metrics, dataset quality, class breakdowns, and rows marked `FAIL` or `N/A`.
4. Export annotated evidence only after confirming the selected dataset and manifest are the intended customer version.

Stage 1 can use the Pixel Difference prototype engine. That does not claim production model accuracy.

## Run False-Call Reduction

1. After a batch run, choose the false-call mode.
2. Click `Analyze False Calls`.
3. Review precision, recall, false-call rate, possible escape rate, review load, and recommendation status.
4. Engineers may create a threshold profile draft or apply a recommended threshold when the recommendation is valid.

Threshold changes are Stage 1 labeled-data evidence only. They do not prove production readiness across new cameras, lighting, boards, or factories.

## Run Performance Benchmark

1. Open `Export & Trace`.
2. Open `Performance Benchmark`.
3. Select `Image folder`.
4. Choose the same `images/` folder used for Stage 1 validation.
5. Run the benchmark and review p50, p95, p99, max frame-to-overlay, images-per-minute, and over-one-second count.

The benchmark is required for a Stage 1 readiness PASS. It is timing evidence for the local image-folder workflow and does not validate live camera acquisition, lighting control, robot motion, PLC safety, MES writeback, or factory cycle time.

## Run Model Acceptance

1. In `Settings`, register and validate the ONNX model.
2. Set the validated model active.
3. Click `Run Model Acceptance`.
4. Select the customer validation dataset folder and formal manifest CSV.
5. Review PASS/CONDITIONAL/FAIL messages, dataset preflight summary, dataset quality, performance, and limitations.
6. Create a model release package only after acceptance evidence is suitable for the selected claim.
7. Promote a production candidate only from a PASS model acceptance run.

Model acceptance is scoped to the supplied dataset and criteria.

## Export Customer Package

From `AI / Models`, click `Export Stage 1 Validation Package` after a successful batch run. The package includes:

- `validation_manifest.json`
- `validation_summary.html`
- `validation_summary.pdf`
- `dataset_preflight_summary.json`
- `validation_results.csv`
- `validation_breakdown.csv`
- `benchmark_results.csv`
- `customer_validation_manifest.csv` when a manifest was selected
- `customer_validation_report.html`
- `customer_validation_report.pdf`
- `limitations.txt`
- annotated image samples when available
- package README and print instructions

The package is intended for management review and customer evidence. It keeps prototype/hardware limitations explicit.

Management review should check:

- `validation_summary.html` for the non-technical summary, p95 timing evidence, false calls, possible escapes, and limitations.
- `dataset_preflight_summary.json` for preflight status, blocking failures, warnings, duplicate hashes, and metadata coverage.
- `validation_manifest.json` for package ID, run ID, dataset preflight status, acceptance status, criteria, included files, and limitations.
- `customer_validation_report.html` or PDF for the human-readable preflight, dataset quality, false-call, and acceptance summaries.
- `validation_results.csv` and `validation_breakdown.csv` for row-level and class/side/ROI evidence.

## Export Stage 1 Readiness Report

After preflight, batch validation, false-call review, validation package export, and benchmark:

1. Open `Export & Trace`.
2. Open `Stage 1 Readiness`.
3. Click `Refresh`.
4. Confirm the overall status, missing evidence list, preflight summary, latest batch run, benchmark p95 and over-one-second count, latest package path, and next recommended action.
5. Click `Export Report`.

The readiness export writes:

- `stage1_readiness_report.html`
- `stage1_readiness_report.pdf`
- `stage1_readiness_report.json`

The report must identify what was tested, what data was used, row counts, false calls, possible escapes, p95 timing, over-one-second count, reports generated, missing evidence, limitations, and remaining Stage 2/3/4 work.

## Interpreting Status

`PASS` means the selected data, manifest, metrics, dataset quality, and configured gates passed for the Stage 1 claim. It does not imply full factory automation readiness.

`CONDITIONAL` means no blocking gate failed, but one or more warnings require review, waiver, or follow-up. Examples include minor missing optional metadata or documented limitations.

`FAIL` means at least one blocking requirement failed. Examples include missing image files, missing required manifest columns, all-OK/all-NG data, insufficient OK/NG balance, insufficient defect-class coverage, excessive unknown labels, or missing golden images under Pixel Difference criteria.

Factory readiness remains separate from Stage 1 customer validation. A Stage 1 package can support customer review, but it does not claim real camera, lighting, robot, PLC, production MES, ERP, or full factory readiness unless those later-stage acceptance paths are explicitly completed with real hardware evidence.
