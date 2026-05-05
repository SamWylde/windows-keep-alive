using System.Diagnostics;
using System.ServiceProcess;

namespace KeepAliveService.Setup;

public static class ServiceInstaller
{
    private const string ServiceName = "KeepAliveService";
    private const string DisplayName = "Keep Alive Watchdog";
    private const string Description = "Prevents system sleep and runs configured watchdogs. Part of windows-keep-alive.";

    public static bool Install()
    {
        Console.WriteLine();
        Console.WriteLine("=== Service Installation ===");

        var exePath = GetServiceExePath();
        if (exePath == null)
        {
            ConsoleOutput.Error("Could not determine service executable path");
            return false;
        }

        ConsoleOutput.Info($"Service executable: {exePath}");

        // Stop and remove existing service if present
        RemoveExistingService();

        // Create the service
        if (!RunSc($"create {ServiceName} binPath= \"\\\"{exePath}\\\"\" start= auto DisplayName= \"{DisplayName}\"",
                "Service created"))
        {
            ConsoleOutput.Error("Failed to create service. Ensure you are running as Administrator.");
            return false;
        }

        // Set description
        RunSc($"description {ServiceName} \"{Description}\"", "Service description set");

        // Configure failure recovery: restart after 5s, 30s, 60s. Reset counter after 24 hours.
        RunSc($"failure {ServiceName} reset= 86400 actions= restart/5000/restart/30000/restart/60000",
            "Failure recovery -> Restart after 5s/30s/60s");

        // Enable failure actions even on non-crash exits
        RunSc($"failureflag {ServiceName} 1",
            "Failure flag -> Enabled (recover from all failures)");

        // Start the service
        try
        {
            using var sc = new ServiceController(ServiceName);
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
            ConsoleOutput.Success($"Service started and running");
            return true;
        }
        catch (Exception ex)
        {
            ConsoleOutput.Error($"Failed to start service: {ex.Message}");
            Console.WriteLine("  Try starting it manually: sc start KeepAliveService");
            return false;
        }
    }

    public static bool Uninstall()
    {
        Console.WriteLine();
        Console.WriteLine("=== Service Uninstall ===");

        // Stop the service
        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status == ServiceControllerStatus.Running)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                ConsoleOutput.Success("Service stopped");
            }
        }
        catch (InvalidOperationException)
        {
            ConsoleOutput.Info("Service not found (may already be uninstalled)");
            return true; // Already gone
        }
        catch (Exception ex)
        {
            ConsoleOutput.Warning($"Error stopping service: {ex.Message}");
        }

        // Delete the service
        RunSc($"delete {ServiceName}", "Service removed");

        // Wait for SCM to fully release the service after delete
        WaitForServiceDeletion();

        // Verify the service is actually gone
        if (IsServicePresent())
        {
            ConsoleOutput.Error("Service still exists after deletion attempt");
            return false;
        }

        ConsoleOutput.Success("Uninstall complete");
        return true;
    }

    private static bool IsServicePresent()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            _ = sc.Status;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void RemoveExistingService()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            var status = sc.Status; // This throws if service doesn't exist

            ConsoleOutput.Info("Existing service found, removing...");

            if (status == ServiceControllerStatus.Running)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            }

            RunSc($"delete {ServiceName}", "Existing service removed");
            WaitForServiceDeletion();
        }
        catch (InvalidOperationException)
        {
            // Service doesn't exist yet - that's fine
        }
    }

    private static void WaitForServiceDeletion()
    {
        // Poll SCM until the service is fully removed (sc query returns error 1060)
        // instead of using a fixed Thread.Sleep which can be too short.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var sc = new ServiceController(ServiceName);
                _ = sc.Status;
                // Service still exists - wait briefly and retry
                Thread.Sleep(500);
            }
            catch (InvalidOperationException)
            {
                // Service no longer exists - deletion complete
                return;
            }
        }
    }

    private static string? GetServiceExePath()
    {
        var exePath = Environment.ProcessPath;

        // Reject if running via dotnet.exe (e.g. "dotnet run") - the service
        // would register dotnet.exe as the binary, which is wrong.
        if (exePath != null &&
            Path.GetFileName(exePath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            ConsoleOutput.Warning("Running via 'dotnet run' - cannot register dotnet.exe as a service.");
            ConsoleOutput.Info("Build a published exe first: dotnet publish -c Release -o publish --self-contained true -p:PublishSingleFile=true");

            // Fall back to looking for a published exe
            var assemblyDir = AppContext.BaseDirectory;
            var candidatePath = Path.Combine(assemblyDir, "KeepAliveService.exe");
            if (File.Exists(candidatePath))
            {
                return candidatePath;
            }

            return null;
        }

        if (exePath != null && File.Exists(exePath) && exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return exePath;
        }

        // Fallback: look for published exe next to the running assembly
        var fallbackDir = AppContext.BaseDirectory;
        var fallbackPath = Path.Combine(fallbackDir, "KeepAliveService.exe");
        if (File.Exists(fallbackPath))
        {
            return fallbackPath;
        }

        return null;
    }

    private static bool RunSc(string arguments, string successMessage)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                ConsoleOutput.Error($"{successMessage} - could not start sc.exe");
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
            else
            {
                var msg = !string.IsNullOrEmpty(error) ? error : output;
                ConsoleOutput.Error($"{successMessage} - sc.exe failed: {msg}");
                return false;
            }
        }
        catch (Exception ex)
        {
            ConsoleOutput.Error($"{successMessage} - {ex.Message}");
            return false;
        }
    }
}
