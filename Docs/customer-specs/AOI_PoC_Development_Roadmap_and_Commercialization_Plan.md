# AOI PoC Software - Development Roadmap & Commercialization Plan (Updated Specification)

## 1. Overview

This document extends the AOI PoC Software Concept & Functional Specification with a **formal development roadmap**, **phase definitions**, and **commercialization timeline**. It reflects the staged rollout strategy for Korea-first market entry, followed by global expansion.

# 2. Development Phases

The AOI solution will be developed and validated in **two major phases**, each containing detailed sub-steps.

## **Phase 1 - PoC Software & Vision Hardware Integration (Total: 16 Weeks)**

Phase 1 focuses on validating AI performance using customer data, then integrating real AI Vision hardware for live inspection.

### **Stage 1 - Image Upload AI Learning & Customer Validation (8 Weeks)**

**Objective:** Build the initial AI engine and GUI using uploaded images.

**Scope:**

- Implement image upload module (PNG/JPG)

- Offline AI inference engine

- Defect overlay visualization

- Batch test tool for customer datasets

- Export of annotated images and CSV reports

- Customer validation of AI accuracy

**Deliverables:**

- AI model (v1.0)

- PoC GUI (Image-based)

- Customer validation report

### **Stage 2 - AI Vision Camera Integration (8 Weeks)**

**Objective:** Connect real 2D/3D cameras and lighting to enable live inspection.

**Scope:**

- GigE / USB3 Vision camera drivers

- Real-time image acquisition

- Lighting control (Ethernet/Serial)

- Top / Side / Bottom view switching

- Real PCB inspection validation

**Deliverables:**

- Live inspection GUI

- Hardware integration test report

- Customer on-site validation

# **Phase 2 - Automation & Factory Integration (Duration: 2027)**

Phase 2 expands the system into a production-ready automation platform.

## **Stage 3 - Robot Integration (Estimated: 12-16 Weeks)**

**Objective:** Automate PCB handling and inspection positioning.

**Scope:**

- Robot controller API integration (Ethernet/RS-485)

- Commands: Load → Inspect → Unload

- Trigger synchronization with camera

- Safety interlock & emergency stop

- Cycle time optimization

**Deliverables:**

- Fully automated inspection cycle

- Robot integration test report

## **Stage 4 - MES/ERP Integration (Estimated: 12-16 Weeks)**

**Objective:** Enable enterprise-level traceability and production data flow.

**Scope:**

- REST API / OPC UA communication

- Lot ID, Model, Result, Timestamp upload

- Image & defect data archiving

- MES-based user authentication

**Deliverables:**

- MES/ERP integration report

- End-to-end traceability validation

# 3. Commercialization Timeline (Korea-first Strategy)

The AOI solution follows a structured commercialization plan aligned with development phases.

## **2026 - PoC Development & Customer Validation**

- **Q2-Q3 2026:** Phase 1 execution (Stages 1 & 2)

- **Q4 2026:** Phase 2 preparation (Robot/MES architecture)

## **2027 - Productization & Market Launch**

### **1Q 2027 - Official Product Release**

- First commercial version ready

- Target: 1 initial customer

- Expected deployment: **5-10 licenses**

### **2Q 2027 - Korea Market Promotion Begins**

- Full marketing & sales activities

- Target: Tier-1 automotive, telecom, EMS manufacturers

- Expected adoption: **up to 50-100 licenses within 24 months**

### **2H 2027 - Overseas Market Promotion**

- Expansion to ASEAN, Japan, and Europe

- Localization (language/UI) and compliance updates

# 4. Market Forecast

### **Initial Customer Deployment (2027)**

- 1 customer

- 5-10 licenses

### **Korea Market (2027-2029)**

- 50-100 licenses across multiple companies

- 24-month adoption window

### **Global Market (2028-2030)**

- Expansion to overseas factories

- Potential for 200+ cumulative licenses

# 5. Summary

This roadmap ensures:

- Fast PoC validation (Phase 1)

- Scalable automation (Phase 2)

- Strong Korea-first commercialization

- Global expansion readiness by late 2027

The AOI platform evolves from a **PoC tool** into a **full production automation system**, aligned with customer expectations and market timing.

**End of Document**
