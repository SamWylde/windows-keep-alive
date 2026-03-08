using System.Diagnostics;
using System.Runtime.InteropServices;

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
            EnsureDesktopShortcut();
            return false;
        }

        Directory.CreateDirectory(CanonicalInstallDirectory);
        File.Copy(currentPath, CanonicalExePath, overwrite: true);
        EnsureDesktopShortcut();

        var psi = new ProcessStartInfo
        {
            FileName = CanonicalExePath,
            Verb = "runas",
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

    public static void RemoveDesktopShortcut()
    {
        try
        {
            var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrWhiteSpace(desktopPath))
            {
                return;
            }

            var shortcutPath = Path.Combine(desktopPath, "Windows Keep Alive.lnk");
            if (File.Exists(shortcutPath))
            {
                File.Delete(shortcutPath);
            }
        }
        catch
        {
            // Best effort only.
        }
    }

    private static void EnsureDesktopShortcut()
    {
        try
        {
            var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrWhiteSpace(desktopPath))
            {
                return;
            }

            Directory.CreateDirectory(desktopPath);
            var shortcutPath = Path.Combine(desktopPath, "Windows Keep Alive.lnk");
            CreateShortcut(shortcutPath, CanonicalExePath, CanonicalInstallDirectory, "Windows Keep Alive");
        }
        catch
        {
            // Best effort only.
        }
    }

    private static void CreateShortcut(string shortcutPath, string targetPath, string workingDirectory, string description)
    {
        object? shellObject = null;
        object? shortcutObject = null;

        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
            {
                return;
            }

            shellObject = Activator.CreateInstance(shellType);
            if (shellObject == null)
            {
                return;
            }

            shortcutObject = shellType.InvokeMember(
                "CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod,
                binder: null,
                target: shellObject,
                args: [shortcutPath]);

            if (shortcutObject == null)
            {
                return;
            }

            var shortcutType = shortcutObject.GetType();
            shortcutType.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcutObject, [targetPath]);
            shortcutType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcutObject, [workingDirectory]);
            shortcutType.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, shortcutObject, [description]);
            shortcutType.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, shortcutObject, [$"{targetPath},0"]);
            shortcutType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcutObject, null);
        }
        finally
        {
            if (shortcutObject != null && Marshal.IsComObject(shortcutObject))
            {
                _ = Marshal.FinalReleaseComObject(shortcutObject);
            }

            if (shellObject != null && Marshal.IsComObject(shellObject))
            {
                _ = Marshal.FinalReleaseComObject(shellObject);
            }
        }
    }
}
