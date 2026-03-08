using System.Diagnostics;
using System.ServiceProcess;
using KeepAliveService.Update;
using Microsoft.Win32;

namespace KeepAliveService.Setup;

public static class RestoreManager
{
    [ThreadStatic] private static int _failures;

    // ========================
    // BACKUP (called before setup applies changes)
    // ========================

    public static void BackupCurrentSettings(AppSettings settings)
    {
        if (settings.OriginalSettingsBackup is { Count: > 0 })
        {
            ConsoleOutput.Info("Original settings backup already exists. Skipping backup.");
            return;
        }

        Console.WriteLine();
        Console.WriteLine("=== Backing Up Original Settings ===");

        var backup = new Dictionary<string, string>();

        BackupPowerSettings(backup);
        BackupRegistrySettings(backup);
        BackupNetworkAdapterSettings(backup);

        settings.OriginalSettingsBackup = backup;
        settings.Save();

        ConsoleOutput.Success($"Backed up {backup.Count} original settings");
    }

    // ========================
    // RESTORE (reverts all changes)
    // ========================

    /// <summary>
    /// Restores all Windows settings to their original values, stops/removes the service
    /// and startup task, but leaves the program installed (EXE + desktop shortcut remain).
    /// </summary>
    public static int RunRestore()
    {
        _failures = 0;

        Console.WriteLine();
        Console.WriteLine("========================================");
        Console.WriteLine("  Windows Keep Alive - Restore Settings");
        Console.WriteLine("========================================");

        var settings = AppSettings.Load();
        var backup = settings.OriginalSettingsBackup;
        var hasBackup = backup is { Count: > 0 };

        if (!hasBackup)
        {
            ConsoleOutput.Warning("No original settings backup found. Will use Windows defaults where possible.");
            backup = new Dictionary<string, string>();
        }

        // Step 1: Stop the service FIRST (prevents ComplianceWatchdog re-apply)
        TryRun("Stop KeepAlive service", StopService);

        // Step 2: Remove the Windows service
        TryRun("Remove KeepAlive service", RemoveService);

        // Step 3: Restore power settings
        TryRun("Restore power settings", () => RestorePowerSettings(backup!));

        // Step 4: Restore registry settings
        TryRun("Restore registry settings", () => RestoreRegistrySettings(backup!));

        // Step 5: Restore network adapter settings
        TryRun("Restore network adapter settings", () => RestoreNetworkSettings(backup!));

        // Step 6: Remove startup scheduled task
        TryRun("Remove startup task", StartupTaskManager.RemoveTask);

        // Note: Desktop shortcut and installed EXE are intentionally kept.
        // The user can re-run setup later without re-downloading.

        // Step 7: Clear setup state (only if all previous steps succeeded,
        // so backup is preserved for retry on partial failure)
        if (_failures == 0)
        {
            TryRun("Clear setup state", () =>
            {
                settings.SetupCompletedUtc = null;
                settings.OriginalSettingsBackup = null;
                settings.Save();
                return true;
            });
        }
        else
        {
            ConsoleOutput.Warning("Skipping setup state cleanup: some steps failed. Backup preserved for retry.");
        }

        PrintRestoreSummary();
        return _failures > 0 ? 1 : 0;
    }

    /// <summary>
    /// Removes everything: service, startup task, desktop shortcut, installed EXE,
    /// and program data. This is the full uninstall.
    /// </summary>
    public static int RunUninstall()
    {
        _failures = 0;

        Console.WriteLine();
        Console.WriteLine("========================================");
        Console.WriteLine("  Windows Keep Alive - Full Uninstall");
        Console.WriteLine("========================================");

        var settings = AppSettings.Load();
        var backup = settings.OriginalSettingsBackup;
        var hasBackup = backup is { Count: > 0 };

        // If settings were never restored, restore them first
        if (hasBackup || settings.SetupCompletedUtc != null)
        {
            ConsoleOutput.Info("Restoring Windows settings before uninstall...");
            if (!hasBackup)
            {
                ConsoleOutput.Warning("No original settings backup found. Will use Windows defaults where possible.");
                backup = new Dictionary<string, string>();
            }

            TryRun("Stop KeepAlive service", StopService);
            TryRun("Remove KeepAlive service", RemoveService);
            TryRun("Restore power settings", () => RestorePowerSettings(backup!));
            TryRun("Restore registry settings", () => RestoreRegistrySettings(backup!));
            TryRun("Restore network adapter settings", () => RestoreNetworkSettings(backup!));
            TryRun("Remove startup task", StartupTaskManager.RemoveTask);
        }
        else
        {
            // No setup state, but still clean up service/task in case they exist
            TryRun("Stop KeepAlive service", StopService);
            TryRun("Remove KeepAlive service", RemoveService);
            TryRun("Remove startup task", StartupTaskManager.RemoveTask);
        }

        // Remove desktop shortcut
        TryRun("Remove desktop shortcut", () =>
        {
            InstallManager.RemoveDesktopShortcut();
            return true;
        });

        // Remove installed EXE and program directory
        TryRun("Remove installed program files", () =>
        {
            InstallManager.RemoveInstalledFiles();
            return true;
        });

        // Remove program data (settings, logs, tools)
        TryRun("Remove program data", () =>
        {
            RemoveProgramData();
            return true;
        });

        PrintUninstallSummary();
        return _failures > 0 ? 1 : 0;
    }

    private static void RemoveProgramData()
    {
        var programDataDir = AppSettings.ProgramDataDirectory;
        if (Directory.Exists(programDataDir))
        {
            try
            {
                Directory.Delete(programDataDir, recursive: true);
                ConsoleOutput.Success($"Removed {programDataDir}");
            }
            catch (Exception ex)
            {
                ConsoleOutput.Warning($"Could not fully remove {programDataDir}: {ex.Message}");
            }
        }
    }

    // =====================
    // BACKUP IMPLEMENTATION
    // =====================

    private static void BackupPowerSettings(Dictionary<string, string> backup)
    {
        // Sleep timeouts
        BackupPowerCfgValue(backup, "power.standby-timeout-ac",
            "238c9fa8-0aad-41ed-83f4-97be242c8f20", "29f6c1db-86da-48c5-9fdb-f2b67b1f44da", isAc: true);
        BackupPowerCfgValue(backup, "power.standby-timeout-dc",
            "238c9fa8-0aad-41ed-83f4-97be242c8f20", "29f6c1db-86da-48c5-9fdb-f2b67b1f44da", isAc: false);

        // Hibernate timeouts
        BackupPowerCfgValue(backup, "power.hibernate-timeout-ac",
            "238c9fa8-0aad-41ed-83f4-97be242c8f20", "9d7815a6-7ee4-497e-8888-515a05f02364", isAc: true);
        BackupPowerCfgValue(backup, "power.hibernate-timeout-dc",
            "238c9fa8-0aad-41ed-83f4-97be242c8f20", "9d7815a6-7ee4-497e-8888-515a05f02364", isAc: false);

        // Monitor timeout
        BackupPowerCfgValue(backup, "power.monitor-timeout-ac",
            "7516b95f-f776-4464-8c53-06167f40cc99", "3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e", isAc: true);
        BackupPowerCfgValue(backup, "power.monitor-timeout-dc",
            "7516b95f-f776-4464-8c53-06167f40cc99", "3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e", isAc: false);

        // Hibernate state
        BackupHibernateState(backup);

        // Lid close action
        BackupPowerCfgValue(backup, "power.lidaction-ac",
            "4f971e89-eebd-4455-a8de-9e59040e7347", "5ca83367-6e45-459f-a27b-476b1d01c936", isAc: true);
        BackupPowerCfgValue(backup, "power.lidaction-dc",
            "4f971e89-eebd-4455-a8de-9e59040e7347", "5ca83367-6e45-459f-a27b-476b1d01c936", isAc: false);

        // Hybrid sleep
        BackupPowerCfgValue(backup, "power.hybridsleep-ac",
            "238c9fa8-0aad-41ed-83f4-97be242c8f20", "94ac6d29-73ce-41a6-809f-6363ba21b47e", isAc: true);
        BackupPowerCfgValue(backup, "power.hybridsleep-dc",
            "238c9fa8-0aad-41ed-83f4-97be242c8f20", "94ac6d29-73ce-41a6-809f-6363ba21b47e", isAc: false);

        // Console lock on wake
        BackupPowerCfgValue(backup, "power.consolelock-ac",
            "fea3413e-7e05-4911-9a71-700331f1c294", "0e796bdb-100d-47d6-a2d5-f7d2daa51f51", isAc: true);
        BackupPowerCfgValue(backup, "power.consolelock-dc",
            "fea3413e-7e05-4911-9a71-700331f1c294", "0e796bdb-100d-47d6-a2d5-f7d2daa51f51", isAc: false);

        // WiFi power saving
        BackupPowerCfgValue(backup, "power.wifi-powersave-ac",
            "19cbb8fa-5279-450e-9fac-8a3d5fedd0c1", "12bbebe6-58d6-4636-95bb-3217ef867c1a", isAc: true);
        BackupPowerCfgValue(backup, "power.wifi-powersave-dc",
            "19cbb8fa-5279-450e-9fac-8a3d5fedd0c1", "12bbebe6-58d6-4636-95bb-3217ef867c1a", isAc: false);

        // USB selective suspend
        BackupPowerCfgValue(backup, "power.usb-suspend-ac",
            "2a737441-1930-4402-8d77-b2bebba308a3", "48e6b7a6-50f5-4782-a5d4-53bb8f07e226", isAc: true);
        BackupPowerCfgValue(backup, "power.usb-suspend-dc",
            "2a737441-1930-4402-8d77-b2bebba308a3", "48e6b7a6-50f5-4782-a5d4-53bb8f07e226", isAc: false);
    }

    private static void BackupPowerCfgValue(
        Dictionary<string, string> backup, string key,
        string subgroupGuid, string settingGuid, bool isAc)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powercfg.exe",
                Arguments = $"/query SCHEME_CURRENT {subgroupGuid} {settingGuid}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            var output = process?.StandardOutput.ReadToEnd() ?? "";
            process?.WaitForExit(10_000);

            var (acHex, dcHex) = Helpers.ParsePowerCfgSettingValues(output);
            var hexValue = isAc ? acHex : dcHex;
            if (hexValue != null)
            {
                backup[key] = hexValue;
            }
        }
        catch
        {
            // Could not read - will use defaults during restore.
        }
    }

    private static void BackupHibernateState(Dictionary<string, string> backup)
    {
        try
        {
            var systemDrive = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (systemDrive.Length >= 3)
            {
                var hiberfilePath = Path.Combine(systemDrive[..3], "hiberfil.sys");
                backup["power.hibernate-enabled"] = File.Exists(hiberfilePath) ? "1" : "0";
                return;
            }
        }
        catch
        {
            // Fall through to default.
        }

        // Conservative default: assume was enabled.
        backup["power.hibernate-enabled"] = "1";
    }

    private static void BackupRegistrySettings(Dictionary<string, string> backup)
    {
        // Winlogon
        var winlogon = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
        BackupRegistryValue(backup, "registry.Winlogon.AutoAdminLogon", Registry.LocalMachine, winlogon, "AutoAdminLogon");
        BackupRegistryValue(backup, "registry.Winlogon.ForceAutoLogon", Registry.LocalMachine, winlogon, "ForceAutoLogon");
        BackupRegistryValue(backup, "registry.Winlogon.DefaultUserName", Registry.LocalMachine, winlogon, "DefaultUserName");
        BackupRegistryValue(backup, "registry.Winlogon.DefaultDomainName", Registry.LocalMachine, winlogon, "DefaultDomainName");
        BackupRegistryValue(backup, "registry.Winlogon.DisableLockWorkstation", Registry.LocalMachine, winlogon, "DisableLockWorkstation");

        // PasswordLess
        BackupRegistryValue(backup, "registry.PasswordLess.DevicePasswordLessBuildVersion",
            Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\PasswordLess\Device", "DevicePasswordLessBuildVersion");

        // ARSO + login policy + LegalNotice (setup clears LegalNotice from this hive)
        var systemPolicy = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
        BackupRegistryValue(backup, "registry.System.LegalNoticeCaption",
            Registry.LocalMachine, systemPolicy, "LegalNoticeCaption");
        BackupRegistryValue(backup, "registry.System.LegalNoticeText",
            Registry.LocalMachine, systemPolicy, "LegalNoticeText");
        BackupRegistryValue(backup, "registry.System.DisableAutomaticRestartSignOn",
            Registry.LocalMachine, systemPolicy, "DisableAutomaticRestartSignOn");
        BackupRegistryValue(backup, "registry.System.DontDisplayLastUserName",
            Registry.LocalMachine, systemPolicy, "DontDisplayLastUserName");

        // Lock screen
        BackupRegistryValue(backup, "registry.Personalization.NoLockScreen",
            Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Personalization", "NoLockScreen");

        // Screen saver
        var desktop = @"SOFTWARE\Policies\Microsoft\Windows\Control Panel\Desktop";
        BackupRegistryValue(backup, "registry.Desktop.ScreenSaverIsSecure", Registry.LocalMachine, desktop, "ScreenSaverIsSecure");
        BackupRegistryValue(backup, "registry.Desktop.ScreenSaveActive", Registry.LocalMachine, desktop, "ScreenSaveActive");
        BackupRegistryValue(backup, "registry.Desktop.ScreenSaveTimeOut", Registry.LocalMachine, desktop, "ScreenSaveTimeOut");
        BackupRegistryValue(backup, "registry.Desktop.SCRNSAVE.EXE", Registry.LocalMachine, desktop, "SCRNSAVE.EXE");

        // Credential Guard (mutated by preflight)
        BackupRegistryValue(backup, "registry.DeviceGuard.EnableVirtualizationBasedSecurity",
            Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity");
        BackupRegistryValue(backup, "registry.Lsa.LsaCfgFlags",
            Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Lsa", "LsaCfgFlags");

        // Update policy
        var auPath = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU";
        BackupRegistryValue(backup, "registry.AU.NoAutoRebootWithLoggedOnUsers", Registry.LocalMachine, auPath, "NoAutoRebootWithLoggedOnUsers");
        BackupRegistryValue(backup, "registry.AU.AUOptions", Registry.LocalMachine, auPath, "AUOptions");

        // Update UX
        var uxPath = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
        BackupRegistryValue(backup, "registry.UX.ActiveHoursStart", Registry.LocalMachine, uxPath, "ActiveHoursStart");
        BackupRegistryValue(backup, "registry.UX.ActiveHoursEnd", Registry.LocalMachine, uxPath, "ActiveHoursEnd");
        BackupRegistryValue(backup, "registry.UX.IsActiveHoursEnabled", Registry.LocalMachine, uxPath, "IsActiveHoursEnabled");
    }

    private static void BackupRegistryValue(
        Dictionary<string, string> backup, string key,
        RegistryKey root, string path, string valueName)
    {
        try
        {
            using var regKey = root.OpenSubKey(path);
            var value = regKey?.GetValue(valueName);

            if (value == null)
            {
                backup[key] = "<not-set>";
            }
            else if (value is int intVal)
            {
                backup[key] = $"dword:{intVal}";
            }
            else
            {
                backup[key] = $"string:{value}";
            }
        }
        catch
        {
            backup[key] = "<error>";
        }
    }

    private static void BackupNetworkAdapterSettings(Dictionary<string, string> backup)
    {
        try
        {
            using var networkKey = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}");
            if (networkKey == null) return;

            foreach (var subKeyName in networkKey.GetSubKeyNames())
            {
                if (!int.TryParse(subKeyName, out _)) continue;

                using var adapterKey = networkKey.OpenSubKey(subKeyName);
                if (adapterKey == null) continue;

                var driverDesc = adapterKey.GetValue("DriverDesc") as string ?? "";
                var componentId = adapterKey.GetValue("ComponentId") as string ?? "";

                if (NetworkConfigurator.IsVirtualAdapter(driverDesc, componentId)) continue;

                var pnpCap = adapterKey.GetValue("PnPCapabilities");
                var backupKey = $"network.adapter.{subKeyName}.PnPCapabilities";
                backup[backupKey] = pnpCap is int pnpVal ? $"dword:{pnpVal}" : "<not-set>";
            }
        }
        catch
        {
            // Best effort only.
        }
    }

    // ========================
    // RESTORE IMPLEMENTATION
    // ========================

    private static bool StopService()
    {
        try
        {
            using var sc = new ServiceController("KeepAliveService");
            if (sc.Status == ServiceControllerStatus.Running)
            {
                ConsoleOutput.Info("Stopping KeepAlive service...");
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                ConsoleOutput.Success("Service stopped");
            }

            return true;
        }
        catch (InvalidOperationException)
        {
            ConsoleOutput.Info("Service not found (may not be installed)");
            return true;
        }
        catch (Exception ex)
        {
            ConsoleOutput.Warning($"Error stopping service: {ex.Message}");
            return false;
        }
    }

    private static bool RemoveService()
    {
        try
        {
            return ServiceInstaller.Uninstall();
        }
        catch (Exception ex)
        {
            ConsoleOutput.Error($"Service removal failed: {ex.Message}");
            return false;
        }
    }

    private static bool RestorePowerSettings(Dictionary<string, string> backup)
    {
        Console.WriteLine();
        Console.WriteLine("=== Restoring Power Settings ===");
        var ok = true;

        // Sleep timeouts
        ok &= RestorePowerCfgTimeout(backup, "power.standby-timeout-ac",
            "/change standby-timeout-ac", "Sleep timeout (AC)", defaultMinutes: 30);
        ok &= RestorePowerCfgTimeout(backup, "power.standby-timeout-dc",
            "/change standby-timeout-dc", "Sleep timeout (DC)", defaultMinutes: 15);

        // Hibernate timeouts
        ok &= RestorePowerCfgTimeout(backup, "power.hibernate-timeout-ac",
            "/change hibernate-timeout-ac", "Hibernate timeout (AC)", defaultMinutes: 180);
        ok &= RestorePowerCfgTimeout(backup, "power.hibernate-timeout-dc",
            "/change hibernate-timeout-dc", "Hibernate timeout (DC)", defaultMinutes: 180);

        // Monitor timeouts
        ok &= RestorePowerCfgTimeout(backup, "power.monitor-timeout-ac",
            "/change monitor-timeout-ac", "Monitor timeout (AC)", defaultMinutes: 15);
        ok &= RestorePowerCfgTimeout(backup, "power.monitor-timeout-dc",
            "/change monitor-timeout-dc", "Monitor timeout (DC)", defaultMinutes: 5);

        // Hibernate on/off
        var hibernateDefault = !backup.TryGetValue("power.hibernate-enabled", out var hibState) || hibState == "1";
        if (hibernateDefault)
        {
            RunPowerCfg("/hibernate on", "Hibernation -> Enabled");
        }

        // Lid close action (default: 1 = Sleep)
        ok &= RestorePowerCfgIndex(backup, "power.lidaction-ac",
            "/setacvalueindex SCHEME_CURRENT SUB_BUTTONS LIDACTION", "Lid close (AC)", defaultValue: 1);
        ok &= RestorePowerCfgIndex(backup, "power.lidaction-dc",
            "/setdcvalueindex SCHEME_CURRENT SUB_BUTTONS LIDACTION", "Lid close (DC)", defaultValue: 1);

        // Hybrid sleep (default: 1 = Enabled)
        ok &= RestorePowerCfgIndex(backup, "power.hybridsleep-ac",
            "/setacvalueindex SCHEME_CURRENT SUB_SLEEP HYBRIDSLEEP", "Hybrid sleep (AC)", defaultValue: 1);
        ok &= RestorePowerCfgIndex(backup, "power.hybridsleep-dc",
            "/setdcvalueindex SCHEME_CURRENT SUB_SLEEP HYBRIDSLEEP", "Hybrid sleep (DC)", defaultValue: 1);

        // Console lock on wake (default: 1 = Enabled)
        ok &= RestorePowerCfgIndex(backup, "power.consolelock-ac",
            "/setacvalueindex SCHEME_CURRENT SUB_NONE CONSOLELOCK", "Sign-in after sleep (AC)", defaultValue: 1);
        ok &= RestorePowerCfgIndex(backup, "power.consolelock-dc",
            "/setdcvalueindex SCHEME_CURRENT SUB_NONE CONSOLELOCK", "Sign-in after sleep (DC)", defaultValue: 1);

        // WiFi power saving (default: 3 = Medium Power Saving)
        ok &= RestorePowerCfgIndex(backup, "power.wifi-powersave-ac",
            "/setacvalueindex SCHEME_CURRENT 19cbb8fa-5279-450e-9fac-8a3d5fedd0c1 12bbebe6-58d6-4636-95bb-3217ef867c1a",
            "WiFi power saving (AC)", defaultValue: 3);
        ok &= RestorePowerCfgIndex(backup, "power.wifi-powersave-dc",
            "/setdcvalueindex SCHEME_CURRENT 19cbb8fa-5279-450e-9fac-8a3d5fedd0c1 12bbebe6-58d6-4636-95bb-3217ef867c1a",
            "WiFi power saving (DC)", defaultValue: 3);

        // USB selective suspend (default: 1 = Enabled)
        ok &= RestorePowerCfgIndex(backup, "power.usb-suspend-ac",
            "/setacvalueindex SCHEME_CURRENT 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226",
            "USB selective suspend (AC)", defaultValue: 1);
        ok &= RestorePowerCfgIndex(backup, "power.usb-suspend-dc",
            "/setdcvalueindex SCHEME_CURRENT 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226",
            "USB selective suspend (DC)", defaultValue: 1);

        return ok;
    }

    private static bool RestoreRegistrySettings(Dictionary<string, string> backup)
    {
        Console.WriteLine();
        Console.WriteLine("=== Restoring Registry Settings ===");
        var ok = true;

        // Auto-login keys are intentionally DISABLED rather than restored from backup.
        // Restoring AutoAdminLogon=1 without matching credential keys would leave the
        // machine in a broken login state. Credential keys (DefaultPassword) should
        // never be restored from backup for security reasons.
        var winlogon = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
        ok &= SetRegistryValue(Registry.LocalMachine, winlogon, "AutoAdminLogon", "0",
            RegistryValueKind.String, "AutoAdminLogon -> 0 (disabled)");
        ok &= SetRegistryValue(Registry.LocalMachine, winlogon, "ForceAutoLogon", "0",
            RegistryValueKind.String, "ForceAutoLogon -> 0 (disabled)");
        ok &= DeleteRegistryValue(Registry.LocalMachine, winlogon, "DefaultUserName", "DefaultUserName -> Removed");
        ok &= DeleteRegistryValue(Registry.LocalMachine, winlogon, "DefaultPassword", "DefaultPassword -> Removed");
        ok &= DeleteRegistryValue(Registry.LocalMachine, winlogon, "DefaultDomainName", "DefaultDomainName -> Removed");

        // Restore DisableLockWorkstation
        ok &= RestoreRegistryFromBackup(backup, "registry.Winlogon.DisableLockWorkstation",
            Registry.LocalMachine, winlogon, "DisableLockWorkstation", "DisableLockWorkstation");

        // Restore PasswordLess
        ok &= RestoreRegistryFromBackup(backup, "registry.PasswordLess.DevicePasswordLessBuildVersion",
            Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\PasswordLess\Device",
            "DevicePasswordLessBuildVersion", "DevicePasswordLessBuildVersion");

        // Restore ARSO + login policy + LegalNotice
        var systemPolicy = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";

        // Restore LegalNotice to Policies\System (where setup clears it).
        // Fall back to old backup key for existing backups created before hive fix.
        var legalCaptionKey = backup.ContainsKey("registry.System.LegalNoticeCaption")
            ? "registry.System.LegalNoticeCaption"
            : "registry.Winlogon.LegalNoticeCaption";
        var legalTextKey = backup.ContainsKey("registry.System.LegalNoticeText")
            ? "registry.System.LegalNoticeText"
            : "registry.Winlogon.LegalNoticeText";
        ok &= RestoreRegistryFromBackup(backup, legalCaptionKey,
            Registry.LocalMachine, systemPolicy, "LegalNoticeCaption", "LegalNoticeCaption");
        ok &= RestoreRegistryFromBackup(backup, legalTextKey,
            Registry.LocalMachine, systemPolicy, "LegalNoticeText", "LegalNoticeText");
        ok &= RestoreRegistryFromBackup(backup, "registry.System.DisableAutomaticRestartSignOn",
            Registry.LocalMachine, systemPolicy,
            "DisableAutomaticRestartSignOn", "DisableAutomaticRestartSignOn");
        ok &= RestoreRegistryFromBackup(backup, "registry.System.DontDisplayLastUserName",
            Registry.LocalMachine, systemPolicy,
            "DontDisplayLastUserName", "DontDisplayLastUserName");

        // Restore Credential Guard
        ok &= RestoreRegistryFromBackup(backup, "registry.DeviceGuard.EnableVirtualizationBasedSecurity",
            Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\DeviceGuard",
            "EnableVirtualizationBasedSecurity", "EnableVirtualizationBasedSecurity");
        ok &= RestoreRegistryFromBackup(backup, "registry.Lsa.LsaCfgFlags",
            Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Lsa",
            "LsaCfgFlags", "LsaCfgFlags");

        // Restore lock screen
        ok &= RestoreRegistryFromBackup(backup, "registry.Personalization.NoLockScreen",
            Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Personalization",
            "NoLockScreen", "NoLockScreen");

        // Restore screen saver settings
        var desktop = @"SOFTWARE\Policies\Microsoft\Windows\Control Panel\Desktop";
        ok &= RestoreRegistryFromBackup(backup, "registry.Desktop.ScreenSaverIsSecure",
            Registry.LocalMachine, desktop, "ScreenSaverIsSecure", "ScreenSaverIsSecure");
        ok &= RestoreRegistryFromBackup(backup, "registry.Desktop.ScreenSaveActive",
            Registry.LocalMachine, desktop, "ScreenSaveActive", "ScreenSaveActive");
        ok &= RestoreRegistryFromBackup(backup, "registry.Desktop.ScreenSaveTimeOut",
            Registry.LocalMachine, desktop, "ScreenSaveTimeOut", "ScreenSaveTimeOut");
        ok &= RestoreRegistryFromBackup(backup, "registry.Desktop.SCRNSAVE.EXE",
            Registry.LocalMachine, desktop, "SCRNSAVE.EXE", "SCRNSAVE.EXE");

        // Restore update policy
        var auPath = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU";
        ok &= RestoreRegistryFromBackup(backup, "registry.AU.NoAutoRebootWithLoggedOnUsers",
            Registry.LocalMachine, auPath, "NoAutoRebootWithLoggedOnUsers", "NoAutoRebootWithLoggedOnUsers");
        ok &= RestoreRegistryFromBackup(backup, "registry.AU.AUOptions",
            Registry.LocalMachine, auPath, "AUOptions", "AUOptions");

        // Restore Update UX settings
        var uxPath = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
        ok &= RestoreRegistryFromBackup(backup, "registry.UX.ActiveHoursStart",
            Registry.LocalMachine, uxPath, "ActiveHoursStart", "ActiveHoursStart");
        ok &= RestoreRegistryFromBackup(backup, "registry.UX.ActiveHoursEnd",
            Registry.LocalMachine, uxPath, "ActiveHoursEnd", "ActiveHoursEnd");
        ok &= RestoreRegistryFromBackup(backup, "registry.UX.IsActiveHoursEnabled",
            Registry.LocalMachine, uxPath, "IsActiveHoursEnabled", "IsActiveHoursEnabled");

        return ok;
    }

    private static bool RestoreNetworkSettings(Dictionary<string, string> backup)
    {
        Console.WriteLine();
        Console.WriteLine("=== Restoring Network Adapter Settings ===");

        try
        {
            using var networkKey = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}");
            if (networkKey == null)
            {
                ConsoleOutput.Warning("Could not open network adapter registry key");
                return true;
            }

            foreach (var subKeyName in networkKey.GetSubKeyNames())
            {
                if (!int.TryParse(subKeyName, out _)) continue;

                var backupKey = $"network.adapter.{subKeyName}.PnPCapabilities";
                if (!backup.ContainsKey(backupKey)) continue;

                using var adapterKey = networkKey.OpenSubKey(subKeyName, writable: true);
                if (adapterKey == null) continue;

                var driverDesc = adapterKey.GetValue("DriverDesc") as string ?? subKeyName;
                var stored = backup[backupKey];

                if (stored == "<not-set>")
                {
                    adapterKey.DeleteValue("PnPCapabilities", throwOnMissingValue: false);
                    ConsoleOutput.Success($"PnPCapabilities removed for: {driverDesc}");
                }
                else if (stored.StartsWith("dword:") && int.TryParse(stored[6..], out var val))
                {
                    adapterKey.SetValue("PnPCapabilities", val, RegistryValueKind.DWord);
                    ConsoleOutput.Success($"PnPCapabilities restored to {val} for: {driverDesc}");
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            ConsoleOutput.Error($"Network adapter restore failed: {ex.Message}");
            return false;
        }
    }

    // ========================
    // HELPER METHODS
    // ========================

    private static void TryRun(string stepName, Func<bool> action)
    {
        try
        {
            if (!action())
            {
                _failures++;
            }
        }
        catch (Exception ex)
        {
            ConsoleOutput.Error($"{stepName} failed: {ex.Message}");
            _failures++;
        }
    }

    private static bool RestorePowerCfgTimeout(
        Dictionary<string, string> backup, string key,
        string command, string description, int defaultMinutes)
    {
        var minutes = defaultMinutes;
        if (backup.TryGetValue(key, out var hexValue) && TryParseHexToInt(hexValue, out var seconds))
        {
            minutes = seconds / 60;
        }

        return RunPowerCfg($"{command} {minutes}", $"{description} -> {minutes} min");
    }

    private static bool RestorePowerCfgIndex(
        Dictionary<string, string> backup, string key,
        string commandPrefix, string description, int defaultValue)
    {
        var value = defaultValue;
        if (backup.TryGetValue(key, out var hexValue) && TryParseHexToInt(hexValue, out var parsed))
        {
            value = parsed;
        }

        return RunPowerCfg($"{commandPrefix} {value}", $"{description} -> {value}");
    }

    private static bool RestoreRegistryFromBackup(
        Dictionary<string, string> backup, string key,
        RegistryKey root, string path, string valueName, string description)
    {
        if (!backup.TryGetValue(key, out var stored) || stored == "<error>")
        {
            return DeleteRegistryValue(root, path, valueName, $"{description} -> Removed (no backup)");
        }

        if (stored == "<not-set>")
        {
            return DeleteRegistryValue(root, path, valueName, $"{description} -> Removed (was not set)");
        }

        if (stored.StartsWith("dword:") && int.TryParse(stored[6..], out var intVal))
        {
            return SetRegistryValue(root, path, valueName, intVal, RegistryValueKind.DWord, $"{description} -> {intVal}");
        }

        if (stored.StartsWith("string:"))
        {
            var strVal = stored[7..];
            return SetRegistryValue(root, path, valueName, strVal, RegistryValueKind.String, $"{description} -> \"{strVal}\"");
        }

        return DeleteRegistryValue(root, path, valueName, $"{description} -> Removed (unparseable backup)");
    }

    private static bool RunPowerCfg(string arguments, string description)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powercfg.exe",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            process?.WaitForExit(10_000);

            if (process?.ExitCode == 0)
            {
                ConsoleOutput.Success(description);
                return true;
            }

            ConsoleOutput.Warning($"{description} - powercfg exit code {process?.ExitCode}");
            return false;
        }
        catch (Exception ex)
        {
            ConsoleOutput.Error($"{description} - {ex.Message}");
            return false;
        }
    }

    private static bool SetRegistryValue(
        RegistryKey root, string path, string name,
        object value, RegistryValueKind kind, string description)
    {
        try
        {
            using var key = root.CreateSubKey(path, writable: true);
            key?.SetValue(name, value, kind);
            ConsoleOutput.Success(description);
            return true;
        }
        catch (Exception ex)
        {
            ConsoleOutput.Error($"{description} - {ex.Message}");
            return false;
        }
    }

    private static bool DeleteRegistryValue(
        RegistryKey root, string path, string name, string description)
    {
        try
        {
            using var key = root.OpenSubKey(path, writable: true);
            if (key?.GetValue(name) != null)
            {
                key.DeleteValue(name, throwOnMissingValue: false);
            }

            ConsoleOutput.Success(description);
            return true;
        }
        catch (Exception ex)
        {
            ConsoleOutput.Warning($"{description} - {ex.Message}");
            return false;
        }
    }

    private static bool TryParseHexToInt(string hexString, out int result)
    {
        result = 0;
        var trimmed = hexString.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..];
        }

        return int.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber, null, out result);
    }

    private static void PrintRestoreSummary()
    {
        Console.WriteLine();
        Console.WriteLine("========================================");

        if (_failures > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  Restore completed with {_failures} failure(s)");
            Console.ResetColor();
            Console.WriteLine("========================================");
            Console.WriteLine();
            Console.WriteLine("  Some steps failed. Review the output above.");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  Restore Complete!");
            Console.ResetColor();
            Console.WriteLine("========================================");
            Console.WriteLine();
            Console.WriteLine("  All Windows settings have been reverted.");
            Console.WriteLine("  The program is still installed — you can re-run setup at any time.");
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  IMPORTANT: Restart your PC for all changes to take full effect.");
        Console.ResetColor();
        Console.WriteLine();
    }

    private static void PrintUninstallSummary()
    {
        Console.WriteLine();
        Console.WriteLine("========================================");

        if (_failures > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  Uninstall completed with {_failures} failure(s)");
            Console.ResetColor();
            Console.WriteLine("========================================");
            Console.WriteLine();
            Console.WriteLine("  Some steps failed. Review the output above.");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  Uninstall Complete!");
            Console.ResetColor();
            Console.WriteLine("========================================");
            Console.WriteLine();
            Console.WriteLine("  All settings restored and program removed.");
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  IMPORTANT: Restart your PC for all changes to take full effect.");
        Console.ResetColor();
        Console.WriteLine();
    }

}
