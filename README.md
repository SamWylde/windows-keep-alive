# Windows Keep Alive

[![Latest Release](https://img.shields.io/github/v/release/SamWylde/windows-keep-alive?style=flat-square)](https://github.com/SamWylde/windows-keep-alive/releases/latest)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-blue?style=flat-square&logo=windows)](https://github.com/SamWylde/windows-keep-alive)
[![.NET](https://img.shields.io/badge/.NET-8.0-purple?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com/)
[![Single EXE](https://img.shields.io/badge/deploy-single%20EXE-green?style=flat-square)](https://github.com/SamWylde/windows-keep-alive/releases/latest)

Keep a Windows laptop awake, or turn it into a more complete unattended remote-access box when you choose to. One self-contained EXE, no installer bundle or runtime required.

## Features

- **Setup modes** - choose keep-awake only, keep-awake plus TeamViewer monitoring, or full unattended auto-login
- **Sleep & hibernate prevention** - keeps the machine awake 24/7 via a named Windows power request and background Windows Service
- **Auto-login configuration** - optional full mode sets up automatic sign-in so the machine recovers from reboots unattended
- **TeamViewer watchdog** - optional remote/full modes monitor TeamViewer and restart it if it stops
- **Compliance watchdog** - periodically verifies all settings and auto-remediates drift
- **Auto-updates** - checks GitHub releases every 24 hours and applies in-place EXE updates
- **Full restore** - backs up original settings before setup; one-click rollback to undo everything
- **Modern Fluent Design GUI** - WPF interface with Mica backdrop and tabbed layout
- **Windows 10 & 11 support** - Home, Pro, Enterprise, and Education editions

## Quick Start

1. Download `KeepAliveService.exe` from the [latest release](https://github.com/SamWylde/windows-keep-alive/releases/latest)
2. Double-click the EXE - it will request administrator privileges
3. Go to the **Setup** tab, choose a mode, and click **Run Setup**
4. For **Full unattended auto-login**, enter credentials and reboot once to verify auto-login works

That's it. The app self-installs to `C:\Program Files\WindowsKeepAlive\`, creates a desktop shortcut, registers the background service, and sets up a startup task so the GUI launches minimized to the system tray on login.

## GUI

The app has four tabs:

| Tab | What it does |
|-----|-------------|
| **Setup** | Choose a setup mode, run/re-run setup, test credentials, update auto-login password, restore, uninstall, view live output |
| **Status** | Service status, compliance check results, start/stop/restart controls |
| **Updates** | Current vs. latest version, release notes, one-click update |
| **Logs** | Live view of `app.log` |

Credentials are only required for **Full unattended auto-login**. Saved passwords are encrypted with Windows DPAPI.

## Setup Modes

| Mode | Configures |
|------|------------|
| **Keep awake only** | Power settings, background keep-awake service, startup task |
| **Keep awake + remote access watchdog** | Keep-awake settings plus network power hardening and TeamViewer monitoring |
| **Full unattended auto-login** | Remote watchdog mode plus auto-login, lock-screen policy, and Windows Update restart policy |

To change modes, select the new mode and run setup again. Moving to a narrower mode restores the settings that are no longer part of that mode.

## What Setup Configures

| Category | Settings |
|----------|----------|
| **Power** | All modes: disable sleep, hibernate, and display timeout on AC and battery |
| **Network** | Remote/full modes: disable Wi-Fi power saving and adapter sleep |
| **Auto-login** | Full mode: enable `AutoAdminLogon`, set credentials via Sysinternals Autologon |
| **Windows Update** | Full mode: schedule active hours, defer restarts, suppress forced reboots |
| **Lock screen** | Full mode: remove legal notice banners, disable lock on resume |
| **Service** | All modes: install and start the `KeepAliveService` background service |
| **Startup task** | All modes: scheduled task to launch the GUI on user login |

All original settings are backed up before any changes are made. Run **Restore** to undo everything.

## Reliability

- **Locale-independent** - compliance checks and settings backup/restore work on non-English Windows
- **Thread-safe** - all configurator state is thread-local, safe for concurrent operation
- **Cross-process safe** - settings file writes use a named mutex to prevent corruption
- **Credential protection** - DPAPI-encrypted passwords are preserved even when decryption fails on a different user profile
- **Deadlock-free** - all external process stdout/stderr reads use async patterns to avoid pipe buffer deadlocks

## Background Service

The service always runs the keep-awake and compliance workers. The TeamViewer watchdog is only registered in remote/full modes.

| Worker | Interval | Purpose |
|--------|----------|---------|
| **PowerKeepAlive** | Held continuously | Uses `PowerCreateRequest` / `PowerSetRequest` to prevent sleep, with `SetThreadExecutionState` fallback |
| **ProcessWatchdog** | 30 sec | Checks if TeamViewer is running, restarts if not |
| **ComplianceWatchdog** | 6 hrs | Verifies settings haven't drifted, auto-remediates |

## CLI

All operations are also available from the command line:

```
KeepAliveService.exe --setup             Run first-time setup (as Admin)
KeepAliveService.exe --setup --mode keep-awake
KeepAliveService.exe --setup --mode remote
KeepAliveService.exe --setup --mode full
KeepAliveService.exe --check             Verify all settings are compliant
KeepAliveService.exe --update-password   Update the auto-login password
KeepAliveService.exe --restore           Restore original settings (keeps program installed)
KeepAliveService.exe --uninstall         Full uninstall (restore settings + remove program)
KeepAliveService.exe --tray-startup      Launch GUI minimized to system tray
KeepAliveService.exe --help              Show help
```

For backward compatibility, bare `--setup` runs **Full unattended auto-login**. Use `--mode keep-awake` or `--mode remote` when you want a narrower setup from the CLI.

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
- TeamViewer installed (remote/full modes only)

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
