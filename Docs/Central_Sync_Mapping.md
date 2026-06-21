# Central Sync Mapping

AOI Monitor keeps local SQLite as the offline source of truth. Central sync is optional and creates reporting payloads for management aggregation across stations.

## Modes

- Disabled: no central aggregation is attempted.
- FileDrop: writes JSON payloads to a configured folder.
- RestApi: boundary only in this build; no production client is installed.
- PostgreSqlBoundary: interface boundary only; no Npgsql package or production database writer is bundled.

## Local To Central Mapping

| Local SQLite source | Central payload type | Central reporting target |
| --- | --- | --- |
| InspectionResults | InspectionResult | station inspection results, model/version evidence, verdict, confidence, timing |
| Defects | Included with future InspectionResult detail expansion | defect detail / ROI evidence |
| ReviewEvents | ReviewEvent | review and disposition audit trail |
| ValidationPackages | ValidationPackage | customer validation package status and manifest references |
| ExportVerification | ExportVerification | package/export integrity evidence |
| Camera/Lighting/Robot/Profile3D/Soak/MES acceptance tables | Future acceptance report payloads | factory readiness evidence by station |

## Data Handling

Raw customer images are not uploaded by default. Image and package paths can be redacted, and image references are included only when central sync settings explicitly allow image inclusion. Secrets and endpoints are redacted from exported queue reports when configured.

Central sync failures leave queue items pending for retry. Failed central connectivity must not remove or modify local SQLite records.
