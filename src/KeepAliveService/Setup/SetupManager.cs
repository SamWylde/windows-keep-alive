using System.Diagnostics;
using System.Security.Principal;
using System.ServiceProcess;
using Microsoft.Win32;

namespace KeepAliveService.Setup;

public static class SetupManager
{
    private static int _criticalFailures;

    public static void RunSetup()
    {
        _criticalFailures = 0;

        Console.WriteLine();
        Console.WriteLine("========================================");
        Console.WriteLine("  Windows Keep Alive - Setup");
        Console.WriteLine("========================================");

        // Step 0: Preflight checks
        if (!RunPreflightChecks())
        {
            Environment.Exit(1);
            return;
        }

        // Step 1: Windows Update policy
        if (!TryRun("Windows Update policy", UpdatePolicyConfigurator.Configure))
            _criticalFailures++;

        // Step 2: Auto-login + ARSO + lock screen
        if (!TryRun("Auto-login configuration", AutoLogonConfigurator.Configure))
            _criticalFailures++;

        // Step 3: Power settings
        if (!TryRun("Power settings", PowerConfigurator.Configure))
            _criticalFailures++;

        // Step 4: Network / WiFi
        // Non-critical: WiFi config failure shouldn't block setup
        TryRun("Network configuration", NetworkConfigurator.Configure);

        // Step 5: Install self as service
        if (!TryRun("Service installation", ServiceInstaller.Install))
            _criticalFailures++;

        // Summary - conditional on success/failure
        PrintSummary();

        if (_criticalFailures > 0)
        {
            Environment.Exit(1);
        }
    }

    private static bool TryRun(string stepName, Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            WriteError($"{stepName} failed: {ex.Message}");
            return false;
        }
    }

    private static bool RunPreflightChecks()
    {
        Console.WriteLine();
        Console.WriteLine("=== Preflight Checks ===");

        // Check Administrator
        if (!IsRunningAsAdmin())
        {
            WriteError("Not running as Administrator. Please run this program as Administrator.");
            Console.WriteLine();
            Console.WriteLine("  Right-click the executable and select 'Run as administrator', or");
            Console.WriteLine("  open an elevated command prompt and run the command from there.");

            // Attempt to self-elevate
            Console.WriteLine();
            Console.Write("  Would you like to restart as Administrator? (y/n): ");
            var response = Console.ReadLine()?.Trim().ToLowerInvariant();

            if (response == "y" || response == "yes")
            {
                TrySelfElevate();
            }

            return false;
        }
        WriteSuccess("Running as Administrator");

        // Check Windows edition
        if (!CheckWindowsEdition())
        {
            return false;
        }

        // Check TeamViewer is installed
        if (!CheckTeamViewerInstalled())
        {
            return false;
        }

        // Check for auto-login blockers
        CheckBlockers();

        // Check for Credential Guard
        CheckCredentialGuard();

        return true;
    }

    private static bool IsRunningAsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void TrySelfElevate()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (exePath == null) return;

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--setup",
                Verb = "runas",
                UseShellExecute = true,
            };

            Process.Start(psi);
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            WriteError($"Could not self-elevate: {ex.Message}");
        }
    }

    private static bool CheckWindowsEdition()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var edition = key?.GetValue("EditionID") as string ?? "Unknown";

            if (edition.Contains("Pro", StringComparison.OrdinalIgnoreCase) ||
                edition.Contains("Enterprise", StringComparison.OrdinalIgnoreCase) ||
                edition.Contains("Education", StringComparison.OrdinalIgnoreCase))
            {
                WriteSuccess($"Windows Edition: {edition}");
                return true;
            }
            else
            {
                WriteError($"Windows Edition: {edition} - This tool requires Pro, Enterprise, or Education");
                return false;
            }
        }
        catch
        {
            WriteWarning("Could not determine Windows edition");
            return true; // Continue anyway
        }
    }

    private static bool CheckTeamViewerInstalled()
    {
        // Check if TeamViewer service exists
        try
        {
            using var sc = new ServiceController("TeamViewer");
            _ = sc.Status;
            WriteSuccess($"TeamViewer service found (status: {sc.Status})");
            return true;
        }
        catch (InvalidOperationException)
        {
            // Service not installed - check for executable
        }

        // Check for TeamViewer process
        var procs = Process.GetProcessesByName("TeamViewer_Service");
        if (procs.Length > 0)
        {
            foreach (var p in procs) p.Dispose();
            WriteSuccess("TeamViewer process found running");
            return true;
        }

        var uiProcs = Process.GetProcessesByName("TeamViewer");
        if (uiProcs.Length > 0)
        {
            foreach (var p in uiProcs) p.Dispose();
            WriteSuccess("TeamViewer process found running");
            return true;
        }

        // Check for TeamViewer executable in common locations
        string[] commonPaths =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "TeamViewer", "TeamViewer.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "TeamViewer", "TeamViewer.exe"),
        ];

        foreach (var path in commonPaths)
        {
            if (File.Exists(path))
            {
                WriteSuccess($"TeamViewer found at: {path}");
                return true;
            }
        }

        // Check registry
        string[] registryPaths = [@"SOFTWARE\TeamViewer", @"SOFTWARE\WOW6432Node\TeamViewer"];
        foreach (var regPath in registryPaths)
        {
            using var key = Registry.LocalMachine.OpenSubKey(regPath);
            if (key?.GetValue("InstallationDirectory") is string installDir)
            {
                var fullPath = Path.Combine(installDir, "TeamViewer.exe");
                if (File.Exists(fullPath))
                {
                    WriteSuccess($"TeamViewer found at: {fullPath}");
                    return true;
                }
            }
        }

        WriteError("TeamViewer is not installed. Install TeamViewer before running setup.");
        Console.WriteLine("    Download from: https://www.teamviewer.com/en/download/");
        Console.WriteLine("    The watchdog cannot guarantee TeamViewer availability without it.");
        return false;
    }

    private static void CheckBlockers()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");

            // Legal notice blocks auto-login
            var legalNotice = key?.GetValue("LegalNoticeText") as string;
            var legalCaption = key?.GetValue("LegalNoticeCaption") as string;

            if (!string.IsNullOrWhiteSpace(legalNotice) || !string.IsNullOrWhiteSpace(legalCaption))
            {
                WriteError("BLOCKER: LegalNoticeText/LegalNoticeCaption is set.");
                Console.WriteLine("    This forces a dialog box at login that requires manual acknowledgment.");
                Console.WriteLine("    Auto-login will NOT work until this is removed.");
                Console.WriteLine("    This is typically set by enterprise Group Policy.");
                Console.WriteLine("    Registry: HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System");
                _criticalFailures++;
            }
            else
            {
                WriteSuccess("No legal notice blocker");
            }

            // DontDisplayLastUserName can interfere
            var dontDisplay = key?.GetValue("DontDisplayLastUserName");
            if (dontDisplay is int d && d == 1)
            {
                WriteWarning("DontDisplayLastUserName = 1 (may interfere with auto-login, typically set by enterprise policy)");
            }
        }
        catch
        {
            // Key doesn't exist - no blockers
            WriteSuccess("No login policy blockers detected");
        }
    }

    private static void CheckCredentialGuard()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\DeviceGuard");
            var vbs = key?.GetValue("EnableVirtualizationBasedSecurity");

            if (vbs is int vbsVal && vbsVal != 0)
            {
                // VBS is enabled, check if Credential Guard is specifically running
                using var lsaKey = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Lsa");
                var lsaCfg = lsaKey?.GetValue("LsaCfgFlags");

                if (lsaCfg is int lsaVal && lsaVal != 0)
                {
                    WriteWarning("Credential Guard appears to be enabled.");
                    Console.WriteLine("    Sysinternals Autologon may fail to store credentials.");
                    Console.WriteLine("    If auto-login doesn't work after setup, you may need to disable Credential Guard:");
                    Console.WriteLine("    1. Run gpedit.msc");
                    Console.WriteLine("    2. Computer Config > Admin Templates > System > Device Guard");
                    Console.WriteLine("    3. Set 'Turn On Virtualization Based Security' to Disabled");
                    Console.WriteLine("    4. Reboot");
                }
                else
                {
                    WriteSuccess("VBS enabled but Credential Guard not configured");
                }
            }
            else
            {
                WriteSuccess("Credential Guard: Not enabled");
            }
        }
        catch
        {
            WriteSuccess("Credential Guard: Not detected");
        }
    }

    private static void PrintSummary()
    {
        Console.WriteLine();
        Console.WriteLine("========================================");

        if (_criticalFailures > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  Setup completed with {_criticalFailures} CRITICAL FAILURE(S)");
            Console.ResetColor();
            Console.WriteLine("========================================");
            Console.WriteLine();
            Console.WriteLine("  Some steps failed. The system may NOT be fully configured.");
            Console.WriteLine("  Review the [FAIL] messages above and fix the issues,");
            Console.WriteLine("  then re-run: KeepAliveService.exe --setup");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  Setup Complete!");
            Console.ResetColor();
            Console.WriteLine("========================================");
            Console.WriteLine();
            Console.WriteLine("  What was configured:");
            Console.WriteLine("    - Windows Update: auto-restart blocked when logged in");
            Console.WriteLine("    - Auto-login: credentials stored encrypted via Autologon");
            Console.WriteLine("    - ARSO: Windows will auto-sign-in after update reboots");
            Console.WriteLine("    - Lock screen: disabled");
            Console.WriteLine("    - Power: sleep/hibernate/lid-close all set to never/do nothing");
            Console.WriteLine("    - WiFi: power saving set to Maximum Performance");
            Console.WriteLine("    - KeepAlive service: installed, running, auto-start");
            Console.WriteLine();
            Console.WriteLine("  The KeepAlive service is now:");
            Console.WriteLine("    - Preventing system sleep via SetThreadExecutionState API");
            Console.WriteLine("    - Watching TeamViewer and restarting it if it stops");
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  IMPORTANT: Restart your PC to verify auto-login works!");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine("  Useful commands:");
        Console.WriteLine("    KeepAliveService.exe --check           Verify all settings");
        Console.WriteLine("    KeepAliveService.exe --update-password  Update auto-login password");
        Console.WriteLine("    KeepAliveService.exe --uninstall        Remove the service");
        Console.WriteLine();
    }

    private static void WriteSuccess(string message)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("  [OK] ");
        Console.ResetColor();
        Console.WriteLine(message);
    }

    private static void WriteWarning(string message)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write("  [WARN] ");
        Console.ResetColor();
        Console.WriteLine(message);
    }

    private static void WriteError(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Write("  [FAIL] ");
        Console.ResetColor();
        Console.WriteLine(message);
    }
}
