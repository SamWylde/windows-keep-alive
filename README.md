# Windows Keep Alive

Single-EXE Windows 11 keep-alive utility for laptops used like home servers.

It is designed to keep the machine online, keep auto-login configured, and keep TeamViewer available after reboots and updates.

## What It Does

- Runs as a background Windows Service to prevent sleep and watch TeamViewer
- Applies keep-alive setup from a GUI (no required CLI flags)
- Configures update/restart, auto-login, power, and network settings
- Self-installs to `C:\Program Files\WindowsKeepAlive\KeepAliveService.exe`
- Stores app state in `C:\ProgramData\WindowsKeepAlive\`
- Checks GitHub releases and can apply in-place EXE updates

## Requirements

- Windows 11 (Core/Home, Pro, Enterprise, or Education)
- Administrator rights for setup/configuration
- TeamViewer installed

Home/Core note: some settings in `HKLM\SOFTWARE\Policies\...` can be present in the registry but not enforced identically to Pro/Enterprise policy behavior. The app now warns when this applies.

## Quick Start (GUI, recommended)

1. Publish a single EXE:
   ```powershell
   dotnet publish src/KeepAliveService/KeepAliveService.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
   ```
2. Double-click `publish\KeepAliveService.exe`.
3. Accept UAC elevation.
4. In the `Setup` tab, enter credentials and click `Run Setup`.
5. Reboot once to validate auto-login behavior.

On first launch, the app copies itself to `C:\Program Files\WindowsKeepAlive\` and relaunches from there so service registration and updates use a stable canonical path.
It also creates a desktop shortcut named `Windows Keep Alive`.

## GUI Tabs

- `Setup`: run full setup, test credentials, update autologin password, uninstall service, view/copy live output
- `Status`: service status + compliance check + start/stop/restart service controls
- `Updates`: current/latest version, release notes, update apply
- `Logs`: view `C:\ProgramData\WindowsKeepAlive\app.log`

Setup input fields are persisted between launches (username/account/domain/password).  
Password is stored encrypted with Windows DPAPI.

Credential testing and setup now adapt to Windows Hello/passwordless policy state:
- If password sign-in appears blocked by policy, setup stops with remediation guidance.
- If policy state is partially unreadable, setup warns and continues with validation.

## Auto Update

- Checks GitHub releases at startup and every 24 hours
- Uses `https://api.github.com/repos/SamWylde/windows-keep-alive/releases/latest`
- Prefers `KeepAliveService.exe` release asset (fallback: first `.exe`)
- `Update Now` shows download progress, then downloads to temp, stops service, replaces EXE, restarts service, relaunches app
- Rollback path restores `.bak` if replacement fails

## CLI (backward compatible)

You can still run commands from terminal:

```powershell
KeepAliveService.exe --setup
KeepAliveService.exe --check
KeepAliveService.exe --update-password
KeepAliveService.exe --uninstall
KeepAliveService.exe --help
```

When no arguments are provided:

- Interactive session: launches WinForms GUI
- Service Control Manager session: runs as service host

## Data Locations

```text
C:\Program Files\WindowsKeepAlive\KeepAliveService.exe
C:\ProgramData\WindowsKeepAlive\settings.json
C:\ProgramData\WindowsKeepAlive\app.log
C:\ProgramData\WindowsKeepAlive\tools\Autologon64.exe
```

## Build

```powershell
dotnet build src/KeepAliveService/KeepAliveService.csproj
```
