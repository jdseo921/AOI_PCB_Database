# AOI PoC Software GUI - Concept & Functional Specification (Extended Version)

## 1. Purpose

This document defines the concept, functional requirements, and development roadmap for the AOI (Automated Optical Inspection) Proof of Concept (PoC) software GUI. It includes four progressive implementation stages - from image-based AI testing to full integration with hardware, robotics, and MES/ERP systems.

## 2. System Overview

The AOI PoC software automates visual inspection of Telematics T-Box PCBs using 2D, 3D, and side-view imaging. It operates as a standalone QC cell, independent of SMT lines, and evolves through four stages:

| Stage | Description | Objective |
|---|---|---|
| Stage 1 | Image-based AI learning and validation | Train and validate AI using uploaded photos; deliver results to customer for verification |
| Stage 2 | AI Vision camera hardware integration | Connect actual 2D/3D cameras; perform live inspection; customer validation on real boards |
| Stage 3 | Robot integration | Automate PCB loading/unloading and inspection positioning |
| Stage 4 | MES/ERP integration | Enable data synchronization, traceability, and production analytics |

## 3. GUI Architecture

The GUI consists of five main modules:

1.  **Main Inspection Screen** - Operator interface for live inspection.

2.  **Recipe Editor** - Engineer interface for ROI and threshold setup.

3.  **AI Model Test Screen** - Validation tool for AI performance.

4.  **Log & Export Screen** - Data management and reporting.

5.  **3D Profile Viewer** - Visualization of height and coplanarity defects.

Each module follows industrial HMI design principles: high contrast, large buttons, minimal text, and clear color coding.

## 4. Functional Requirements

### 4.1 Main Inspection Screen

**Purpose:** Execute inspection and visualize results.

**Functions:**

- Display live camera feed (Top, Side, Bottom views).

- Overlay detected defects with bounding boxes and labels.

- Show defect list with columns: No, Type, Score, Side, X, Y.

- Control buttons: Start, Stop, Next Board, Save Result.

- Display alarm log with timestamps and messages.

**UI Behavior:**

- Real-time update of defect overlays.

- Color coding: Green (OK), Red (NG), Yellow (Warning).

- Auto-save inspection results after each board.

### 4.2 Recipe Editor

**Purpose:** Define inspection zones and thresholds.

**Functions:**

- ROI drawing and editing on image.

- ROI types: Presence, Polarity, Solder Bridge, Height, Anomaly.

- Parameter input fields: AI Score, Height Min/Max, Volume Min/Max.

- Buttons: Test Run, Save Recipe.

**UI Behavior:**

- Display ROI boundaries in color (yellow for active, green for saved).

- Allow zoom/pan for precise ROI placement.

- Save recipe revisions with timestamp and user ID.

### 4.3 AI Model Test Screen

**Purpose:** Validate AI model performance using sample data.

**Functions:**

- Batch test folder selection.

- Display metrics: Accuracy, Precision, Recall, False Call Rate.

- Show results table: Image, GT, AI Result, Score, Pass/Fail.

- Buttons: Run Test Again, Export CSV, Export Report.

**UI Behavior:**

- Highlight failed samples in red.

- Allow image preview for each test case.

- Store test results in local database.

### 4.4 Log & Export Screen

**Purpose:** Manage inspection history and export data.

**Functions:**

- Display log table: Time, Model, Result, Defects.

- Export options: CSV, Image Overlay.

- Filter by date, model, or operator.

**UI Behavior:**

- Sortable columns.

- Confirmation dialog before export.

- Auto-archive logs older than 30 days.

### 4.5 3D Profile Viewer

**Purpose:** Visualize height and coplanarity defects.

**Functions:**

- Display 3D height map (color-coded scale).

- Show defect details: Type, Height, Volume.

- Display height slice graph with peak markers.

- Buttons: Accept Defect, Reject Defect.

**UI Behavior:**

- Interactive controls: Rotate, Zoom, Pan.

- Dynamic height scale legend.

- Synchronize with defect list selection.

## 5. Development Stages & Requirements

### **Stage 1 - Image Upload & AI Learning**

**Objective:** Validate AI model using existing PCB photos.

**Requirements:**

- GUI supports image upload (PNG/JPG).

- AI inference engine processes images offline.

- Display defect overlays and confidence scores.

- Export test results for customer validation.

- Deliver trained AI model and report to customer.

**Deliverables:**

- AI model (.pt or .h5 format)

- Test result CSV and annotated images

### **Stage 2 - AI Vision Camera Integration**

**Objective:** Connect real cameras for live inspection.

**Requirements:**

- Support GigE / USB3 Vision cameras.

- Real-time image acquisition and processing.

- Synchronize camera trigger and lighting control.

- Display live feed in GUI (Top, Side, Bottom views).

- Customer validation on actual boards.

**Deliverables:**

- Hardware integration test report

- Live inspection demo

### **Stage 3 - Robot Integration**

**Objective:** Automate PCB handling and positioning.

**Requirements:**

- Interface with robot controller via Ethernet or RS-485.

- Commands: Load, Inspect, Unload.

- Synchronize robot motion with camera trigger.

- Safety interlock and emergency stop integration.

**Deliverables:**

- Robot control API documentation

- Integrated inspection cycle demo

### **Stage 4 - MES/ERP Integration**

**Objective:** Enable production data synchronization and traceability.

**Requirements:**

- Connect to MES/ERP via REST API or OPC UA.

- Data exchange: Lot ID, Model, Result, Timestamp.

- Upload inspection results and images.

- Support user authentication via MES.

**Deliverables:**

- MES/ERP integration test report

- End-to-end traceability validation

## 6. Technical Requirements

### Platform

- OS: Windows 10/11 Industrial Edition.

- Framework: .NET / C# or Python (PyQt / Tkinter).

- GPU acceleration for AI inference (NVIDIA CUDA).

### Hardware Interface

- 2D/3D cameras (GigE / USB3 Vision).

- Lighting control via serial or Ethernet.

- Robot and MES communication via TCP/IP.

### Data Management

- Local SQLite or PostgreSQL database.

- Image storage path configurable.

- Export format: CSV, PNG, PDF.

### AI Integration

- TensorFlow / PyTorch inference engine.

- Model version control.

- Configurable confidence threshold.

## 7. UI Design Guidelines

- Resolution: 1920×1080 minimum.

- Color palette: Industrial blue/gray background, green/red/yellow indicators.

- Font: Sans-serif, minimum 14pt.

- Button size: ≥ 120×40 px.

- Layout grid: 12-column responsive design.

## 8. User Roles & Permissions

- **Operator:** Run inspection, view results.

- **Engineer:** Edit recipes, test AI models.

- **Admin:** Manage users, export logs, system settings.

## 9. Data Flow Summary

1.  Operator loads PCB → Camera captures image → AI detects defects → Results displayed.

2.  Engineer adjusts recipe → Tests AI model → Deploys updated parameters.

3.  Logs and reports exported for QC documentation.

4.  MES/ERP integration ensures traceability and analytics.

## 10. Deliverables

- GUI source code and assets.

- Database schema.

- AI model integration module.

- Hardware interface drivers.

- User manual and installation guide.

## 11. Acceptance Criteria

- GUI matches mockups and functional flow.

- Real-time defect visualization within 1 second per image.

- Stable operation for 8-hour continuous PoC testing.

- Exported reports verified for accuracy.

- Successful integration with camera, robot, and MES.

## 12. Future Expansion

- Inline AOI adaptation for SMT line.

- Multi-station dashboard.

- Predictive quality analytics.

- Cloud-based data aggregation.

**End of Specification Document**
