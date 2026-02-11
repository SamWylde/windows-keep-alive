using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

namespace KeepAliveService.Setup;

public static class SetupManager
{
    public static void RunSetup()
    {
        Console.WriteLine();
        Console.WriteLine("========================================");
        Console.WriteLine("  Windows Keep Alive - Setup");
        Console.WriteLine("========================================");

        // Step 0: Preflight checks
        if (!RunPreflightChecks())
        {
            return;
        }

        // Step 1: Windows Update policy
        try { UpdatePolicyConfigurator.Configure(); }
        catch (Exception ex) { WriteError($"Update policy configuration failed: {ex.Message}"); }

        // Step 2: Auto-login + ARSO + lock screen
        try { AutoLogonConfigurator.Configure(); }
        catch (Exception ex) { WriteError($"Auto-login configuration failed: {ex.Message}"); }

        // Step 3: Power settings
        try { PowerConfigurator.Configure(); }
        catch (Exception ex) { WriteError($"Power configuration failed: {ex.Message}"); }

        // Step 4: Network / WiFi
        try { NetworkConfigurator.Configure(); }
        catch (Exception ex) { WriteError($"Network configuration failed: {ex.Message}"); }

        // Step 5: Install self as service
        try { ServiceInstaller.Install(); }
        catch (Exception ex) { WriteError($"Service installation failed: {ex.Message}"); }

        // Summary
        PrintSummary();
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
        Console.WriteLine("  Setup Complete!");
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
