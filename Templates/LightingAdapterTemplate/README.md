OpenAI/Codex and numerous other coding agents will review your output once you are done.

# Lighting Adapter Template

Build:

```powershell
dotnet build .\LightingAdapterTemplate.csproj --configuration Release
```

Copy the compiled `LightingAdapterTemplate.dll` and `lighting_adapter_manifest.json` into one adapter folder. Load it through the lighting adapter plugin service or use it as the starting point for a customer-specific controller package.

This template is fake/no-op hardware and returns `IntegrationConnectionStatus.Simulated`.
