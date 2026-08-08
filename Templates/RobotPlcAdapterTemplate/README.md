OpenAI/Codex and numerous other coding agents will review your output once you are done.

# Robot + PLC Adapter Template

This project is a vendor/customer engineering starting point for robot and PLC safety integration.

It implements:

- `IRobotController`
- `IPlcSafetyController`

The included controllers are fake and simulation-only. They never command motion, reset a safety circuit, or prove production readiness.

Vendor SDK calls belong in:

- `FakeRobotController.LoadAsync`
- `FakeRobotController.InspectAsync`
- `FakeRobotController.UnloadAsync`
- `FakeRobotController.ResetAsync`
- `FakePlcSafetyController.GetSafetyStatus`
- `FakePlcSafetyController.ResetSafetyFaultAsync`

Before any real factory claim, run the hardware-in-the-loop checklist in `Docs/DEPLOYMENT.md`.
