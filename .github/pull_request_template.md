## Quality Checklist

Complete every applicable item before requesting review. Mark an item `N/A` only when it is genuinely not relevant, and add the reason in the evidence section.

- [ ] I ran `dotnet build AOI_PCB_Database.slnx --configuration Release`.
- [ ] I ran `dotnet test AOI_PCB_Database.slnx --configuration Release`.
- [ ] I ran `pwsh Scripts/run-quality-gates.ps1 -Configuration Release`.
- [ ] I checked `DESIGN.md` and did not introduce forbidden UI patterns.
- [ ] I ran the HMI layout audit.
- [ ] I verified no clipped text at 1920x1080 / 125% DPI.
- [ ] I added a `ScrollViewer` or adaptive layout for dense UI.
- [ ] I did not block the UI thread in page constructors.
- [ ] I handled expected exceptions.
- [ ] I did not expose secrets.
- [ ] I did not commit customer images, production images, local runtime SQLite databases, WAL/SHM files, image vaults, generated exports, or package output.
- [ ] I did not commit vendor SDK binaries to `AOI_Monitor`; vendor adapters remain isolated from the app unless reviewed as a plugin/template boundary.
- [ ] I labeled simulation, mock, fake, folder, null, prototype-only, or dry-run evidence as simulation/non-production evidence.
- [ ] I did not claim real hardware readiness from simulation, dry-run, folder, fake, null, or mock evidence.
- [ ] I did not claim Stage 2 camera pilot readiness unless real camera, lighting, and 3D acceptance evidence is attached or explicitly cited.
- [ ] I did not use "production ready", "factory accepted", "MES integrated", "Stage 2 complete", or equivalent wording without the required evidence and formal scope.
- [ ] I updated docs if workflow changed.
- [ ] I updated requirements/status documentation when implementation status, readiness gates, deployment profile scope, or evidence boundaries changed.
- [ ] I added or updated tests.
- [ ] I verified export/report checksums when relevant.
- [ ] I included export verification evidence for package, report, artifact, or export-format changes.

## Evidence

- Build/test/quality gate command or CI run:
- HMI layout audit report:
- Navigation/performance evidence, if relevant:
- Export/report checksum evidence, if relevant:
- Stage 2 real camera/lighting/3D acceptance evidence, if claimed:
- Hardware/MES validation evidence, if relevant:
- Documentation updates, if requirements/status changed:

## Risk Notes

Describe operator-facing risk, rollout impact, and any manual checks reviewers should repeat.
