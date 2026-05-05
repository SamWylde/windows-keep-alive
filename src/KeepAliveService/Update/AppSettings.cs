using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KeepAliveService.Update;

public sealed class AppSettings
{
    private static readonly byte[] PasswordEntropy = Encoding.UTF8.GetBytes("WindowsKeepAlive.CredentialEntropy.v1");
    private static readonly Mutex SaveMutex = new(false, @"Global\WindowsKeepAlive.Settings.Mutex");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public string InstallPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "WindowsKeepAlive",
        "KeepAliveService.exe");

    public DateTime? LastUpdateCheckUtc { get; set; }

    public int UpdateCheckIntervalHours { get; set; } = 24;

    public DateTime? SetupCompletedUtc { get; set; }

    public string? OperationMode { get; set; } = KeepAliveMode.KeepAwakeOnly.ToSettingsValue();

    public string? SavedUsername { get; set; }

    public string? SavedAccountType { get; set; }

    public string? SavedDomain { get; set; }

    public string? SavedPasswordEncrypted { get; set; }

    public string? StartupTaskUser { get; set; }

    public Dictionary<string, string>? OriginalSettingsBackup { get; set; }

    public static string ProgramDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WindowsKeepAlive");

    public static string SettingsPath => Path.Combine(ProgramDataDirectory, "settings.json");

    public static string LogPath => Path.Combine(ProgramDataDirectory, "app.log");

    public static string ToolsDirectory => Path.Combine(ProgramDataDirectory, "tools");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(ProgramDataDirectory);
        Directory.CreateDirectory(ToolsDirectory);
    }

    public static AppSettings Load(bool createIfMissing = true)
    {
        if (createIfMissing)
        {
            EnsureDirectories();
        }

        try
        {
            if (!File.Exists(SettingsPath))
            {
                var defaults = new AppSettings();
                if (createIfMissing)
                {
                    defaults.Save();
                }

                return defaults;
            }

            var json = File.ReadAllText(SettingsPath);
            var hasOperationMode = HasJsonProperty(json, "operationMode");
            var parsed = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            parsed.UpdateCheckIntervalHours = NormalizeInterval(parsed.UpdateCheckIntervalHours);
            if (string.IsNullOrWhiteSpace(parsed.InstallPath))
            {
                parsed.InstallPath = new AppSettings().InstallPath;
            }

            if (!hasOperationMode || string.IsNullOrWhiteSpace(parsed.OperationMode))
            {
                parsed.SetOperationMode(parsed.SetupCompletedUtc != null
                    ? KeepAliveMode.FullUnattended
                    : KeepAliveMode.KeepAwakeOnly);
            }

            return parsed;
        }
        catch
        {
            // Preserve corrupt file for diagnostics so backup metadata is not silently lost.
            try
            {
                var corruptPath = SettingsPath + ".corrupt";
                if (File.Exists(SettingsPath) && !File.Exists(corruptPath))
                    File.Copy(SettingsPath, corruptPath);
            }
            catch
            {
                // Best effort only.
            }

            return new AppSettings();
        }
    }

    public void Save(bool preserveConcurrentSetupState = true)
    {
        EnsureDirectories();
        UpdateCheckIntervalHours = NormalizeInterval(UpdateCheckIntervalHours);
        OperationMode = GetOperationMode().ToSettingsValue();

        var acquired = false;
        try
        {
            acquired = SaveMutex.WaitOne(5000);
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }

        if (!acquired)
        {
            // Another process holds the lock for too long; skip write to avoid data corruption.
            return;
        }

        try
        {
            // Preserve fields that may have been written by another process (e.g. setup
            // writes OriginalSettingsBackup and SetupCompletedUtc while the GUI holds a
            // stale in-memory instance). Without this, a GUI credential save would erase
            // the backup data written by setup.
            if (preserveConcurrentSetupState && File.Exists(SettingsPath))
            {
                try
                {
                    var existing = JsonSerializer.Deserialize<AppSettings>(
                        File.ReadAllText(SettingsPath), JsonOptions);
                    if (existing != null)
                    {
                        OriginalSettingsBackup ??= existing.OriginalSettingsBackup;
                        SetupCompletedUtc ??= existing.SetupCompletedUtc;
                        StartupTaskUser ??= existing.StartupTaskUser;
                    }
                }
                catch
                {
                    // Best effort — proceed with current values.
                }
            }

            var json = JsonSerializer.Serialize(this, JsonOptions);
            var tempPath = SettingsPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, SettingsPath, overwrite: true);
        }
        finally
        {
            if (acquired)
            {
                SaveMutex.ReleaseMutex();
            }
        }
    }

    public void SetSavedPassword(string? password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            SavedPasswordEncrypted = null;
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(password);
        var protectedBytes = ProtectedData.Protect(bytes, PasswordEntropy, DataProtectionScope.CurrentUser);
        SavedPasswordEncrypted = Convert.ToBase64String(protectedBytes);
    }

    public string? GetSavedPassword()
    {
        if (string.IsNullOrWhiteSpace(SavedPasswordEncrypted))
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(SavedPasswordEncrypted);
            var plain = ProtectedData.Unprotect(bytes, PasswordEntropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }

    public KeepAliveMode GetOperationMode()
    {
        var fallback = SetupCompletedUtc != null
            ? KeepAliveMode.FullUnattended
            : KeepAliveMode.KeepAwakeOnly;
        return KeepAliveModeExtensions.ParseOrDefault(OperationMode, fallback);
    }

    public void SetOperationMode(KeepAliveMode mode)
    {
        OperationMode = mode.ToSettingsValue();
    }

    private static int NormalizeInterval(int value)
    {
        return value <= 0 ? 24 : value;
    }

    private static bool HasJsonProperty(string json, string propertyName)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty(propertyName, out _);
        }
        catch
        {
            return false;
        }
    }
}
