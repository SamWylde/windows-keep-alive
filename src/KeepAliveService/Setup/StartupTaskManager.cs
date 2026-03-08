using System.Diagnostics;
using KeepAliveService.Update;
using Microsoft.Win32;

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
                ConsoleOutput.Warning($"Executable not found at canonical path: {exePath}");
                ConsoleOutput.Warning("Startup task will be created but may not work until the app is installed.");
            }

            // schtasks /create /tn "WindowsKeepAlive"
            //   /tr "\"C:\Program Files\WindowsKeepAlive\KeepAliveService.exe\" --tray-startup"
            //   /sc onlogon /rl highest /f
            var args = $"/create /tn \"{TaskName}\" " +
                       $"/tr \"\\\"{exePath}\\\" --tray-startup\" " +
                       $"/sc onlogon /rl highest /f";

            if (!RunSchtasks(args, "Startup task created (GUI auto-start at logon)"))
            {
                ConsoleOutput.Error("Failed to create startup scheduled task");
                return false;
            }

            WarnIfUserMismatch();
            return true;
        }
        catch (Exception ex)
        {
            ConsoleOutput.Error($"Startup task creation failed: {ex.Message}");
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
            ConsoleOutput.Warning($"Could not remove startup task: {ex.Message}");
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

    /// <summary>
    /// Validates that the startup task exists AND has the correct action (canonical exe path
    /// with --tray-startup) and runs with highest privileges.
    /// </summary>
    public static (bool exists, bool correct, string? detail) ValidateTask()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/query /tn \"{TaskName}\" /xml",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            var output = process?.StandardOutput.ReadToEnd() ?? "";
            process?.WaitForExit(10_000);

            if (process?.ExitCode != 0)
            {
                return (false, false, "Task not found");
            }

            var expectedExe = InstallManager.CanonicalExePath;
            var hasCorrectAction = output.Contains(expectedExe, StringComparison.OrdinalIgnoreCase)
                                   && output.Contains("--tray-startup", StringComparison.OrdinalIgnoreCase);
            var hasHighestPriv = output.Contains("HighestAvailable", StringComparison.OrdinalIgnoreCase);

            if (!hasCorrectAction)
            {
                return (true, false, $"Task action does not point to {expectedExe} --tray-startup");
            }

            if (!hasHighestPriv)
            {
                return (true, false, "Task is not configured to run with highest privileges");
            }

            return (true, true, null);
        }
        catch (Exception ex)
        {
            return (false, false, ex.Message);
        }
    }

    private static void WarnIfUserMismatch()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon");
            var autologonUser = key?.GetValue("DefaultUserName") as string;
            if (string.IsNullOrWhiteSpace(autologonUser)) return;

            var processUser = Environment.UserName;
            if (!autologonUser.Contains(processUser, StringComparison.OrdinalIgnoreCase) &&
                !processUser.Contains(autologonUser, StringComparison.OrdinalIgnoreCase))
            {
                ConsoleOutput.Warning(
                    $"Startup task was created by '{processUser}' but auto-login user is '{autologonUser}'. " +
                    $"The task may not run under the correct account.");
            }
        }
        catch
        {
            // Best effort only.
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
            if (process == null)
            {
                ConsoleOutput.Error($"{successMessage} - could not start schtasks.exe");
                return false;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit(15_000);
            Task.WaitAll([outputTask, errorTask], 5000);
            var output = outputTask.IsCompletedSuccessfully ? outputTask.Result.Trim() : "";
            var error = errorTask.IsCompletedSuccessfully ? errorTask.Result.Trim() : "";

            if (process.ExitCode == 0)
            {
                ConsoleOutput.Success(successMessage);
                return true;
            }

            var msg = !string.IsNullOrEmpty(error) ? error : output;
            ConsoleOutput.Error($"{successMessage} - schtasks failed: {msg}");
            return false;
        }
        catch (Exception ex)
        {
            ConsoleOutput.Error($"{successMessage} - {ex.Message}");
            return false;
        }
    }

}
