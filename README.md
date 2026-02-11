# Windows Keep Alive

A single C# application that ensures your Windows 11 laptop stays **always on, always logged in, and TeamViewer always running** - even after forced Windows Update restarts.

Built for Windows 11 Pro+ machines used as home servers with the lid closed.

## What It Does

- **Prevents forced update restarts** - Registry policies block Windows from rebooting when you're logged in
- **Auto-login after any restart** - Credentials stored encrypted via Sysinternals Autologon + ARSO (Automatic Restart Sign-On)
- **Prevents sleep/hibernate** - Power settings locked down, lid close set to "Do Nothing"
- **Keeps WiFi alive** - Adapter power management disabled, maximum performance mode
- **Watches TeamViewer** - Restarts TeamViewer if it stops (checks every 30 seconds)
- **Prevents system sleep via API** - `SetThreadExecutionState` continuously prevents Windows from sleeping
- **Self-healing** - Runs as a Windows Service that auto-restarts on failure

## Prerequisites

- Windows 11 Pro, Enterprise, or Education
- [.NET 8 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (or .NET SDK to build from source)
- Administrator privileges (for setup)
- TeamViewer installed

## Quick Start

1. **Build the project:**
   ```
   dotnet publish src/KeepAliveService -c Release -o publish
   ```

2. **Run setup as Administrator:**
   ```
   publish\KeepAliveService.exe --setup
   ```
   This will:
   - Configure Windows Update policy
   - Prompt for your login credentials (Microsoft account email + password)
   - Configure power settings
   - Configure WiFi power management
   - Install and start the KeepAlive service

3. **Restart your PC** to verify auto-login works.

4. **Verify everything:**
   ```
   publish\KeepAliveService.exe --check
   ```

## Commands

| Command | Description |
|---------|-------------|
| `KeepAliveService.exe --setup` | First-time setup (run as Admin) |
| `KeepAliveService.exe --check` | Verify all settings are correct |
| `KeepAliveService.exe --update-password` | Update auto-login password after a password change |
| `KeepAliveService.exe --uninstall` | Remove the service (keeps Windows settings) |
| `KeepAliveService.exe --help` | Show help |

## How It Works

### Setup (`--setup`)
Runs once as Administrator. Configures:
- Windows Update registry policies to prevent forced reboots
- Auto-login via Sysinternals Autologon (encrypts credentials as LSA secrets)
- ARSO (Windows' built-in post-update auto-sign-in)
- Lock screen disabled
- All power timeouts set to Never
- Lid close action set to Do Nothing
- WiFi power saving set to Maximum Performance
- Installs itself as a Windows Service with auto-recovery

### Service (background)
Runs continuously as a Windows Service:
- Calls `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED)` every 5 minutes to prevent sleep
- Checks TeamViewer every 30 seconds, restarts it if not running
- Logs to Windows Event Log (`Event Viewer > Application > KeepAliveService`)
- Auto-restarts on failure (5s, 30s, 60s escalating recovery)

## Troubleshooting

**Auto-login doesn't work after restart:**
- Run `--check` to verify settings
- Ensure you entered the correct Microsoft account **password** (not your PIN)
- Check if Credential Guard is blocking: run `msinfo32` and look for "Credential Guard"
- Check if a Group Policy sets a legal notice that requires acknowledgment

**TeamViewer not restarting:**
- Check Event Viewer for KeepAliveService logs
- Verify TeamViewer is installed in a standard location
- The watchdog has a rate limiter - if TeamViewer fails 5+ times in 10 minutes, it backs off for 5 minutes

**Service not starting:**
- Ensure .NET 8 runtime is installed: `dotnet --list-runtimes`
- Check Event Viewer for error details
- Try starting manually: `sc start KeepAliveService`

**Password changed:**
- Run `KeepAliveService.exe --update-password` as Administrator to update stored credentials

## Uninstall

```
KeepAliveService.exe --uninstall
```

This removes the Windows Service but does **not** revert Windows settings (power, auto-login, update policy). To undo those, change them manually in Windows Settings.
