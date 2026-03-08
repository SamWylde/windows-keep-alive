using System.Security.Principal;
using System.Text.RegularExpressions;

namespace KeepAliveService;

internal static partial class Helpers
{
    // Matches lines ending with ": 0x<hex>" (the format used by powercfg for setting values).
    [GeneratedRegex(@":\s*(0x[0-9a-fA-F]+)\s*$")]
    private static partial Regex PowerCfgHexLineRegex();

    /// <summary>
    /// Parses powercfg /query output for a single setting and extracts AC and DC hex values.
    /// Uses position-based parsing instead of locale-dependent label matching.
    /// powercfg always emits hex value lines in order: min, max, increment, AC, DC (optional).
    /// </summary>
    public static (string? acHex, string? dcHex) ParsePowerCfgSettingValues(string output)
    {
        var hexValues = new List<string>();
        var regex = PowerCfgHexLineRegex();

        foreach (var line in output.Split('\n'))
        {
            var match = regex.Match(line);
            if (match.Success)
                hexValues.Add(match.Groups[1].Value);
        }

        // First 3 are min/max/increment; 4th is AC, 5th is DC
        var acHex = hexValues.Count > 3 ? hexValues[3] : null;
        var dcHex = hexValues.Count > 4 ? hexValues[4] : null;
        return (acHex, dcHex);
    }

    public static bool IsRunningAsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static string FormatBytes(long? bytes)
    {
        if (bytes == null) return "unknown";
        string[] suffixes = ["B", "KB", "MB", "GB"];
        var order = 0;
        double size = bytes.Value;
        while (size >= 1024 && order < suffixes.Length - 1)
        {
            order++;
            size /= 1024;
        }

        return $"{size:0.##} {suffixes[order]}";
    }
}
