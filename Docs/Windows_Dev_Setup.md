# Windows Dev Setup — Run Claude Code Locally to Build This App

This project is a **WPF app** targeting `net10.0-windows`. WPF only builds on Windows, so a Claude Code session that can build/run it must execute **locally on a Windows machine** — not in a cloud/Linux environment. This guide gets you there.

## Key facts

- **Claude Code is not a separate app.** It is the **Code** tab inside the Claude desktop app.
- The Code tab is **not local by default** — it has a **Local / Remote** switch. **Remote** runs on Anthropic's cloud (Linux). **Local** runs on your Windows machine. You must choose **Local**.
- The **CLI** always runs on the machine where you type `claude`, so it is the most foolproof way to guarantee local native-Windows execution.

## Prerequisites (install on your Windows PC)

- **.NET 10 SDK** — https://dotnet.microsoft.com/download/dotnet/10.0
- **Git for Windows** — https://git-scm.com/downloads/win
- Clone the repo locally (native Windows PowerShell, not WSL):
  ```powershell
  git clone https://github.com/jdseo921/AOI_PCB_Database.git
  cd AOI_PCB_Database
  git checkout claude/aoi-pcb-gui-review-qpqo05
  ```

## Option A — Desktop app (least friction if you already have it)

1. Open the Claude desktop app and go to the **Code** tab.
2. In the environment dropdown, select **Local** (not Remote).
3. Click **Select folder** and choose your cloned `AOI_PCB_Database` directory.

## Option B — CLI (always local; recommended)

In native Windows PowerShell:
```powershell
irm https://claude.ai/install.ps1 | iex   # then close and reopen PowerShell so `claude` is on PATH
cd AOI_PCB_Database
claude
```

## Verify you are local + native (the real test)

In the session, run:
```powershell
ver
dotnet --version
dotnet build AOI_PCB_Database.slnx -c Release
```

- `ver` shows **Microsoft Windows**, `dotnet --version` shows **10.x**, and the build **succeeds** → you are local and native; the WPF app can be built, published, run, and verified in-session.
- Output shows **Linux** or `dotnet` is not found → you are in a Remote/cloud (Linux) session. Switch the desktop dropdown to **Local**, or use the CLI (Option B).

## Do NOT use WSL

WSL is Linux; WPF will not build there. Use a plain Windows PowerShell / Windows Terminal window. Quick check: a native prompt looks like `PS C:\...>`; a WSL prompt looks like `user@machine:~$`.

## Building a distributable manually (optional)

Self-contained build a reviewer can run without the SDK:
```powershell
dotnet publish AOI_Monitor\AOI_Monitor.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o publish\AOI_Monitor
# run publish\AOI_Monitor\AOI_Monitor.exe
```
Or use the repo script: `pwsh Scripts/prepare-client-test-package.ps1 -Zip`.

## Related

- `Docs/Manual_Test_Plan.md` — what to test and what must pass.
- `Docs/Image_Learning_Quickstart_Test.md` — exercise the image-only ML path.
