# Completion Assessment Methodology

The Completion Matrix is an internal gap report. It is designed to show how much objective evidence exists for each factory-readiness stage and to prevent simulated or prototype evidence from being presented as production completion.

Scores are calculated from persisted evidence records in the local SQLite database and service settings. They are not hardcoded claims of readiness. If evidence has not been recorded by the relevant test, package, export, or configuration service, the criterion remains missing.

## Scoring Areas

Each area is scored independently from 0 to 100 percent.

| Area | Criteria and Weights |
| --- | --- |
| Stage 1 image validation | Customer validation package recorded 40, persisted validation batch run 25, false-call reduction evidence 20, export verification 15 |
| Production model readiness | Active model registered 15, runtime validation completed 15, PASS model acceptance 35, ProductionCandidate/Deployed lifecycle 20, release package path recorded 15 |
| False-positive reduction readiness | Validation run has measurable false-call rate 20, false-call sweep completed 30, recommended operating point exists 25, deployed threshold profile linked to false-call evidence 25 |
| Stage 2 camera/lighting/3D | Real camera acceptance PASS 35, real lighting sync PASS 25, real 3D profile PASS 25, simulated boundary exercise 15 |
| Stage 3 robot/safety | Real robot cell acceptance PASS 35, real safety/PLC interlock evidence 30, invalid transition/reset checks 20, robot audit events 15 |
| Stage 4 MES/ERP | Passing traceability acceptance 35, MES REST ready 25, MES queue clear 20, abandoned-item disposition visible 20 |
| Central sync/management | Central sync configured 20, central sync queue has no failed items 20, management dashboard exported 30, central sync or management report exported 30 |
| Reliability/soak | Soak run recorded 25, at least 30 minutes stability evidence 20, 8-hour factory evidence 30, no failed cycles/critical errors 15, latency traces 10 |
| Deployment/supportability | Passing build/test/publish evidence imported 30, configuration backup exported 25, factory readiness package exported 25, factory acceptance checklist/package exported 20 |
| Commercial readiness | LocalUsers accountability mode 25, management dashboard evidence exported 20, FullFactoryAutomation has no blocking issues 25, release/support build evidence 15, customer/commercial package exported 15 |

The overall percentage is the average of the area percentages. It is a gap indicator, not a Go/No-Go decision. Go/No-Go remains controlled by the Factory Readiness profiles and acceptance gates.

## Simulation Evidence

Simulation evidence is useful for software smoke tests and integration rehearsals, but it cannot satisfy real hardware criteria. A simulated camera, lighting controller, 3D profile source, robot controller, safety controller, MES endpoint, or central sync target may add evidence that the software path was exercised, but the real-hardware or real-integration weighted criteria remain missing.

The matrix deliberately separates simulation boundary evidence from real factory evidence. This prevents a Stage 2 or Stage 3 score from reaching production-level completion unless acceptance records show real adapters, real devices, and real safety behavior.

## How Evidence Changes Scores

Customer dataset execution and Stage 1 package export increase only the Stage 1 image validation score. They do not imply camera, lighting, robot, MES, soak, or commercial readiness.

Model records increase model readiness only when the registry, runtime validation, model acceptance, lifecycle promotion, and release package evidence are present. A registered model without PASS acceptance or lifecycle promotion remains incomplete.

Hardware scores increase when camera, lighting, 3D, robot, and safety acceptance records are recorded as real hardware with passing status. Simulated acceptance records remain visible but do not satisfy real-hardware weights.

MES/ERP completion depends on traceability test evidence, MES REST readiness, queue health, and abandoned-item disposition. Central sync and management reporting are scored separately so a local MES queue does not imply enterprise aggregation readiness.

Reliability completion depends on soak-test and latency evidence. Short or simulated soak runs are recorded as partial evidence, while the 8-hour factory criterion requires the service to mark the run as completed factory evidence.

Central sync and management readiness increase when central sync is configured, the queue is healthy, and management dashboard or central-sync reports are exported. The dashboard uses local SQLite first and does not require a central server to generate the gap report.

Deployment/supportability increases when build/test/publish evidence, configuration backup evidence, factory readiness packages, and FAT checklist packages exist. This category is meant to answer whether another customer/factory PC can be installed, restored, and reviewed.

Commercial readiness depends on LocalUsers accountability, management review exports, customer/commercial packages, and FullFactoryAutomation having no blocking issues. Demo role selection produces a readiness warning and does not satisfy accountability completion.
