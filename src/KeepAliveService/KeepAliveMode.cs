namespace KeepAliveService;

public enum KeepAliveMode
{
    KeepAwakeOnly,
    RemoteAccessWatchdog,
    FullUnattended,
}

public static class KeepAliveModeExtensions
{
    public static bool UsesRemoteAccessWatchdog(this KeepAliveMode mode) =>
        mode is KeepAliveMode.RemoteAccessWatchdog or KeepAliveMode.FullUnattended;

    public static bool UsesAutoLogin(this KeepAliveMode mode) =>
        mode == KeepAliveMode.FullUnattended;

    public static bool UsesNetworkHardening(this KeepAliveMode mode) =>
        mode is KeepAliveMode.RemoteAccessWatchdog or KeepAliveMode.FullUnattended;

    public static bool UsesWindowsUpdatePolicy(this KeepAliveMode mode) =>
        mode == KeepAliveMode.FullUnattended;

    public static string ToSettingsValue(this KeepAliveMode mode) => mode switch
    {
        KeepAliveMode.KeepAwakeOnly => "keep-awake-only",
        KeepAliveMode.RemoteAccessWatchdog => "remote-access-watchdog",
        KeepAliveMode.FullUnattended => "full-unattended",
        _ => "keep-awake-only",
    };

    public static string ToDisplayName(this KeepAliveMode mode) => mode switch
    {
        KeepAliveMode.KeepAwakeOnly => "Keep awake only",
        KeepAliveMode.RemoteAccessWatchdog => "Keep awake + remote access watchdog",
        KeepAliveMode.FullUnattended => "Full unattended auto-login",
        _ => "Keep awake only",
    };

    public static bool TryParse(string? raw, out KeepAliveMode mode)
    {
        mode = KeepAliveMode.KeepAwakeOnly;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var normalized = raw.Trim().Replace("_", "-", StringComparison.Ordinal);
        switch (normalized.ToLowerInvariant())
        {
            case "keep-awake-only":
            case "keep-awake":
            case "awake":
            case "power":
            case "basic":
                mode = KeepAliveMode.KeepAwakeOnly;
                return true;

            case "remote-access-watchdog":
            case "remote-watchdog":
            case "watchdog":
            case "remote":
                mode = KeepAliveMode.RemoteAccessWatchdog;
                return true;

            case "full-unattended":
            case "full":
            case "unattended":
            case "auto-login":
            case "autologin":
                mode = KeepAliveMode.FullUnattended;
                return true;

            default:
                return Enum.TryParse(raw, ignoreCase: true, out mode);
        }
    }

    public static KeepAliveMode ParseOrDefault(string? raw, KeepAliveMode fallback) =>
        TryParse(raw, out var mode) ? mode : fallback;
}
