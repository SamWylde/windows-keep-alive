using KeepAliveService.UI;
using Microsoft.Win32;

namespace KeepAliveService.Setup;

public static class SignInReadinessDetector
{
    private const string PasswordlessDevicePath =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\PasswordLess\Device";

    private const string PolicyManagerCurrentAuthenticationPath =
        @"SOFTWARE\Microsoft\PolicyManager\current\device\Authentication";

    private const string PolicyManagerCurrentPasswordlessPath =
        @"SOFTWARE\Microsoft\PolicyManager\current\device\Authentication\EnablePasswordlessExperience";

    private const string PolicyManagerDefaultPasswordlessPath =
        @"SOFTWARE\Microsoft\PolicyManager\default\Authentication\EnablePasswordlessExperience";

    public static SignInReadinessResult Assess(CredentialInfo credentials)
    {
        if (credentials.AccountType != AccountType.MicrosoftAccount)
        {
            return new SignInReadinessResult(
                SignInReadinessStatus.Ready,
                "Password sign-in readiness check: not required for this account type.",
                []);
        }

        var keyReadErrors = new List<string>();

        var devicePasswordless = TryReadDword(
            PasswordlessDevicePath,
            "DevicePasswordLessBuildVersion",
            keyReadErrors);

        var policyPasswordlessCurrent = TryReadDword(
            PolicyManagerCurrentAuthenticationPath,
            "EnablePasswordlessExperience",
            keyReadErrors)
            ?? TryReadDword(PolicyManagerCurrentPasswordlessPath, "value", keyReadErrors);

        var policyPasswordlessDefault = TryReadDword(
            PolicyManagerDefaultPasswordlessPath,
            "value",
            keyReadErrors);

        var helloOnlyToggleEnabled = devicePasswordless.HasValue && devicePasswordless.Value != 0;
        var passwordlessPolicyEnabled =
            (policyPasswordlessCurrent.HasValue && policyPasswordlessCurrent.Value != 0) ||
            (policyPasswordlessDefault.HasValue && policyPasswordlessDefault.Value != 0);

        if (helloOnlyToggleEnabled || passwordlessPolicyEnabled)
        {
            var reasons = new List<string>();
            if (helloOnlyToggleEnabled)
            {
                reasons.Add($"DevicePasswordLessBuildVersion={devicePasswordless}");
            }

            if (passwordlessPolicyEnabled)
            {
                reasons.Add(
                    $"EnablePasswordlessExperience(current={policyPasswordlessCurrent?.ToString() ?? "not set"}, " +
                    $"default={policyPasswordlessDefault?.ToString() ?? "not set"})");
            }

            var message =
                "Password sign-in appears disabled by Windows Hello/passwordless settings: " +
                string.Join("; ", reasons) +
                ". Auto-logon requires password-based sign-in.";

            string[] remediation =
            [
                "Turn off 'Only allow Windows Hello sign-in for Microsoft accounts on this device' in Settings > Accounts > Sign-in options.",
                "If managed by policy, disable 'Windows passwordless experience' policy and sync/reboot.",
                "Confirm your Microsoft account has a password (not passwordless-only), then run Test Credentials again."
            ];

            return new SignInReadinessResult(SignInReadinessStatus.Blocked, message, remediation);
        }

        if (keyReadErrors.Count > 0)
        {
            return new SignInReadinessResult(
                SignInReadinessStatus.Warning,
                "Could not fully read all passwordless policy keys. Continuing with credential validation.",
                keyReadErrors);
        }

        return new SignInReadinessResult(
            SignInReadinessStatus.Ready,
            "Password sign-in appears available for Microsoft account.",
            []);
    }

    private static int? TryReadDword(string path, string valueName, List<string> keyReadErrors)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            if (key == null)
            {
                return null;
            }

            var value = key.GetValue(valueName);
            if (value is int intValue)
            {
                return intValue;
            }

            return null;
        }
        catch (Exception ex)
        {
            keyReadErrors.Add($"{path}\\{valueName}: {ex.Message}");
            return null;
        }
    }
}

public enum SignInReadinessStatus
{
    Ready,
    Warning,
    Blocked,
}

public sealed record SignInReadinessResult(
    SignInReadinessStatus Status,
    string Message,
    IReadOnlyList<string> RemediationSteps);
