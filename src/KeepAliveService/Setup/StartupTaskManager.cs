using System.Diagnostics;
using KeepAliveService.Update;

namespace KeepAliveService.Setup;

public static class StartupTaskManager
{
    private const string TaskName = "WindowsKeepAlive";

    public static bool EnsureTask()
    {
        Console.WriteLine();
        Console.WriteLine("=== Startup Task ===");

        try
        {
            var exePath = InstallManager.CanonicalExePath;
            if (!File.Exists(exePath))
            {
                WriteWarning($"Executable not found at canonical path: {exePath}");
                WriteWarning("Startup task will be created but may not work until the app is installed.");
            }

            // schtasks /create /tn "WindowsKeepAlive"
            //   /tr "\"C:\Program Files\WindowsKeepAlive\KeepAliveService.exe\" --tray-startup"
            //   /sc onlogon /rl highest /f
            var args = $"/create /tn \"{TaskName}\" " +
                       $"/tr \"\\\"{exePath}\\\" --tray-startup\" " +
                       $"/sc onlogon /rl highest /f";

            if (!RunSchtasks(args, "Startup task created (GUI auto-start at logon)"))
            {
                WriteError("Failed to create startup scheduled task");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            WriteError($"Startup task creation failed: {ex.Message}");
            return false;
        }
    }

    public static bool RemoveTask()
    {
        try
        {
            if (!IsTaskPresent())
            {
                return true;
            }

            return RunSchtasks($"/delete /tn \"{TaskName}\" /f", "Startup task removed");
        }
        catch (Exception ex)
        {
            WriteWarning($"Could not remove startup task: {ex.Message}");
            return false;
        }
    }

    public static bool IsTaskPresent()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/query /tn \"{TaskName}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            process?.WaitForExit(10_000);
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool RunSchtasks(string arguments, string successMessage)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            var output = process?.StandardOutput.ReadToEnd()?.Trim() ?? "";
            var error = process?.StandardError.ReadToEnd()?.Trim() ?? "";
            process?.WaitForExit(15_000);

            if (process?.ExitCode == 0)
            {
                WriteSuccess(successMessage);
                return true;
            }

            var msg = !string.IsNullOrEmpty(error) ? error : output;
            WriteError($"{successMessage} - schtasks failed: {msg}");
            return false;
        }
        catch (Exception ex)
        {
            WriteError($"{successMessage} - {ex.Message}");
            return false;
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
