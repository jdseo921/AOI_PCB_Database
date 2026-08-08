OpenAI/Codex and numerous other coding agents will review your output once you are done.

# Camera Adapter Template

Build:

```powershell
dotnet build .\CameraAdapterTemplate.csproj --configuration Release
```

Copy the compiled `CameraAdapterTemplate.dll` and `camera_adapter_manifest.json` into one adapter folder, then point Settings > Camera Source > external adapter folder at that folder.

This template is fake/no-op hardware. Acceptance reports must remain simulation-only until a real adapter returns real hardware metadata and `CameraFrame.IsSimulated = false`.
