using Microsoft.Win32;

namespace KeepAliveService.Setup;

public static class UpdatePolicyConfigurator
{
    private const string AuPolicyPath = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU";
    private const string UxSettingsPath = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";

    private static int _failures;

    public static bool Configure()
    {
        _failures = 0;

        Console.WriteLine();
        Console.WriteLine("=== Windows Update Policy ===");

        WarnIfPolicyMayBeIgnoredOnHome();

        ConfigureRegistrySettings();

        return _failures == 0;
    }

    private static void WarnIfPolicyMayBeIgnoredOnHome()
    {
        if (!WindowsEditionHelper.TryGetWindowsEditionInfo(out var info, out _)
            || info?.IsHomeOrCore != true)
        {
            return;
        }

        WriteWarning("Windows Home/Core detected: some Windows Update policy values under HKLM\\SOFTWARE\\Policies may not be fully enforced on Windows 10/11.");
        WriteWarning("Behavior will still depend on the OS update stack. Use compliance checks after reboot to validate runtime behavior.");
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

    private static void SetRegistryDword(RegistryKey root, string path, string name, int value, string description)
    {
        try
        {
            using var key = root.CreateSubKey(path, writable: true);
            if (key == null)
            {
                WriteError($"{description} - Could not create/open registry key");
                _failures++;
                return;
            }

            key.SetValue(name, value, RegistryValueKind.DWord);
            WriteSuccess(description);
        }
        catch (UnauthorizedAccessException)
        {
            WriteError($"{description} - Access denied (run as Administrator)");
            _failures++;
        }
        catch (Exception ex)
        {
            WriteError($"{description} - {ex.Message}");
            _failures++;
        }
    }

    private static void WriteSuccess(string message)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("  [OK] ");
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

    private static void WriteWarning(string message)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write("  [WARN] ");
        Console.ResetColor();
        Console.WriteLine(message);
    }
}
