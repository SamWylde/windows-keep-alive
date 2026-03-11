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

            var taskUser = GetAutoLogonUser() ?? Environment.UserName;

            // Use PowerShell Register-ScheduledTask to create a user-specific
            // logon trigger with Interactive logon type (no password required).
            // schtasks.exe cannot create user-specific logon triggers without
            // prompting for the password.
            //
            // Note: -Execute takes the raw path without extra quotes; Task Scheduler
            // handles the path internally. Embedding quotes would corrupt the action.
            var psScript =
                $"$action = New-ScheduledTaskAction -Execute '{EscapePsString(exePath)}' -Argument '--tray-startup'; " +
                $"$trigger = New-ScheduledTaskTrigger -AtLogOn -User '{EscapePsString(taskUser)}'; " +
                $"$principal = New-ScheduledTaskPrincipal -UserId '{EscapePsString(taskUser)}' -RunLevel Highest -LogonType Interactive; " +
                $"$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries; " +
                $"Register-ScheduledTask -TaskName '{TaskName}' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force";

            if (!RunPowerShell(psScript, $"Startup task created for user '{taskUser}' (GUI auto-start at logon)"))
            {
                ConsoleOutput.Error("Failed to create startup scheduled task");
                return false;
            }

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
    /// with --tray-startup), runs with highest privileges, and is bound to the expected user.
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

            // Verify the task principal matches the auto-logon user.
            var expectedUser = GetAutoLogonUser();
            if (!string.IsNullOrWhiteSpace(expectedUser) &&
                !output.Contains(expectedUser, StringComparison.OrdinalIgnoreCase))
            {
                return (true, false, $"Task is not bound to auto-logon user '{expectedUser}'");
            }

            return (true, true, null);
        }
        catch (Exception ex)
        {
            return (false, false, ex.Message);
        }
    }

    /// <summary>
    /// Reads the configured auto-logon user from the Winlogon registry key.
    /// Returns DOMAIN\User (e.g. "MicrosoftAccount\user@outlook.com",
    /// "MACHINENAME\localuser", or "DOMAIN\domainuser").
    /// </summary>
    internal static string? GetAutoLogonUser()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon");
            if (key == null) return null;

            var username = key.GetValue("DefaultUserName") as string;
            if (string.IsNullOrWhiteSpace(username)) return null;

            // If username already contains a qualifier, use as-is.
            if (username.Contains('\\'))
                return username;

            // Prepend DefaultDomainName (covers MicrosoftAccount, machine name,
            // and AD domain). For UPN-style Microsoft accounts like
            // user@outlook.com, the domain is "MicrosoftAccount".
            var domain = key.GetValue("DefaultDomainName") as string;
            if (!string.IsNullOrWhiteSpace(domain))
                return $"{domain}\\{username}";

            return username;
        }
        catch
        {
            return null;
        }
    }

    private static string EscapePsString(string value)
    {
        return value.Replace("'", "''");
    }

    private static bool RunPowerShell(string script, string successMessage)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                ConsoleOutput.Error($"{successMessage} - could not start powershell.exe");
                return false;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit(30_000);
            Task.WaitAll([outputTask, errorTask], 5000);
            var output = outputTask.IsCompletedSuccessfully ? outputTask.Result.Trim() : "";
            var error = errorTask.IsCompletedSuccessfully ? errorTask.Result.Trim() : "";

            if (process.ExitCode == 0)
            {
                ConsoleOutput.Success(successMessage);
                return true;
            }

            var msg = !string.IsNullOrEmpty(error) ? error : output;
            ConsoleOutput.Error($"{successMessage} - PowerShell failed: {msg}");
            return false;
        }
        catch (Exception ex)
        {
            ConsoleOutput.Error($"{successMessage} - {ex.Message}");
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
