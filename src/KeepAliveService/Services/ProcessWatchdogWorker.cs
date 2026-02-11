using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Win32;

namespace KeepAliveService.Services;

public class ProcessWatchdogWorker : BackgroundService
{
    private readonly ILogger<ProcessWatchdogWorker> _logger;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(15);

    // Rate limiter: track failures to avoid thrashing
    private const int MaxFailuresBeforeCooldown = 5;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CooldownPeriod = TimeSpan.FromMinutes(5);
    private readonly Queue<DateTime> _failureTimestamps = new();
    private DateTime _cooldownUntil = DateTime.MinValue;

    public ProcessWatchdogWorker(ILogger<ProcessWatchdogWorker> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TeamViewer watchdog started");

        // Let the system stabilize after boot before checking
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                EnsureTeamViewerRunning();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in TeamViewer watchdog check");
            }

            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("TeamViewer watchdog stopped");
    }

    private void EnsureTeamViewerRunning()
    {
        // Check if we're in cooldown due to repeated failures
        if (DateTime.UtcNow < _cooldownUntil)
        {
            return;
        }

        // Check for TeamViewer service first (preferred - handles remote connections)
        if (IsTeamViewerServiceRunning())
        {
            return;
        }

        // Check for TeamViewer process as fallback (non-service installations)
        if (IsTeamViewerProcessRunning())
        {
            return;
        }

        _logger.LogWarning("TeamViewer not detected, attempting restart");

        // Track this failure for rate limiting
        RecordFailure();
        if (IsInCooldown())
        {
            _cooldownUntil = DateTime.UtcNow.Add(CooldownPeriod);
            _logger.LogError(
                "TeamViewer failed to start {Count} times in {Window} minutes. " +
                "Entering cooldown for {Cooldown} minutes. Check TeamViewer installation.",
                MaxFailuresBeforeCooldown,
                FailureWindow.TotalMinutes,
                CooldownPeriod.TotalMinutes);
            return;
        }

        // Try restarting via Windows Service Controller first
        if (TryRestartViaServiceController())
        {
            return;
        }

        // Fallback: launch executable directly
        TryLaunchExecutable();
    }

    private static bool IsTeamViewerServiceRunning()
    {
        try
        {
            using var sc = new ServiceController("TeamViewer");
            return sc.Status == ServiceControllerStatus.Running;
        }
        catch
        {
            // Service not installed
            return false;
        }
    }

    private static bool IsTeamViewerProcessRunning()
    {
        var serviceProcs = Process.GetProcessesByName("TeamViewer_Service");
        if (serviceProcs.Length > 0)
        {
            DisposeAll(serviceProcs);
            return true;
        }

        var uiProcs = Process.GetProcessesByName("TeamViewer");
        if (uiProcs.Length > 0)
        {
            DisposeAll(uiProcs);
            return true;
        }

        return false;
    }

    private bool TryRestartViaServiceController()
    {
        try
        {
            using var sc = new ServiceController("TeamViewer");
            if (sc.Status != ServiceControllerStatus.Running)
            {
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                _logger.LogInformation("TeamViewer service restarted via Service Controller");
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not restart TeamViewer via Service Controller");
        }

        return false;
    }

    private bool TryLaunchExecutable()
    {
        var exePath = FindTeamViewerPath();
        if (exePath == null)
        {
            _logger.LogError("Could not find TeamViewer installation path");
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            _logger.LogInformation("TeamViewer launched from {Path}", exePath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch TeamViewer from {Path}", exePath);
            return false;
        }
    }

    private static string? FindTeamViewerPath()
    {
        // Strategy 1: Common install locations
        string[] commonPaths =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "TeamViewer", "TeamViewer.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "TeamViewer", "TeamViewer.exe"),
        ];

        foreach (var path in commonPaths)
        {
            if (File.Exists(path))
                return path;
        }

        // Strategy 2: TeamViewer registry key
        string[] registryPaths =
        [
            @"SOFTWARE\TeamViewer",
            @"SOFTWARE\WOW6432Node\TeamViewer",
        ];

        foreach (var regPath in registryPaths)
        {
            using var key = Registry.LocalMachine.OpenSubKey(regPath);
            if (key?.GetValue("InstallationDirectory") is string installDir)
            {
                var fullPath = Path.Combine(installDir, "TeamViewer.exe");
                if (File.Exists(fullPath))
                    return fullPath;
            }
        }

        // Strategy 3: App Paths registry
        using var appPathKey = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\TeamViewer.exe");
        if (appPathKey?.GetValue(null) is string appPath && File.Exists(appPath))
            return appPath;

        return null;
    }

    private void RecordFailure()
    {
        var now = DateTime.UtcNow;
        _failureTimestamps.Enqueue(now);

        // Prune old failures outside the window
        while (_failureTimestamps.Count > 0 &&
               _failureTimestamps.Peek() < now - FailureWindow)
        {
            _failureTimestamps.Dequeue();
        }
    }

    private bool IsInCooldown()
    {
        return _failureTimestamps.Count >= MaxFailuresBeforeCooldown;
    }

    private static void DisposeAll(Process[] processes)
    {
        foreach (var p in processes)
            p.Dispose();
    }
}
