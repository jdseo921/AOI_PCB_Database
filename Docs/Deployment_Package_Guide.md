# AOI Monitor Deployment Package Guide

This guide describes how to prepare, install, back up, restore, and roll back AOI Monitor on a customer or factory PC.

## Prerequisites

- Windows 10/11 Pro or Enterprise, 64-bit.
- Local administrator rights for first install, firewall rules, and hardware driver/plugin installation.
- .NET Desktop Runtime matching the app target framework when using a framework-dependent package. Self-contained packages include the runtime.
- 8 GB RAM minimum; 16 GB recommended for customer dataset validation.
- SSD storage for the app, local SQLite database, generated overlays, reports, and customer validation packages.
- Vendor camera, lighting, robot, PLC, and MES drivers installed only on factory PCs that will use real hardware.
- A time-synchronized PC clock so audit records, acceptance evidence, and readiness packages have reliable timestamps.

## Package Contents

Customer packages are generated with `Scripts/publish.ps1`.

- `app/` contains the published WPF application.
- `Docs/` is included when documentation is packaged.
- `Templates/` is included only when adapter templates are requested for developer handoff.
- `SampleData/customer_validation_manifest_template.csv` is included only when `-IncludeSampleManifestTemplate` is requested.
- `RUN_RELEASE.md` records package generation settings.

Runtime data is intentionally excluded from release packages: local SQLite databases, image vaults, training images, customer images, generated exports, overlays, and machine-interface JSON.

Common package commands:

```powershell
pwsh Scripts/publish.ps1 -Configuration Release
pwsh Scripts/publish.ps1 -Configuration Release -IncludeTemplates
pwsh Scripts/publish.ps1 -Configuration Release -IncludeSampleManifestTemplate
pwsh Scripts/publish.ps1 -Configuration Release -IncludeTemplates -IncludeSampleManifestTemplate
```

## Install Path

Recommended install path:

`C:\Program Files\AOI Monitor\`

For pilot/customer validation PCs without installer infrastructure, unzip the release package to:

`C:\AOI\AOI_Monitor\`

Run `AOI_Monitor.exe` from the `app` folder. Keep the release folder read-only for operators after configuration is complete.

## Storage Path

Recommended local storage root:

`C:\AOI\Data\AOI_Monitor\`

The storage root contains settings, the SQLite database, image vault, exports, acceptance evidence, and generated readiness/customer packages. Configure it in `Settings > Storage Path` using an Admin role. Do not place production storage inside the application install folder.

For customer dataset validation, keep customer images in a separate dataset folder and reference them from manifests. Do not copy customer datasets into release packages.

## Backup Configuration

Use `Settings > Backup Configuration` as Admin before changing models, thresholds, hardware adapters, MES settings, central sync settings, or storage paths.

The configuration backup includes:

- App settings and first-run state.
- Inspection model configuration and model registry metadata.
- Active and historical threshold profiles.
- Recipe revisions.
- Camera source, lighting, MES, central sync, and deployment profile settings.

The configuration backup excludes customer images and raw production images by default. Keep dataset folders, image vaults, and generated exports in a separate operational backup plan if the customer requires full runtime-data retention.

## Restore Configuration

Use `Settings > Restore Configuration Preview` as Admin. The app validates the backup schema and database schema before allowing restore. Review warnings and blocking issues before applying.

After restore:

- Restart AOI Monitor before production use.
- Confirm storage root, active model, threshold profile, camera source, lighting, MES, and central sync settings.
- Run the relevant acceptance tests before using restored hardware or model settings for customer/factory evidence.

Restore preview checks schema compatibility, target storage path, settings changes, existing model/threshold conflicts, missing model files, and missing plugin folders. Do not apply a restore until blocking issues are cleared and warnings are understood.

## Firewall And Network Notes

Allow outbound network traffic only for explicitly configured integrations:

- MES/ERP REST endpoints.
- Central sync REST or file-drop destinations.
- Vendor camera, lighting, robot, or PLC network interfaces.

Inbound firewall rules should remain closed unless a vendor adapter or factory integration explicitly requires them. Record customer IT approvals, ports, hostnames, and VLAN/subnet constraints in the factory acceptance checklist.

Secrets are stored only in local configuration files and should be protected by Windows account permissions. Rotate MES/API credentials during commissioning and after rollback exercises.

## Hardware Plugin Folder Notes

Hardware/vendor SDK dependencies must remain outside the main application binaries. Place customer or vendor adapter plugins in a dedicated folder such as:

`C:\AOI\Plugins\`

Configure adapter paths in Settings. Keep simulated/fake template adapters labeled as simulated evidence. A fake adapter does not satisfy real camera, lighting, robot, PLC, or MES readiness gates.

Plugin folders should contain:

- Adapter assembly and dependencies.
- Adapter manifest JSON.
- Vendor SDK runtime DLLs when allowed by the vendor license.
- Version notes and acceptance-test evidence for the exact plugin build.

## Rollback Plan

Before upgrading:

1. Export a configuration backup.
2. Record the current release folder name, app version, active model ID, active threshold profile, storage root, and plugin folder.
3. Copy the current release folder to an archive location or keep the previous zip package.
4. Run the publish/package validation script for the new release.

To roll back:

1. Stop AOI Monitor.
2. Restore the previous release folder or unzip the previous package.
3. Restore the last known-good configuration using `Restore Configuration Preview`.
4. Confirm plugin paths still point to the intended adapter build.
5. Re-run model, camera, lighting, robot, traceability, and factory readiness checks that apply to the deployment profile.

Rollback is complete only when the restored app produces the expected audit events, active model/threshold settings, and acceptance evidence for the target deployment stage.

## Run Readiness Package After Install

After installing or restoring configuration on a customer/factory PC:

1. Start AOI Monitor as an Admin user.
2. Open `Settings` and confirm the deployment target, storage path, model registry, threshold profile, camera/plugin folders, MES, and central sync settings.
3. Run the applicable validation actions for the deployment profile:
   - Stage 1: Dataset Preflight, AI Model Test, false-call reduction, model acceptance when using ONNX.
   - Stage 2: Camera, lighting, 3D profile, and latency trace evidence.
   - Stage 3: Robot cell and PLC/safety acceptance evidence.
   - Stage 4: MES traceability signoff and MES queue review.
4. Open `Log & Export`.
5. Export the Factory Readiness Go/No-Go package.
6. Review the HTML/JSON summary and confirm simulated, mock, CSV sample, fake adapter, and not-connected evidence is not treated as real production readiness.
