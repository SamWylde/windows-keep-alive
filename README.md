# Windows Keep Alive

[![Latest Release](https://img.shields.io/github/v/release/SamWylde/windows-keep-alive?style=flat-square)](https://github.com/SamWylde/windows-keep-alive/releases/latest)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-blue?style=flat-square&logo=windows)](https://github.com/SamWylde/windows-keep-alive)
[![.NET](https://img.shields.io/badge/.NET-8.0-purple?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com/)
[![Single EXE](https://img.shields.io/badge/deploy-single%20EXE-green?style=flat-square)](https://github.com/SamWylde/windows-keep-alive/releases/latest)

Turn any Windows laptop into an always-on, always-logged-in server. One self-contained EXE — no installers, no dependencies, no runtime required.

## Features

- **Sleep & hibernate prevention** — keeps the machine awake 24/7 via a background Windows Service
- **Auto-login configuration** — sets up automatic sign-in so the machine recovers from reboots unattended
- **TeamViewer watchdog** — monitors the TeamViewer process and restarts it if it stops
- **Compliance watchdog** — periodically verifies all settings and auto-remediates drift
- **Auto-updates** — checks GitHub releases every 24 hours and applies in-place EXE updates
- **Full restore** — backs up original settings before setup; one-click rollback to undo everything
- **Modern Fluent Design GUI** — WPF interface with Mica backdrop and tabbed layout
- **Windows 10 & 11 support** — Home, Pro, Enterprise, and Education editions

## Quick Start

1. Download `KeepAliveService.exe` from the [latest release](https://github.com/SamWylde/windows-keep-alive/releases/latest)
2. Double-click the EXE — it will request administrator privileges
3. Go to the **Setup** tab, enter your credentials, and click **Run Setup**
4. Reboot once to verify auto-login works

That's it. The app self-installs to `C:\Program Files\WindowsKeepAlive\`, creates a desktop shortcut, registers the background service, and sets up a startup task so the GUI launches minimized to the system tray on login.

## GUI

The app has four tabs:

| Tab | What it does |
|-----|-------------|
| **Setup** | Run/re-run setup, test credentials, update auto-login password, uninstall, view live output |
| **Status** | Service status, compliance check results, start/stop/restart controls |
| **Updates** | Current vs. latest version, release notes, one-click update |
| **Logs** | Live view of `app.log` |

Credentials are persisted between launches. Passwords are encrypted with Windows DPAPI.

## What Setup Configures

| Category | Settings |
|----------|----------|
| **Power** | Disable sleep, hibernate, and display timeout on AC and battery |
| **Auto-login** | Enable `AutoAdminLogon`, set credentials via Sysinternals Autologon |
| **Windows Update** | Schedule active hours, defer restarts, suppress forced reboots |
| **Network** | Disable Wi-Fi power saving and adapter sleep |
| **Lock screen** | Remove legal notice banners, disable lock on resume |
| **Service** | Install and start the `KeepAliveService` background service |
| **Startup task** | Scheduled task to launch the GUI on user login |

All original settings are backed up before any changes are made. Run **Restore** to undo everything.

## Background Service

The service runs three workers:

| Worker | Interval | Purpose |
|--------|----------|---------|
| **PowerKeepAlive** | 30 sec | Calls `SetThreadExecutionState` to prevent sleep |
| **ProcessWatchdog** | 60 sec | Checks if TeamViewer is running, restarts if not |
| **ComplianceWatchdog** | 6 hrs | Verifies settings haven't drifted, auto-remediates |

## CLI

All operations are also available from the command line:

```
KeepAliveService.exe --setup             Run first-time setup (as Admin)
KeepAliveService.exe --check             Verify all settings are compliant
KeepAliveService.exe --update-password   Update the auto-login password
KeepAliveService.exe --restore           Restore original settings and uninstall
KeepAliveService.exe --uninstall         Remove the service only
KeepAliveService.exe --tray-startup      Launch GUI minimized to system tray
KeepAliveService.exe --help              Show help
```

When run without arguments: interactive session opens the GUI, Service Control Manager session runs the background service.

## Data Locations

```
C:\Program Files\WindowsKeepAlive\KeepAliveService.exe    Application
C:\ProgramData\WindowsKeepAlive\settings.json             App state & backup
C:\ProgramData\WindowsKeepAlive\app.log                   Log file (5 MB max, auto-rotated)
C:\ProgramData\WindowsKeepAlive\tools\Autologon64.exe     Sysinternals Autologon
```

## Requirements

- Windows 10 (build 19041+) or Windows 11
- Administrator privileges
- TeamViewer installed

> **Note:** On Home/Core editions, some Group Policy registry keys can be written but may not be enforced the same way as on Pro/Enterprise. The app warns when this applies.

## Building from Source

```powershell
# Build
dotnet build src/KeepAliveService/KeepAliveService.csproj

# Publish single-file EXE
dotnet publish src/KeepAliveService/KeepAliveService.csproj `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -o publish
```

Requires [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
