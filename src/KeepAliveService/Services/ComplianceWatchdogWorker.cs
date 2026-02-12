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
            try
            {
                RunComplianceCycle();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in compliance watchdog cycle");
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
        if (DateTime.UtcNow < _cooldownUntilUtc)
        {
            _logger.LogDebug("Compliance watchdog cooldown active until {CooldownUntilUtc}", _cooldownUntilUtc);
            return;
        }

        var settings = AppSettings.Load();
        if (settings.SetupCompletedUtc == null)
        {
            _logger.LogDebug("Skipping compliance watchdog: setup has not been completed yet.");
            return;
        }

        var initialCompliance = ComplianceChecker.RunCheck();
        if (initialCompliance == 0)
        {
            _consecutiveRemediationFailures = 0;
            _logger.LogDebug("Compliance watchdog check complete: all settings are compliant.");
            return;
        }

        _logger.LogWarning("Compliance drift detected, applying remediation.");

        var updatePolicyOk = TryApplyStep("Update policy", UpdatePolicyConfigurator.Configure);
        var autoLoginOk = TryApplyStep("Auto-login non-credential settings", AutoLogonConfigurator.ApplyNonCredentialSettings);
        var powerOk = TryApplyStep("Power settings", PowerConfigurator.Configure);
        var networkOk = TryApplyStep("Network settings", NetworkConfigurator.Configure);

        var finalCompliance = ComplianceChecker.RunCheck();
        if (finalCompliance == 0 && updatePolicyOk && autoLoginOk && powerOk && networkOk)
        {
            _consecutiveRemediationFailures = 0;
            _logger.LogInformation("Compliance remediation completed successfully.");
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
            _consecutiveRemediationFailures = 0;
        }
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
