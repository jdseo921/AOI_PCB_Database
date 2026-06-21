# Robot Controller Template

Build:

```powershell
dotnet build .\RobotControllerTemplate.csproj --configuration Release
```

Robot motion is not loaded by the app through a drop-folder plugin loader. Register controllers during a reviewed commissioning/bootstrap step:

```csharp
IntegrationBoundaryRegistry.RobotController = new FakeRobotController();
IntegrationBoundaryRegistry.PlcSafetyController = new FakePlcSafetyController();
```

This template is fake/no-op hardware. It must remain simulation-only until a site-specific adapter is reviewed with real robot, PLC, interlock, and emergency-stop evidence.
