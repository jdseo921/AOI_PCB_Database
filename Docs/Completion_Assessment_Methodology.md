# Completion Assessment Methodology

The Completion Matrix is an internal gap report. It is designed to show how much objective evidence exists for each factory-readiness stage and to prevent simulated or prototype evidence from being presented as production completion.

Scores are calculated from persisted evidence records in the local SQLite database and service settings. They are not hardcoded claims of readiness. If evidence has not been recorded by the relevant test, package, export, or configuration service, the criterion remains missing.

## Scoring Areas

Each area is scored independently from 0 to 100 percent.

| Area | Criteria and Weights |
| --- | --- |
| Stage 1 image validation | Customer validation package recorded 40, persisted validation batch run 25, false-call reduction evidence 20, export verification 15 |
| Stage 2 camera/lighting/3D | Real camera acceptance PASS 35, real lighting sync PASS 25, real 3D profile PASS 25, simulated boundary exercise 15 |
| Stage 3 robot/safety | Real robot cell acceptance PASS 35, real safety/PLC interlock evidence 30, invalid transition/reset checks 20, robot audit events 15 |
| Stage 4 MES/ERP | Passing traceability acceptance 35, MES REST ready 25, MES queue clear 20, central sync status visible and healthy 20 |
| Model readiness | Active model registered 15, runtime validation completed 15, PASS model acceptance 35, ProductionCandidate/Deployed lifecycle 20, release package path recorded 15 |
| Reliability/soak | Soak run recorded 25, at least 30 minutes stability evidence 20, 8-hour factory evidence 30, no failed cycles/critical errors 15, latency traces 10 |
| Management/commercial readiness | Factory readiness package exported 25, factory acceptance checklist/package exported 20, management dashboard exported 20, passing build/test evidence 20, LocalUsers accountability mode 15 |

The overall percentage is the average of the area percentages. It is a gap indicator, not a Go/No-Go decision. Go/No-Go remains controlled by the Factory Readiness profiles and acceptance gates.

## Simulation Evidence

Simulation evidence is useful for software smoke tests and integration rehearsals, but it cannot satisfy real hardware criteria. A simulated camera, lighting controller, 3D profile source, robot controller, safety controller, MES endpoint, or central sync target may add evidence that the software path was exercised, but the real-hardware or real-integration weighted criteria remain missing.

The matrix deliberately separates simulation boundary evidence from real factory evidence. This prevents a Stage 2 or Stage 3 score from reaching production-level completion unless acceptance records show real adapters, real devices, and real safety behavior.

## How Evidence Changes Scores

Customer dataset execution and Stage 1 package export increase only the Stage 1 image validation score. They do not imply camera, lighting, robot, MES, soak, or commercial readiness.

Model records increase model readiness only when the registry, runtime validation, model acceptance, lifecycle promotion, and release package evidence are present. A registered model without PASS acceptance or lifecycle promotion remains incomplete.

Hardware scores increase when camera, lighting, 3D, robot, and safety acceptance records are recorded as real hardware with passing status. Simulated acceptance records remain visible but do not satisfy real-hardware weights.

MES/ERP completion depends on traceability test evidence, MES REST readiness, queue health, and central sync visibility. Local queue status alone is not enough to claim an integrated factory system.

Reliability completion depends on soak-test and latency evidence. Short or simulated soak runs are recorded as partial evidence, while the 8-hour factory criterion requires the service to mark the run as completed factory evidence.

Management/commercial readiness depends on exported readiness/FAT/dashboard packages, imported build/test evidence, and non-demo local accountability mode. Demo role selection produces a readiness warning and does not satisfy accountability completion.
