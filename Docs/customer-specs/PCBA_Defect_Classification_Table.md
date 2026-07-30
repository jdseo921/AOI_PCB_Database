# PCBA Defect Classification Table

### PDF-Style Technical Document

Version 1.0
Prepared for: AOI / Visual Inspection / AI Vision Development
Date: 27 April 2026

## 1. Overview

This document provides a standardized classification of PCBA defects for use in AOI software development, visual inspection guidelines, and AI vision model training.

## 2. Defect Categories

PCBA defects are grouped into six major categories:

- Solder-related defects
- Component placement defects
- Solder paste printing defects
- PCB / pad / surface defects
- Electrical / circuit defects
- Connector / mechanical defects

## 3. Defect Classification Table

### 3.1 Solder-Related Defects

| Defect Name | Description | Severity | Detection Method |
|---|---|---|---|
| Solder Bridge | Adjacent pads/leads shorted by solder | Critical | AOI / Visual |
| Insufficient Solder | Not enough solder to form a proper joint | Major | AOI / 3D |
| Excess Solder | Too much solder, risk of bridging | Major | AOI |
| Cold Joint | Dull, grainy joint due to insufficient heat | Major | Visual |
| Poor Wetting | Solder does not spread on pad/lead | Major | AOI |
| Solder Crack | Cracks in solder joint | Major | Visual |
| Solder Ball | Small solder spheres around joint | Minor | AOI |
| Fillet Shape Defect | Incorrect solder fillet geometry | Minor | AOI |

### 3.2 Component-Related Defects

| Defect Name | Description | Severity | Detection Method |
|---|---|---|---|
| Missing Component | Component not placed | Critical | AOI |
| Misalignment | Component shifted from pad center | Major | AOI |
| Tombstone | One side lifted due to uneven wetting | Major | AOI |
| Polarity Error | Incorrect orientation of polarized parts | Critical | AOI / Visual |
| Rotation Error | Component rotated 90°/180° | Major | AOI |
| Bent Lead | IC lead bent or not contacting pad | Major | AOI / Visual |
| Damaged Component | Cracked or chipped package | Major | Visual |

### 3.3 Solder Paste Printing Defects

| Defect Name | Description | Severity | Detection Method |
|---|---|---|---|
| Paste Misalignment | Paste offset from pad | Major | SPI / AOI |
| Paste Insufficient | Not enough paste deposited | Major | SPI |
| Paste Excess | Too much paste | Major | SPI |
| Paste Slump | Paste spreads beyond stencil area | Major | SPI |
| Paste Void | Air pockets inside paste | Minor | X-ray |

### 3.4 PCB / Pad / Surface Defects

| Defect Name | Description | Severity | Detection Method |
|---|---|---|---|
| Pad Lift | Pad lifted from PCB | Critical | Visual |
| Contamination | Dust, oil, flux residue | Major | AOI / Visual |
| Scratch | Surface scratch or abrasion | Minor | Visual |
| Silkscreen Error | Incorrect or missing marking | Minor | Visual |
| Copper Exposure | Exposed copper due to mask issue | Major | Visual |

### 3.5 Electrical / Circuit Defects

| Defect Name | Description | Severity | Detection Method |
|---|---|---|---|
| Open Circuit | Broken trace or connection | Critical | ICT / AOI |
| Short Circuit | Unintended electrical connection | Critical | AOI |
| Trace Damage | Scratched or broken copper trace | Major | Visual |
| Via Defect | Poor plating or non-conductive via | Major | X-ray |

### 3.6 Connector / Mechanical Defects

| Defect Name | Description | Severity | Detection Method |
|---|---|---|---|
| Bent Pin | Deformed connector pin | Major | AOI / Visual |
| Pin Height Error | Incorrect pin height | Major | 3D AOI |
| Partial Insertion | Connector not fully seated | Critical | AOI / Visual |
| Shield Can Gap | Gap between shield can and PCB | Major | Side-View AOI |

## 4. Mandatory AOI Defect Set

The following defects must be included in all AOI recipes:

- Missing Component
- Misalignment
- Polarity Error
- Solder Bridge
- Tombstone
- Cold Joint
- Shield Can Gap
- Connector Pin Height
- 3D Coplanarity
- Solder Volume

## 5. Usage

This document is intended for:

- AOI recipe development
- AI vision model labeling
- QC inspection standardization
- Customer PoC documentation
- Manufacturing engineering guidelines

End of Document
