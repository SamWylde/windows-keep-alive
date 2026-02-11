using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace KeepAliveService.Update;

public sealed class GitHubUpdateChecker : IDisposable
{
    private const string Owner = "SamWylde";
    private const string Repo = "windows-keep-alive";

    private readonly AppSettings _settings;
    private readonly HttpClient _httpClient;
    private readonly bool _disposeHttpClient;

    public GitHubUpdateChecker(AppSettings settings, HttpClient? httpClient = null)
    {
        _settings = settings;

        if (httpClient == null)
        {
            _httpClient = new HttpClient();
            _disposeHttpClient = true;
        }
        else
        {
            _httpClient = httpClient;
            _disposeHttpClient = false;
        }

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"WindowsKeepAlive/{version}");
    }

    public async Task<UpdateCheckResult> CheckForUpdateAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        var currentVersion = NormalizeVersion(Assembly.GetExecutingAssembly().GetName().Version);
        var nowUtc = DateTime.UtcNow;
        var interval = TimeSpan.FromHours(_settings.UpdateCheckIntervalHours);

        if (!force &&
            _settings.LastUpdateCheckUtc.HasValue &&
            (nowUtc - _settings.LastUpdateCheckUtc.Value) < interval)
        {
            return new UpdateCheckResult(
                WasChecked: false,
                IsUpdateAvailable: false,
                CurrentVersion: currentVersion,
                LatestVersion: null,
                LatestTag: null,
                ReleaseNotes: null,
                DownloadUrl: null,
                AssetName: null,
                Message: "Skipped update check (interval not reached).");
        }

        try
        {
            var url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var draft = root.TryGetProperty("draft", out var draftProp) && draftProp.GetBoolean();
            var prerelease = root.TryGetProperty("prerelease", out var preProp) && preProp.GetBoolean();
            var tag = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() : null;
            var body = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() : string.Empty;

            if (draft || prerelease || string.IsNullOrWhiteSpace(tag))
            {
                _settings.LastUpdateCheckUtc = nowUtc;
                _settings.Save();
                return new UpdateCheckResult(
                    WasChecked: true,
                    IsUpdateAvailable: false,
                    CurrentVersion: currentVersion,
                    LatestVersion: null,
                    LatestTag: tag,
                    ReleaseNotes: body,
                    DownloadUrl: null,
                    AssetName: null,
                    Message: "No stable release found.");
            }

            var latestVersion = ParseTagVersion(tag);
            var (assetName, downloadUrl) = SelectExeAsset(root);

            _settings.LastUpdateCheckUtc = nowUtc;
            _settings.Save();

            if (latestVersion == null)
            {
                return new UpdateCheckResult(
                    WasChecked: true,
                    IsUpdateAvailable: false,
                    CurrentVersion: currentVersion,
                    LatestVersion: null,
                    LatestTag: tag,
                    ReleaseNotes: body,
                    DownloadUrl: downloadUrl,
                    AssetName: assetName,
                    Message: $"Latest release tag '{tag}' is not a valid version.");
            }

            var updateAvailable = latestVersion > currentVersion &&
                                  !string.IsNullOrWhiteSpace(downloadUrl);

            var message = updateAvailable
                ? $"Update available: v{latestVersion}"
                : "You are on the latest version.";

            if (latestVersion > currentVersion && string.IsNullOrWhiteSpace(downloadUrl))
            {
                message = "A newer version exists, but no .exe asset was found.";
            }

            return new UpdateCheckResult(
                WasChecked: true,
                IsUpdateAvailable: updateAvailable,
                CurrentVersion: currentVersion,
                LatestVersion: latestVersion,
                LatestTag: tag,
                ReleaseNotes: body,
                DownloadUrl: downloadUrl,
                AssetName: assetName,
                Message: message);
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(
                WasChecked: true,
                IsUpdateAvailable: false,
                CurrentVersion: currentVersion,
                LatestVersion: null,
                LatestTag: null,
                ReleaseNotes: null,
                DownloadUrl: null,
                AssetName: null,
                Message: $"Update check failed: {ex.Message}");
        }
    }

    public async Task<UpdateApplyResult> ApplyUpdateAsync(UpdateCheckResult update, CancellationToken cancellationToken = default)
    {
        if (!update.IsUpdateAvailable || string.IsNullOrWhiteSpace(update.DownloadUrl))
        {
            return new UpdateApplyResult(false, "No update is currently available.");
        }

        try
        {
            var targetExe = string.IsNullOrWhiteSpace(_settings.InstallPath)
                ? InstallManager.CanonicalExePath
                : _settings.InstallPath;

            Directory.CreateDirectory(Path.GetDirectoryName(targetExe)!);

            var tempExe = Path.Combine(Path.GetTempPath(), $"KeepAliveService_update_{Guid.NewGuid():N}.exe");
            await DownloadFileAsync(update.DownloadUrl, tempExe, cancellationToken);

            var scriptPath = Path.Combine(Path.GetTempPath(), $"KeepAliveService_apply_{Guid.NewGuid():N}.cmd");
            var script = BuildUpdateScript(
                targetExe,
                tempExe,
                Environment.ProcessId,
                "KeepAliveService");
            await File.WriteAllTextAsync(scriptPath, script, Encoding.ASCII, cancellationToken);

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(targetExe),
            });

            return new UpdateApplyResult(true, "Update staged. The application will restart after replacement.");
        }
        catch (Exception ex)
        {
            return new UpdateApplyResult(false, $"Failed to apply update: {ex.Message}");
        }
    }

    private async Task DownloadFileAsync(string downloadUrl, string destinationPath, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = File.Create(destinationPath);
        await source.CopyToAsync(destination, cancellationToken);
    }

    private static (string? assetName, string? downloadUrl) SelectExeAsset(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return (null, null);
        }

        string? fallbackName = null;
        string? fallbackUrl = null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            var url = asset.TryGetProperty("browser_download_url", out var urlProp) ? urlProp.GetString() : null;

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            if (name.Equals("KeepAliveService.exe", StringComparison.OrdinalIgnoreCase))
            {
                return (name, url);
            }

            if (fallbackUrl == null && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                fallbackName = name;
                fallbackUrl = url;
            }
        }

        return (fallbackName, fallbackUrl);
    }

    private static Version? ParseTagVersion(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var cleaned = tag.Trim().TrimStart('v', 'V');
        var dashIndex = cleaned.IndexOf('-');
        if (dashIndex > 0)
        {
            cleaned = cleaned[..dashIndex];
        }

        return Version.TryParse(cleaned, out var version)
            ? NormalizeVersion(version)
            : null;
    }

    private static Version NormalizeVersion(Version? version)
    {
        if (version == null)
        {
            return new Version(0, 0, 0, 0);
        }

        var build = version.Build < 0 ? 0 : version.Build;
        var revision = version.Revision < 0 ? 0 : version.Revision;
        return new Version(version.Major, version.Minor, build, revision);
    }

    private static string BuildUpdateScript(string targetExePath, string downloadedExePath, int currentProcessId, string serviceName)
    {
        var escapedTarget = targetExePath.Replace("\"", "\"\"");
        var escapedDownload = downloadedExePath.Replace("\"", "\"\"");
        var backupPath = $"{targetExePath}.bak".Replace("\"", "\"\"");

        return $@"@echo off
setlocal
set ""TARGET_EXE={escapedTarget}""
set ""NEW_EXE={escapedDownload}""
set ""BACKUP_EXE={backupPath}""
set ""APP_PID={currentProcessId}""

:wait_for_app_exit
tasklist /FI ""PID eq %APP_PID%"" | find ""%APP_PID%"" >nul
if not errorlevel 1 (
    timeout /t 1 /nobreak >nul
    goto wait_for_app_exit
)

net stop {serviceName} >nul 2>&1
timeout /t 2 /nobreak >nul

if exist ""%BACKUP_EXE%"" del /f /q ""%BACKUP_EXE%"" >nul 2>&1
if exist ""%TARGET_EXE%"" move /y ""%TARGET_EXE%"" ""%BACKUP_EXE%"" >nul 2>&1

move /y ""%NEW_EXE%"" ""%TARGET_EXE%"" >nul 2>&1
if errorlevel 1 goto rollback

net start {serviceName} >nul 2>&1
start """" ""%TARGET_EXE%""
del /f /q ""%~f0"" >nul 2>&1
exit /b 0

:rollback
if exist ""%BACKUP_EXE%"" move /y ""%BACKUP_EXE%"" ""%TARGET_EXE%"" >nul 2>&1
net start {serviceName} >nul 2>&1
start """" ""%TARGET_EXE%""
del /f /q ""%~f0"" >nul 2>&1
exit /b 1
";
    }

    public void Dispose()
    {
        if (_disposeHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}

public sealed record UpdateCheckResult(
    bool WasChecked,
    bool IsUpdateAvailable,
    Version CurrentVersion,
    Version? LatestVersion,
    string? LatestTag,
    string? ReleaseNotes,
    string? DownloadUrl,
    string? AssetName,
    string Message);

public sealed record UpdateApplyResult(
    bool Started,
    string Message);
