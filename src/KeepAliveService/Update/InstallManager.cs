using System.Diagnostics;

namespace KeepAliveService.Update;

public static class InstallManager
{
    public static string CanonicalInstallDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsKeepAlive");

    public static string CanonicalExePath => Path.Combine(CanonicalInstallDirectory, "KeepAliveService.exe");

    public static bool IsRunningFromCanonicalPath()
    {
        var currentPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentPath))
        {
            return false;
        }

        return PathsEqual(currentPath, CanonicalExePath);
    }

    public static bool EnsureInstalledAndRelaunchIfNeeded(string[]? args = null)
    {
        EnsureProgramDataLayout();

        var currentPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentPath) || !File.Exists(currentPath))
        {
            throw new InvalidOperationException("Unable to determine current executable path.");
        }

        if (PathsEqual(currentPath, CanonicalExePath))
        {
            PersistInstallPath(CanonicalExePath);
            return false;
        }

        Directory.CreateDirectory(CanonicalInstallDirectory);
        File.Copy(currentPath, CanonicalExePath, overwrite: true);

        var psi = new ProcessStartInfo
        {
            FileName = CanonicalExePath,
            UseShellExecute = true,
            WorkingDirectory = CanonicalInstallDirectory,
        };

        foreach (var arg in args ?? Array.Empty<string>())
        {
            psi.ArgumentList.Add(arg);
        }

        Process.Start(psi);
        PersistInstallPath(CanonicalExePath);
        return true;
    }

    public static void EnsureProgramDataLayout()
    {
        AppSettings.EnsureDirectories();
    }

    private static void PersistInstallPath(string installPath)
    {
        try
        {
            var settings = AppSettings.Load();
            if (!string.Equals(settings.InstallPath, installPath, StringComparison.OrdinalIgnoreCase))
            {
                settings.InstallPath = installPath;
                settings.Save();
            }
        }
        catch
        {
            // Best effort only.
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }
}
