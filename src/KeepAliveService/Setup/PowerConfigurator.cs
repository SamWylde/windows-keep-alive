using System.Diagnostics;

namespace KeepAliveService.Setup;

public static class PowerConfigurator
{
    // Power setting GUIDs — used by both setup and compliance checker.
    private const string SubButtons = "4f971e89-eebd-4455-a8de-9e59040e7347";
    private const string LidAction = "5ca83367-6e45-459f-a27b-476b1d01c936";
    private const string SubSleep = "238c9fa8-0aad-41ed-83f4-97be242c8f20";
    private const string HybridSleep = "94ac6d29-73ce-41a6-809f-6363ba21b47e";
    private const string SubNone = "fea3413e-7e05-4911-9a71-700331f1c294";
    private const string ConsoleLock = "0e796bdb-100d-47d6-a2d5-f7d2daa51f51";

    private static int _failures;

    public static bool Configure()
    {
        _failures = 0;
        Console.WriteLine();
        Console.WriteLine("=== Power Settings ===");

        // Disable sleep timeouts (AC and DC)
        RunPowerCfg("/change standby-timeout-ac 0", "Sleep timeout (AC) -> Never");
        RunPowerCfg("/change standby-timeout-dc 0", "Sleep timeout (DC) -> Never");

        // Disable hibernate timeouts
        RunPowerCfg("/change hibernate-timeout-ac 0", "Hibernate timeout (AC) -> Never");
        RunPowerCfg("/change hibernate-timeout-dc 0", "Hibernate timeout (DC) -> Never");

        // Disable monitor timeout
        RunPowerCfg("/change monitor-timeout-ac 0", "Monitor timeout (AC) -> Never");
        RunPowerCfg("/change monitor-timeout-dc 0", "Monitor timeout (DC) -> Never");

        // Disable hibernation entirely
        RunPowerCfg("/hibernate off", "Hibernation -> Disabled");

        // Lid close action = Do Nothing (0) — use GUIDs, not aliases, for reliability
        RunPowerCfg($"/setacvalueindex SCHEME_CURRENT {SubButtons} {LidAction} 0", "Lid close on AC -> Do Nothing");
        RunPowerCfg($"/setdcvalueindex SCHEME_CURRENT {SubButtons} {LidAction} 0", "Lid close on DC -> Do Nothing");

        // Disable hybrid sleep
        RunPowerCfg($"/setacvalueindex SCHEME_CURRENT {SubSleep} {HybridSleep} 0", "Hybrid sleep (AC) -> Disabled");
        RunPowerCfg($"/setdcvalueindex SCHEME_CURRENT {SubSleep} {HybridSleep} 0", "Hybrid sleep (DC) -> Disabled");

        // Disable console lock on wake (require sign-in after sleep)
        RunPowerCfg($"/setacvalueindex SCHEME_CURRENT {SubNone} {ConsoleLock} 0", "Sign-in after sleep (AC) -> Disabled");
        RunPowerCfg($"/setdcvalueindex SCHEME_CURRENT {SubNone} {ConsoleLock} 0", "Sign-in after sleep (DC) -> Disabled");

        // Flush all index changes to the kernel by re-activating the current scheme.
        // Without this, /setacvalueindex and /setdcvalueindex only write to the
        // scheme's backing store but do not take effect.
        RunPowerCfg("/setactive SCHEME_CURRENT", "Activate power scheme to apply changes");

        // Verify lid close was actually applied (the most critical setting).
        VerifyLidCloseSetting();

        return _failures == 0;
    }

    private static void VerifyLidCloseSetting()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powercfg.exe",
                Arguments = $"/query SCHEME_CURRENT {SubButtons} {LidAction}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                WriteWarning("Could not verify lid close action: failed to start powercfg");
                return;
            }

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit(10_000);

            if (process.ExitCode != 0)
            {
                WriteWarning($"Could not verify lid close action: powercfg /query returned exit code {process.ExitCode}: {error.Trim()}");
                return;
            }

            var (acHex, dcHex) = Helpers.ParsePowerCfgSettingValues(output);

            if (acHex == null && dcHex == null)
            {
                // Setting not reported — likely a desktop or VM without a lid sensor.
                WriteWarning("Lid close action: setting not reported by powercfg (no lid sensor detected, skipping verification)");
                return;
            }

            const string expected = "0x00000000";
            var acOk = acHex == null || acHex.Equals(expected, StringComparison.OrdinalIgnoreCase);
            var dcOk = dcHex == null || dcHex.Equals(expected, StringComparison.OrdinalIgnoreCase);

            if (acOk && dcOk)
            {
                var detail = (acHex != null && dcHex != null) ? "AC and DC"
                    : acHex != null ? "AC only (DC not reported)"
                    : "DC only (AC not reported)";
                WriteSuccess($"Lid close action verified: Do Nothing ({detail})");
            }
            else
            {
                WriteError($"Lid close action did NOT apply correctly (AC={acHex ?? "not reported"}, DC={dcHex ?? "not reported"}, expected {expected})");
                _failures++;
            }
        }
        catch (Exception ex)
        {
            WriteWarning($"Could not verify lid close action: {ex.Message}");
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
                var error = process?.StandardError.ReadToEnd()?.Trim();
                WriteWarning($"{description} - powercfg returned exit code {process?.ExitCode}: {error}");
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
