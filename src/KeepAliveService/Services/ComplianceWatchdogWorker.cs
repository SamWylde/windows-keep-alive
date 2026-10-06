using KeepAliveService.Setup;
using KeepAliveService.Update;

namespace KeepAliveService.Services;

public class ComplianceWatchdogWorker : BackgroundService
{
    private readonly ILogger<ComplianceWatchdogWorker> _logger;
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromHours(24);
    private const int MaxConsecutiveFailuresBeforeCooldown = 3;

    private int _consecutiveRemediationFailures;
    private DateTime _cooldownUntilUtc = DateTime.MinValue;
    private int _failuresAtCooldown;
    private KeepAliveMode _lastMode = KeepAliveMode.KeepAwakeOnly;

    public ComplianceWatchdogWorker(ILogger<ComplianceWatchdogWorker> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Compliance watchdog started");

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
            var cycleStartUtc = DateTime.UtcNow;
            try
            {
                RunComplianceCycle();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in compliance watchdog cycle");
                var partial = ComplianceChecker.LastReport;
                var failing = new List<string> { $"Compliance cycle threw {ex.GetType().Name}" };
                failing.AddRange(partial?.Failing ?? Array.Empty<string>());
                StatusWriter.Write(
                    StatusStates.Exception,
                    _lastMode,
                    cycleStartUtc,
                    new ComplianceReport(cycleStartUtc, partial?.Pass ?? 0, partial?.Fail ?? 0, partial?.Warn ?? 0, failing, partial?.Skipped ?? Array.Empty<string>()),
                    _consecutiveRemediationFailures,
                    CooldownOrNull(),
                    _logger);
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

        _logger.LogInformation("Compliance watchdog stopped");
    }

    private void RunComplianceCycle()
    {
        var settings = AppSettings.Load();
        var mode = settings.GetOperationMode();
        _lastMode = mode;

        if (settings.SetupCompletedUtc == null)
        {
            _logger.LogDebug("Skipping compliance watchdog: setup has not been completed yet.");
            StatusWriter.Write(StatusStates.SetupIncomplete, mode, DateTime.UtcNow, null, 0, null, _logger);
            return;
        }

        if (DateTime.UtcNow < _cooldownUntilUtc)
        {
            _logger.LogDebug("Compliance watchdog cooldown active until {CooldownUntilUtc}", _cooldownUntilUtc);
            // Read-only check so the report keeps a fresh observation; no remediation runs during cooldown.
            ComplianceChecker.RunCheck(mode, includeStartupTask: false);
            var cooldownReport = ComplianceChecker.LastReport;
            StatusWriter.Write(StatusStates.Cooldown, mode, cooldownReport?.ObservedUtc ?? DateTime.UtcNow, cooldownReport, _failuresAtCooldown, _cooldownUntilUtc, _logger);
            return;
        }

        // Skip startup task check: the watchdog runs as SYSTEM and cannot
        // create a correct user-session scheduled task.
        var initialCompliance = ComplianceChecker.RunCheck(mode, includeStartupTask: false);
        if (initialCompliance == 0)
        {
            _consecutiveRemediationFailures = 0;
            _logger.LogDebug("Compliance watchdog check complete: all settings are compliant.");
            WriteResult(StatusStates.Ok, mode);
            return;
        }

        _logger.LogWarning("Compliance drift detected in {Mode} mode, applying remediation.", mode);

        var updatePolicyOk = true;
        if (mode.UsesWindowsUpdatePolicy())
        {
            updatePolicyOk = TryApplyStep("Update policy", UpdatePolicyConfigurator.Configure);
        }

        var autoLoginOk = true;
        if (mode.UsesAutoLogin())
        {
            autoLoginOk = TryApplyStep("Auto-login non-credential settings", AutoLogonConfigurator.ApplyNonCredentialSettings);
        }

        var powerOk = TryApplyStep("Power settings", PowerConfigurator.Configure);
        var networkOk = true;
        if (mode.UsesNetworkHardening())
        {
            networkOk = TryApplyStep("Network settings", NetworkConfigurator.Configure);
        }
        // NOTE: Startup task is NOT remediated here. The watchdog runs as SYSTEM,
        // which would create the task under the wrong principal. Only interactive
        // setup (running as the logged-in user) can create a proper user-session task.

        var finalCompliance = ComplianceChecker.RunCheck(mode, includeStartupTask: false);
        if (finalCompliance == 0 && updatePolicyOk && autoLoginOk && powerOk && networkOk)
        {
            _consecutiveRemediationFailures = 0;
            _logger.LogInformation("Compliance remediation completed successfully.");
            WriteResult(StatusStates.Ok, mode);
            return;
        }

        _consecutiveRemediationFailures++;
        var stepFailures = new List<string>();
        if (!updatePolicyOk) stepFailures.Add("Update policy");
        if (!autoLoginOk) stepFailures.Add("Auto-login");
        if (!powerOk) stepFailures.Add("Power");
        if (!networkOk) stepFailures.Add("Network");

        var failedStepSummary = stepFailures.Count == 0
            ? "none"
            : string.Join(", ", stepFailures);

        _logger.LogWarning(
            "Compliance remediation attempt failed ({FailureCount}/{FailureThreshold}). Failed steps: {FailedSteps}. Final compliance exit code: {ComplianceExitCode}.",
            _consecutiveRemediationFailures,
            MaxConsecutiveFailuresBeforeCooldown,
            failedStepSummary,
            finalCompliance);

        if (_consecutiveRemediationFailures >= MaxConsecutiveFailuresBeforeCooldown)
        {
            _cooldownUntilUtc = DateTime.UtcNow.Add(FailureCooldown);
            _logger.LogError(
                "Compliance remediation failed {FailureCount} consecutive times. Entering cooldown until {CooldownUntilUtc}. Re-run setup from the GUI as Administrator.",
                _consecutiveRemediationFailures,
                _cooldownUntilUtc);
            _failuresAtCooldown = _consecutiveRemediationFailures;
            _consecutiveRemediationFailures = 0;
            WriteResult(StatusStates.Cooldown, mode, _failuresAtCooldown);
            return;
        }

        WriteResult(StatusStates.Failing, mode);
    }

    private DateTime? CooldownOrNull() =>
        DateTime.UtcNow < _cooldownUntilUtc ? _cooldownUntilUtc : null;

    private void WriteResult(string state, KeepAliveMode mode, int? remediationFailures = null)
    {
        var report = ComplianceChecker.LastReport;
        StatusWriter.Write(
            state,
            mode,
            report?.ObservedUtc ?? DateTime.UtcNow,
            report,
            remediationFailures ?? _consecutiveRemediationFailures,
            CooldownOrNull(),
            _logger);
    }

    private bool TryApplyStep(string stepName, Func<bool> action)
    {
        try
        {
            var success = action();
            if (!success)
            {
                _logger.LogWarning("Compliance remediation step failed: {StepName}", stepName);
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Compliance remediation step threw an exception: {StepName}", stepName);
            return false;
        }
    }
}
