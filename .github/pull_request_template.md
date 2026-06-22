## Quality Checklist

Complete every applicable item before requesting review. Mark an item `N/A` only when it is genuinely not relevant, and add the reason in the evidence section.

- [ ] I ran `dotnet build AOI_PCB_Database.slnx --configuration Release`.
- [ ] I ran `dotnet test AOI_PCB_Database.slnx --configuration Release`.
- [ ] I checked `DESIGN.md` and did not introduce forbidden UI patterns.
- [ ] I ran the HMI layout audit.
- [ ] I verified no clipped text at 1920x1080 / 125% DPI.
- [ ] I added a `ScrollViewer` or adaptive layout for dense UI.
- [ ] I did not block the UI thread in page constructors.
- [ ] I handled expected exceptions.
- [ ] I did not expose secrets.
- [ ] I did not claim real hardware readiness from simulation.
- [ ] I updated docs if workflow changed.
- [ ] I added or updated tests.
- [ ] I verified export/report checksums when relevant.

## Evidence

- Build/test command or CI run:
- HMI layout audit report:
- Navigation/performance evidence, if relevant:
- Export/report checksum evidence, if relevant:
- Hardware/MES validation evidence, if relevant:

## Risk Notes

Describe operator-facing risk, rollout impact, and any manual checks reviewers should repeat.
