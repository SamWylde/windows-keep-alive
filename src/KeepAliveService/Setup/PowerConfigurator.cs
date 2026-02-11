using System.Diagnostics;

namespace KeepAliveService.Setup;

public static class PowerConfigurator
{
    public static void Configure()
    {
        Console.WriteLine();
        Console.WriteLine("=== Power Settings ===");

        // Disable sleep timeouts (AC and DC)
        RunPowerCfg("/change standby-timeout-ac 0", "Sleep timeout (AC) → Never");
        RunPowerCfg("/change standby-timeout-dc 0", "Sleep timeout (DC) → Never");

        // Disable hibernate timeouts
        RunPowerCfg("/change hibernate-timeout-ac 0", "Hibernate timeout (AC) → Never");
        RunPowerCfg("/change hibernate-timeout-dc 0", "Hibernate timeout (DC) → Never");

        // Disable monitor timeout
        RunPowerCfg("/change monitor-timeout-ac 0", "Monitor timeout (AC) → Never");
        RunPowerCfg("/change monitor-timeout-dc 0", "Monitor timeout (DC) → Never");

        // Disable hibernation entirely
        RunPowerCfg("/hibernate off", "Hibernation → Disabled");

        // Lid close action = Do Nothing (0)
        RunPowerCfg("/setacvalueindex SCHEME_CURRENT SUB_BUTTONS LIDACTION 0", "Lid close on AC → Do Nothing");
        RunPowerCfg("/setdcvalueindex SCHEME_CURRENT SUB_BUTTONS LIDACTION 0", "Lid close on DC → Do Nothing");

        // Disable hybrid sleep
        RunPowerCfg("/setacvalueindex SCHEME_CURRENT SUB_SLEEP HYBRIDSLEEP 0", "Hybrid sleep (AC) → Disabled");
        RunPowerCfg("/setdcvalueindex SCHEME_CURRENT SUB_SLEEP HYBRIDSLEEP 0", "Hybrid sleep (DC) → Disabled");

        // Disable console lock on wake (require sign-in after sleep)
        RunPowerCfg("/setacvalueindex SCHEME_CURRENT SUB_NONE CONSOLELOCK 0", "Sign-in after sleep (AC) → Disabled");
        RunPowerCfg("/setdcvalueindex SCHEME_CURRENT SUB_NONE CONSOLELOCK 0", "Sign-in after sleep (DC) → Disabled");

        // Apply all changes
        RunPowerCfg("/setactive SCHEME_CURRENT", "Applied power scheme changes");
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
