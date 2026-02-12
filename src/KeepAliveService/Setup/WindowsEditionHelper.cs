using Microsoft.Win32;

namespace KeepAliveService.Setup;

public sealed record WindowsEditionInfo(
    string EditionId,
    string ProductName,
    int BuildNumber)
{
    public bool IsWindows10 =>
        ProductName.Contains("Windows 10", StringComparison.OrdinalIgnoreCase);

    public bool IsWindows11 =>
        ProductName.Contains("Windows 11", StringComparison.OrdinalIgnoreCase);

    public bool IsSupportedOsFamily => IsWindows10 || IsWindows11;

    public bool SupportsBaseline => IsSupportedOsFamily && BuildNumber >= WindowsEditionHelper.MinSupportedBuild;

    public bool IsHomeOrCore =>
        EditionId.Contains("Core", StringComparison.OrdinalIgnoreCase) ||
        EditionId.Contains("Home", StringComparison.OrdinalIgnoreCase) ||
        ProductName.Contains("Home", StringComparison.OrdinalIgnoreCase);
}

public static class WindowsEditionHelper
{
    private const string CurrentVersionPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    public const int MinSupportedBuild = 19041;

    public static bool TryGetWindowsEditionInfo(out WindowsEditionInfo? info, out string? error)
    {
        info = null;
        error = null;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(CurrentVersionPath);
            var edition = key?.GetValue("EditionID") as string ?? "Unknown";
            var productName = key?.GetValue("ProductName") as string ?? "Unknown";
            var buildString = key?.GetValue("CurrentBuildNumber") as string ?? "0";

            if (!int.TryParse(buildString, out var buildNumber))
            {
                error = $"Could not parse Windows build number: {buildString}";
                return false;
            }

            info = new WindowsEditionInfo(edition, productName, buildNumber);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
