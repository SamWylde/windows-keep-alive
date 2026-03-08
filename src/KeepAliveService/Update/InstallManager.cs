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
        try
        {
            File.Copy(currentPath, CanonicalExePath, overwrite: true);
        }
        catch (IOException)
        {
            // The exe may be locked by the running service or an existing GUI instance.
            // Stop/kill both before retrying.
            try
            {
                using var sc = new System.ServiceProcess.ServiceController("KeepAliveService");
                if (sc.Status != System.ServiceProcess.ServiceControllerStatus.Stopped)
                {
                    sc.Stop();
                    sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                }
            }
            catch
            {
                // Service may not be installed yet.
            }

            KillExistingGuiInstances();
            Thread.Sleep(1000);
            File.Copy(currentPath, CanonicalExePath, overwrite: true);
        }
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

    private static void KillExistingGuiInstances()
    {
        try
        {
            var currentPid = Environment.ProcessId;
            foreach (var proc in Process.GetProcessesByName("KeepAliveService"))
            {
                try
                {
                    if (proc.Id == currentPid)
                        continue;
                    proc.Kill();
                    proc.WaitForExit(5000);
                }
                catch
                {
                    // Best effort — process may have already exited.
                }
                finally
                {
                    proc.Dispose();
                }
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
        // Remove from both public and per-user desktops (older installs used per-user).
        RemoveShortcutFrom(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
        RemoveShortcutFrom(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
    }

    private static void RemoveShortcutFrom(string? desktopPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(desktopPath)) return;
            var shortcutPath = Path.Combine(desktopPath, "Windows Keep Alive.lnk");
            if (File.Exists(shortcutPath))
                File.Delete(shortcutPath);
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
            // Use public (all-users) desktop so the shortcut is available regardless of
            // which admin account runs setup, avoiding mismatch with the autologon user.
            var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
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
