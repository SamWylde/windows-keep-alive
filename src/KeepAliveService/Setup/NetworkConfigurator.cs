using System.Diagnostics;

namespace KeepAliveService.Setup;

public static class NetworkConfigurator
{
    public static void Configure()
    {
        Console.WriteLine();
        Console.WriteLine("=== Network / WiFi Configuration ===");

        // Set wireless adapter power saving to Maximum Performance
        // GUID 19cbb8fa-... = Wireless Adapter Settings
        // GUID 12bbebe6-... = Power Saving Mode (0 = Maximum Performance)
        RunPowerCfg(
            "/setacvalueindex SCHEME_CURRENT 19cbb8fa-5279-450e-9fac-8a3d5fedd0c1 12bbebe6-58d6-4636-95bb-3217ef867c1a 0",
            "WiFi power saving (AC) → Maximum Performance");
        RunPowerCfg(
            "/setdcvalueindex SCHEME_CURRENT 19cbb8fa-5279-450e-9fac-8a3d5fedd0c1 12bbebe6-58d6-4636-95bb-3217ef867c1a 0",
            "WiFi power saving (DC) → Maximum Performance");

        // Disable USB selective suspend
        // GUID 2a737441-... = USB Settings
        // GUID 48e6b7a6-... = USB Selective Suspend Setting (0 = Disabled)
        RunPowerCfg(
            "/setacvalueindex SCHEME_CURRENT 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226 0",
            "USB selective suspend (AC) → Disabled");
        RunPowerCfg(
            "/setdcvalueindex SCHEME_CURRENT 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226 0",
            "USB selective suspend (DC) → Disabled");

        // Apply changes
        RunPowerCfg("/setactive SCHEME_CURRENT", "Applied network power settings");

        // Try to disable WiFi adapter power management via netsh
        DisableWifiPowerManagement();
    }

    private static void DisableWifiPowerManagement()
    {
        // Attempt to find the WiFi interface name and disable power save
        try
        {
            var interfaceName = GetWifiInterfaceName();
            if (interfaceName == null)
            {
                WriteWarning("No WiFi interface detected (may be using Ethernet only)");
                return;
            }

            // Use netsh to set power management - this sets the adapter-level power save
            var psi = new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = $"wlan set autoconfig enabled=yes interface=\"{interfaceName}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            process?.WaitForExit(10_000);

            WriteSuccess($"WiFi auto-connect enabled for interface: {interfaceName}");
        }
        catch (Exception ex)
        {
            WriteWarning($"WiFi power management config: {ex.Message}");
        }
    }

    private static string? GetWifiInterfaceName()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = "wlan show interfaces",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            var output = process?.StandardOutput.ReadToEnd() ?? "";
            process?.WaitForExit(10_000);

            // Parse output for "Name : <interface name>"
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("Name", StringComparison.OrdinalIgnoreCase) && trimmed.Contains(':'))
                {
                    var name = trimmed.Substring(trimmed.IndexOf(':') + 1).Trim();
                    if (!string.IsNullOrEmpty(name))
                        return name;
                }
            }
        }
        catch
        {
            // netsh may not be available or WLAN service not running
        }

        return null;
    }

    private static void RunPowerCfg(string arguments, string description)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powercfg.exe",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            process?.WaitForExit(10_000);

            if (process?.ExitCode == 0)
            {
                WriteSuccess(description);
            }
            else
            {
                WriteWarning($"{description} - powercfg returned exit code {process?.ExitCode}");
            }
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
