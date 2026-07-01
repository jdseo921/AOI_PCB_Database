# Vendor Adapter Implementation Guide

Vendor/customer hardware adapters must live outside the main app. Do not add Basler, Hikrobot, Cognex, Keyence, robot, PLC, or lighting SDK packages to `AOI_Monitor`. Start from the templates under `Templates/`, build the adapter, and package the compiled DLL plus manifest in a customer-specific plugin folder.

## Camera Adapter Requirements

Implement `IVisionCameraAdapterFactory`, `IVisionCameraAdapter`, and `IVisionDeviceDiscovery`.

### Camera Manifest Schema

The adapter manifest must be named `camera_adapter_manifest.json` or `*.camera-adapter.json` and include:

- `camera_adapter_manifest.json`
- `adapterId`
- `displayName`
- `version`
- `assemblyFile`
- `factoryTypeName`
- `supportedInterfaces`
- `supportedViews`
- `supportedPixelFormats`

### Camera Frame Metadata Requirements

Every accepted frame must provide:

- stable `FrameId`
- `CameraId` from the real device serial, IP, or vendor ID
- correct `ViewType`
- UTC capture timestamp
- width and height above acceptance criteria
- pixel format from the configured required set
- `SourceKind` naming the real adapter/source
- `IsSimulated = false` only when the frame came from real hardware

Fake, replay, folder, SDK sample, or metadata-only frames must set `IsSimulated = true`.

## Simulated vs Real Hardware

Simulation is useful for UI and timing dry runs, but it is not factory readiness evidence. Acceptance reports remain `NOT VALIDATED` for real hardware readiness when:

- the source is folder/null/fake
- frames are marked simulated
- source metadata is missing
- hardware serial/device identity is not present
- the adapter cannot prove live acquisition

Only real devices with real frame metadata may produce real hardware acceptance evidence.

## Timing Requirements

Adapters must respect configured timeouts and avoid blocking the UI thread. Camera adapters should enforce connect, trigger, first-frame, and frame timeout values. Lighting and robot adapters must return bounded `Task` results and honor cancellation tokens.

For camera acceptance, expect checks on:

- connect latency
- first-frame latency
- average frame interval
- dropped-frame rate
- trigger failure rate
- trigger-to-frame timing when software trigger is enabled

## Safety Warnings

Robot and PLC adapters are safety-critical. The app interfaces are software boundaries only; they are not a safety controller, not an emergency-stop circuit, and not safety certification. Real robot enablement must be reviewed with:

- physical emergency stop validation
- guard door/light curtain checks
- air pressure and clamp interlock checks
- servo-ready and motion-permit checks
- PLC fault reset behavior
- site lockout/tagout and commissioning procedure

Do not auto-load robot motion plugins from an unreviewed folder. Register robot controllers during an explicit commissioning/bootstrap step.

## Running Acceptance Tests

1. Build the adapter project in Release.
2. Copy the adapter DLL and manifest into one plugin folder.
3. Configure the app to use the plugin folder.
4. Run the matching acceptance action:
   - Settings > Camera Source > Discover Cameras, then Run Camera Acceptance Test
   - Settings > Lighting Sync > Run Lighting Sync Test
   - Settings > Robot Cell Acceptance > Run Robot Cell Acceptance
5. Export the acceptance report/package.
6. Review whether the report says real hardware is validated. Fake templates must remain simulation-only.

## Vendor Onboarding Checklist

Use this checklist before a vendor camera adapter is delivered for Stage 2 camera pilot review:

- Keep vendor SDK references, redistributables, licenses, and native runtime files in the external adapter project/package only. Do not add vendor SDK dependencies to `AOI_Monitor`.
- Package one camera adapter folder with `camera_adapter_manifest.json` or one `*.camera-adapter.json` file, the compiled adapter assembly, and any licensed runtime files needed by that adapter.
- Confirm the manifest includes non-empty `adapterId`, `displayName`, `version`, `assemblyFile`, `factoryTypeName`, `supportedInterfaces`, `supportedViews`, and `supportedPixelFormats`.
- Confirm the factory identity and capabilities match the manifest exactly enough for `VisionCameraPluginLoader` to load the adapter.
- Implement bounded connect, start, trigger, frame, stop, and disconnect behavior. Do not block UI-facing workflows; long hardware calls must respect configured timeouts.
- Implement discovery when the SDK supports it, returning real device ID, vendor, model, serial, interface, suggested view, status, and capabilities.
- Return complete frame metadata for each accepted frame: stable frame ID, real camera ID, view, UTC timestamp, dimensions, pixel format, source kind, board/lot context when configured, and acquisition timing.
- Set `CameraFrame.IsSimulated = false` only for live frames acquired from the real camera. Fake, replay, folder, metadata-only, SDK sample, and template adapters must set `IsSimulated = true`.
- Run the package validator:

```powershell
pwsh Scripts/validate-camera-adapter-package.ps1 `
  -AdapterFolder C:\VendorPackages\CustomerVendor.CameraAdapter `
  -SettingsJson C:\VendorPackages\camera_acceptance_settings.json `
  -OutputFolder C:\AOI_Evidence\camera_adapter_validation
```

`-SettingsJson` is optional. When supplied, it may contain a `CameraSourceSettings` JSON object directly, or an object with `cameraSourceSettings` and `acceptanceCriteria` sections. The validator writes JSON/HTML summary files and a camera acceptance JSON/HTML report under the output folder.

PASS/WARN output is not the same as factory acceptance. A fake/template adapter should load and may produce a WARN validation package, but its factory readiness must remain `NOT VALIDATED`. Real Stage 2 camera readiness requires live hardware frames with `IsSimulated=false`, real device metadata, and acceptable timing/metadata results.

## Packaging Plugin Folder

Recommended layout:

```text
CustomerVendor.CameraAdapter/
  camera_adapter_manifest.json
  CustomerVendor.CameraAdapter.dll
  vendor-runtime-files-if-licensed/
  README.md
```

Lighting uses `lighting_adapter_manifest.json` or `*.lighting-adapter.json` with:

- `driverId`
- `displayName`
- `version`
- `assemblyFile`
- `factoryTypeName`
- `supportedModes`

Robot templates include `robot_controller_manifest.json` plus documented registration because robot motion plugins are not automatically loaded by the app.

Do not commit generated plugin binaries, vendor redistributables, secrets, customer images, or runtime logs to this repository.
