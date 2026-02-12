using System.Diagnostics;
using Microsoft.Win32;

namespace KeepAliveService.Setup;

public static class NetworkConfigurator
{
    private static int _failures;

    public static bool Configure()
    {
        _failures = 0;
        Console.WriteLine();
        Console.WriteLine("=== Network Configuration ===");

        // Set wireless adapter power saving to Maximum Performance
        // GUID 19cbb8fa-... = Wireless Adapter Settings
        // GUID 12bbebe6-... = Power Saving Mode (0 = Maximum Performance)
        RunPowerCfg(
            "/setacvalueindex SCHEME_CURRENT 19cbb8fa-5279-450e-9fac-8a3d5fedd0c1 12bbebe6-58d6-4636-95bb-3217ef867c1a 0",
            "WiFi power saving (AC) -> Maximum Performance");
        RunPowerCfg(
            "/setdcvalueindex SCHEME_CURRENT 19cbb8fa-5279-450e-9fac-8a3d5fedd0c1 12bbebe6-58d6-4636-95bb-3217ef867c1a 0",
            "WiFi power saving (DC) -> Maximum Performance");

        // Disable USB selective suspend
        // GUID 2a737441-... = USB Settings
        // GUID 48e6b7a6-... = USB Selective Suspend Setting (0 = Disabled)
        RunPowerCfg(
            "/setacvalueindex SCHEME_CURRENT 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226 0",
            "USB selective suspend (AC) -> Disabled");
        RunPowerCfg(
            "/setdcvalueindex SCHEME_CURRENT 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226 0",
            "USB selective suspend (DC) -> Disabled");

        // Disable adapter power management via registry (the real fix)
        DisableAdapterPowerManagement();

        return _failures == 0;
    }

    private static void DisableAdapterPowerManagement()
    {
        // Disable "Allow the computer to turn off this device to save power" for all
        // network adapters by setting PnPCapabilities = 0x18 (24) in the registry.
        // This is the same setting as unchecking the box in Device Manager > Power Management.
        try
        {
            var adaptersFound = 0;
            using var networkKey = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}");

            if (networkKey == null)
            {
                WriteWarning("Could not open network adapter registry key");
                return;
            }

            foreach (var subKeyName in networkKey.GetSubKeyNames())
            {
                // Skip non-numeric keys like "Properties"
                if (!int.TryParse(subKeyName, out _))
                    continue;

                using var adapterKey = networkKey.OpenSubKey(subKeyName, writable: true);
                if (adapterKey == null) continue;

                var driverDesc = adapterKey.GetValue("DriverDesc") as string ?? "";
                var componentId = adapterKey.GetValue("ComponentId") as string ?? "";

                var isVirtual = driverDesc.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                                driverDesc.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase) ||
                                driverDesc.Contains("WAN Miniport", StringComparison.OrdinalIgnoreCase) ||
                                driverDesc.Contains("TAP-", StringComparison.OrdinalIgnoreCase) ||
                                driverDesc.Contains("Kernel Debug", StringComparison.OrdinalIgnoreCase) ||
                                driverDesc.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) ||
                                componentId.Contains("vwifimp", StringComparison.OrdinalIgnoreCase) ||
                                componentId.Contains("loopback", StringComparison.OrdinalIgnoreCase) ||
                                componentId.Contains("tunnel", StringComparison.OrdinalIgnoreCase) ||
                                componentId.Contains("ms_", StringComparison.OrdinalIgnoreCase);

                if (isVirtual) continue;

                adaptersFound++;

                // PnPCapabilities: 0x18 (24) = disable power management
                // 0x10 = PDCAP_D1_SUPPORTED (don't allow D1 state)
                // 0x08 = PDCAP_D2_SUPPORTED (don't allow D2 state)
                // Combined = disable OS power management for this device
                adapterKey.SetValue("PnPCapabilities", 24, RegistryValueKind.DWord);
                WriteSuccess($"Disabled power management for: {driverDesc}");
            }

            if (adaptersFound == 0)
            {
                WriteWarning("No eligible physical network adapters found in registry");
            }
        }
        catch (UnauthorizedAccessException)
        {
            WriteError("Cannot modify adapter power management - access denied (run as Administrator)");
            _failures++;
        }
        catch (Exception ex)
        {
            WriteWarning($"Adapter power management: {ex.Message}");
        }
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
                _failures++;
            }
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
