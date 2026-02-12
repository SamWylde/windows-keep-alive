using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KeepAliveService.Update;

public sealed class AppSettings
{
    private static readonly byte[] PasswordEntropy = Encoding.UTF8.GetBytes("WindowsKeepAlive.CredentialEntropy.v1");

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

    public string? SavedUsername { get; set; }

    public string? SavedAccountType { get; set; }

    public string? SavedDomain { get; set; }

    public string? SavedPasswordEncrypted { get; set; }

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

    public static AppSettings Load()
    {
        EnsureDirectories();

        try
        {
            if (!File.Exists(SettingsPath))
            {
                var defaults = new AppSettings();
                defaults.Save();
                return defaults;
            }

            var json = File.ReadAllText(SettingsPath);
            var parsed = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            parsed.UpdateCheckIntervalHours = NormalizeInterval(parsed.UpdateCheckIntervalHours);
            if (string.IsNullOrWhiteSpace(parsed.InstallPath))
            {
                parsed.InstallPath = new AppSettings().InstallPath;
            }

            return parsed;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        EnsureDirectories();
        UpdateCheckIntervalHours = NormalizeInterval(UpdateCheckIntervalHours);
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(SettingsPath, json);
    }

    public void SetSavedPassword(string? password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            SavedPasswordEncrypted = null;
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(password);
        var protectedBytes = ProtectedData.Protect(bytes, PasswordEntropy, DataProtectionScope.LocalMachine);
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
            var plain = ProtectedData.Unprotect(bytes, PasswordEntropy, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }

    private static int NormalizeInterval(int value)
    {
        return value <= 0 ? 24 : value;
    }
}
