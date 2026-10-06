using System.ServiceProcess;
using System.Text.Json;
using KeepAliveService.Setup;
using KeepAliveService.Update;
using Microsoft.Win32;

namespace KeepAliveService.Services;

public static class StatusStates
{
    public const string Ok = "ok";
    public const string Failing = "failing";
    public const string Exception = "exception";
    public const string Cooldown = "cooldown";
    public const string SetupIncomplete = "setup-incomplete";
}

/// <summary>
/// Writes C:\ProgramData\WindowsKeepAlive\status.json (schema 1) for the Site Worker host.
/// Holds no secrets: only the mode string from settings, counts, check messages and booleans.
/// </summary>
public static class StatusWriter
{
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string StatusPath => Path.Combine(AppSettings.ProgramDataDirectory, "status.json");

    /// <summary>Builds the JSON text. <paramref name="observedUtc"/> is the time of the check, not of the write.</summary>
    public static string BuildJson(
        string state,
        string mode,
        DateTime observedUtc,
        DateTime writtenUtc,
        ComplianceReport? report,
        bool serviceInstalled,
        bool serviceRunning,
        bool autoLoginConfigured,
        int remediationFailures,
        DateTime? cooldownUntilUtc,
        string version)
    {
        var doc = new Dictionary<string, object?>
        {
            ["schema"] = SchemaVersion,
            ["version"] = version,
            ["mode"] = mode,
            ["writtenUtc"] = Iso(writtenUtc),
            ["observedUtc"] = Iso(observedUtc),
            ["state"] = state,
            ["pass"] = report?.Pass ?? 0,
            ["fail"] = report?.Fail ?? 0,
            ["warn"] = report?.Warn ?? 0,
            ["failing"] = report?.Failing ?? Array.Empty<string>(),
            ["skipped"] = report?.Skipped ?? Array.Empty<string>(),
            ["serviceInstalled"] = serviceInstalled,
            ["serviceRunning"] = serviceRunning,
            ["autoLoginConfigured"] = autoLoginConfigured,
            ["remediationFailures"] = remediationFailures,
            ["cooldownUntilUtc"] = cooldownUntilUtc.HasValue ? Iso(cooldownUntilUtc.Value) : null,
        };
        return JsonSerializer.Serialize(doc, JsonOptions);
    }

    /// <summary>Writes status.json atomically (temp file, then move). Never throws; returns false on failure. <paramref name="statusPathOverride"/> is for tests.</summary>
    public static bool Write(
        string state,
        KeepAliveMode mode,
        DateTime observedUtc,
        ComplianceReport? report,
        int remediationFailures,
        DateTime? cooldownUntilUtc,
        ILogger? logger = null,
        string? statusPathOverride = null)
    {
        var statusPath = statusPathOverride ?? StatusPath;
        try
        {
            if (statusPathOverride == null)
            {
                AppSettings.EnsureDirectories();
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(statusPath)!);
            }

            var (installed, running) = ReadServiceState();
            var json = BuildJson(
                state,
                mode.ToSettingsValue(),
                observedUtc,
                DateTime.UtcNow,
                report,
                installed,
                running,
                ReadAutoLoginConfigured(),
                remediationFailures,
                cooldownUntilUtc,
                GetVersion());

            var tempPath = statusPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, statusPath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not write {Path}", statusPath);
            return false;
        }
    }

    private static string Iso(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    private static string GetVersion() =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    private static (bool installed, bool running) ReadServiceState()
    {
        try
        {
            using var sc = new ServiceController("KeepAliveService");
            return (true, sc.Status == ServiceControllerStatus.Running);
        }
        catch
        {
            return (false, false);
        }
    }

    private static bool ReadAutoLoginConfigured()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon");
            return key?.GetValue("AutoAdminLogon") as string == "1"
                && !string.IsNullOrEmpty(key.GetValue("DefaultUserName") as string);
        }
        catch
        {
            return false;
        }
    }
}
