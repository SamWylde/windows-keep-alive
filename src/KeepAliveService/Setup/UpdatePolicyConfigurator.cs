using System.Diagnostics;
using Microsoft.Win32;

namespace KeepAliveService.Setup;

public static class UpdatePolicyConfigurator
{
    private const string AuPolicyPath = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU";
    private const string UxSettingsPath = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";

    public static void Configure()
    {
        Console.WriteLine();
        Console.WriteLine("=== Windows Update Policy ===");

        ConfigureRegistrySettings();
        DisableRebootScheduledTask();
    }

    private static void ConfigureRegistrySettings()
    {
        // Prevent auto-reboot when users are logged in
        SetRegistryDword(Registry.LocalMachine, AuPolicyPath,
            "NoAutoRebootWithLoggedOnUsers", 1,
            "No auto-reboot with logged-on users");

        // Auto-download and schedule the install (gives user control over install timing)
        SetRegistryDword(Registry.LocalMachine, AuPolicyPath,
            "AUOptions", 4,
            "Auto-download, schedule install");

        // Set Active Hours to maximum 18-hour range (midnight to 6 PM)
        SetRegistryDword(Registry.LocalMachine, UxSettingsPath,
            "ActiveHoursStart", 0,
            "Active hours start -> Midnight");

        SetRegistryDword(Registry.LocalMachine, UxSettingsPath,
            "ActiveHoursEnd", 18,
            "Active hours end -> 6 PM");

        SetRegistryDword(Registry.LocalMachine, UxSettingsPath,
            "IsActiveHoursEnabled", 1,
            "Active hours -> Enabled");
    }

    private static void DisableRebootScheduledTask()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = @"/Change /TN ""\Microsoft\Windows\UpdateOrchestrator\Reboot"" /Disable",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            process?.WaitForExit(10_000);

            if (process?.ExitCode == 0)
            {
                WriteSuccess("UpdateOrchestrator\\Reboot task -> Disabled");
            }
            else
            {
                // Task may not exist on all Windows builds
                WriteWarning("UpdateOrchestrator\\Reboot task not found or could not be disabled (may not exist on this build)");
            }
        }
        catch (Exception ex)
        {
            WriteWarning($"Could not disable reboot task: {ex.Message}");
        }
    }

    private static void SetRegistryDword(RegistryKey root, string path, string name, int value, string description)
    {
        try
        {
            using var key = root.CreateSubKey(path, writable: true);
            if (key == null)
            {
                WriteError($"{description} - Could not create/open registry key");
                return;
            }

            key.SetValue(name, value, RegistryValueKind.DWord);
            WriteSuccess(description);
        }
        catch (UnauthorizedAccessException)
        {
            WriteError($"{description} - Access denied (run as Administrator)");
        }
        catch (Exception ex)
        {
            WriteError($"{description} - {ex.Message}");
        }
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
