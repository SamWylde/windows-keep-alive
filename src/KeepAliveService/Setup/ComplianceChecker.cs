using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Win32;

namespace KeepAliveService.Setup;

public static class ComplianceChecker
{
    private static int _passCount;
    private static int _failCount;
    private static int _warnCount;
    private static bool _isHomeOrCore;
    private static bool _isWindows10;
    private static bool _isWindows11;

    public static int RunCheck()
    {
        _passCount = 0;
        _failCount = 0;
        _warnCount = 0;
        _isHomeOrCore = false;
        _isWindows10 = false;
        _isWindows11 = false;

        Console.WriteLine();
        Console.WriteLine("========================================");
        Console.WriteLine("  KeepAlive Compliance Check");
        Console.WriteLine("========================================");

        CheckWindowsEdition();
        CheckUpdatePolicy();
        CheckAutoLogin();
        CheckArso();
        CheckLockScreen();
        CheckPowerSettings();
        CheckNetworkSettings();
        CheckService();
        CheckTeamViewer();

        Console.WriteLine();
        Console.WriteLine("========================================");
        Console.ForegroundColor = _failCount == 0 ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"  Results: {_passCount} passed, {_failCount} failed, {_warnCount} warnings");
        Console.ResetColor();
        Console.WriteLine("========================================");
        Console.WriteLine();

        return _failCount == 0 ? 0 : 1;
    }

    private static void CheckWindowsEdition()
    {
        Console.WriteLine();
        Console.WriteLine("--- System ---");

        if (!WindowsEditionHelper.TryGetWindowsEditionInfo(out var info, out _)
            || info == null)
        {
            Fail("Could not determine Windows edition");
            return;
        }

        if (!info.IsSupportedOsFamily)
        {
            Fail($"Windows product: {info.ProductName} - requires Windows 10 or Windows 11");
            return;
        }

        if (!info.SupportsBaseline)
        {
            Fail($"Windows build: {info.BuildNumber} - requires Windows 10 build {WindowsEditionHelper.MinSupportedBuild}+ or Windows 11");
            return;
        }

        _isWindows10 = info.IsWindows10;
        _isWindows11 = info.IsWindows11;
        _isHomeOrCore = info.IsHomeOrCore;
        Pass($"Windows Edition: {info.EditionId} ({info.ProductName}, Build {info.BuildNumber})");
        if (_isHomeOrCore)
        {
            Warn("Windows Home/Core detected: policy registry checks may pass even when OS enforcement differs.");
        }
    }

    private static void CheckUpdatePolicy()
    {
        Console.WriteLine();
        Console.WriteLine("--- Windows Update Policy ---");

        if (_isHomeOrCore)
        {
            Warn("Home/Core note: NoAutoRebootWithLoggedOnUsers is a policy key and may not be strictly enforced.");
        }

        CheckRegistryDword(Registry.LocalMachine,
            @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU",
            "NoAutoRebootWithLoggedOnUsers", 1,
            "No auto-reboot with logged-on users");

        CheckRegistryDword(Registry.LocalMachine,
            @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU",
            "AUOptions", 4,
            "Auto-download, schedule install");

        CheckRegistryDword(Registry.LocalMachine,
            @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings",
            "IsActiveHoursEnabled", 1,
            "Active Hours enabled");

        CheckRegistryDword(Registry.LocalMachine,
            @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings",
            "ActiveHoursStart", 0,
            "Active Hours start");

        CheckRegistryDword(Registry.LocalMachine,
            @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings",
            "ActiveHoursEnd", 18,
            "Active Hours end");
    }

    private static void CheckAutoLogin()
    {
        Console.WriteLine();
        Console.WriteLine("--- Auto-Login ---");

        var winlogonPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";

        // Check AutoAdminLogon
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(winlogonPath);
            var autoAdmin = key?.GetValue("AutoAdminLogon") as string;
            var defaultUser = key?.GetValue("DefaultUserName") as string;

            if (autoAdmin == "1")
                Pass($"AutoAdminLogon = 1");
            else
                Fail($"AutoAdminLogon = {autoAdmin ?? "(not set)"} (expected 1)");

            if (!string.IsNullOrEmpty(defaultUser))
                Pass($"DefaultUserName = {defaultUser}");
            else
                Fail("DefaultUserName is not set");
        }
        catch
        {
            Fail("Could not read Winlogon registry");
        }

        // Check ForceAutoLogon
        CheckRegistryString(Registry.LocalMachine, winlogonPath,
            "ForceAutoLogon", "1", "ForceAutoLogon");

        // Check Windows Hello passwordless requirement in a capability-aware way.
        CheckPasswordlessRequirement();

        // Check for blockers
        CheckAutoLoginBlockers();
    }

    private static void CheckPasswordlessRequirement()
    {
        const string passwordlessPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\PasswordLess\Device";
        const string valueName = "DevicePasswordLessBuildVersion";

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(passwordlessPath);
            if (key == null)
            {
                var osLabel = _isWindows10 ? "Windows 10" : _isWindows11 ? "Windows 11" : "this Windows version";
                Pass($"Windows Hello passwordless requirement key not present ({osLabel}); treating as not applicable.");
                return;
            }

            var value = key.GetValue(valueName);
            if (value is int intValue)
            {
                if (intValue == 0)
                {
                    Pass("Windows Hello passwordless requirement disabled");
                }
                else if (_isWindows11)
                {
                    Fail($"Windows Hello passwordless requirement = {intValue} (expected 0; auto-login will not work on Windows 11)");
                }
                else
                {
                    Warn($"Windows Hello passwordless requirement = {intValue} (expected 0 for best auto-login reliability)");
                }

                return;
            }

            if (value == null)
            {
                Warn("Windows Hello passwordless requirement value is not set; behavior may vary by build/edition.");
                return;
            }

            Warn($"Windows Hello passwordless requirement value has unexpected type: {value.GetType().Name}");
        }
        catch
        {
            Warn("Could not read Windows Hello passwordless requirement key; continuing.");
        }
    }

    private static void CheckAutoLoginBlockers()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");

            var legalNotice = key?.GetValue("LegalNoticeText") as string;
            var legalCaption = key?.GetValue("LegalNoticeCaption") as string;

            if (!string.IsNullOrWhiteSpace(legalNotice) || !string.IsNullOrWhiteSpace(legalCaption))
            {
                Fail("LegalNoticeText/LegalNoticeCaption is set - this blocks auto-login (user must click OK)");
            }
            else
            {
                Pass("No LegalNotice blocker");
            }

            var dontDisplayLast = key?.GetValue("DontDisplayLastUserName");
            if (dontDisplayLast is int dontDisplay && dontDisplay == 1)
            {
                Warn("DontDisplayLastUserName = 1 - may interfere with auto-login");
            }
        }
        catch
        {
            // Key may not exist - that's fine
        }
    }

    private static void CheckArso()
    {
        Console.WriteLine();
        Console.WriteLine("--- ARSO (Automatic Restart Sign-On) ---");

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
            var value = key?.GetValue("DisableAutomaticRestartSignOn");

            if (value == null || (value is int intVal && intVal == 0))
                Pass("ARSO enabled (DisableAutomaticRestartSignOn = 0 or not set)");
            else
                Fail($"ARSO disabled (DisableAutomaticRestartSignOn = {value})");
        }
        catch
        {
            Pass("ARSO enabled (default - no override policy found)");
        }
    }

    private static void CheckLockScreen()
    {
        Console.WriteLine();
        Console.WriteLine("--- Lock Screen ---");

        if (_isHomeOrCore)
        {
            Warn("Home/Core note: lock-screen and screen-saver policy keys may not be strictly enforced.");
        }

        CheckRegistryDword(Registry.LocalMachine,
            @"SOFTWARE\Policies\Microsoft\Windows\Personalization",
            "NoLockScreen", 1, "Lock screen disabled");

        CheckRegistryDword(Registry.LocalMachine,
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon",
            "DisableLockWorkstation", 1, "Workstation lock disabled");

        CheckRegistryString(Registry.LocalMachine,
            @"SOFTWARE\Policies\Microsoft\Windows\Control Panel\Desktop",
            "ScreenSaverIsSecure", "0", "Screen saver password disabled (machine policy)");

        CheckRegistryString(Registry.LocalMachine,
            @"SOFTWARE\Policies\Microsoft\Windows\Control Panel\Desktop",
            "ScreenSaveActive", "0", "Screen saver disabled (machine policy)");

        CheckRegistryString(Registry.LocalMachine,
            @"SOFTWARE\Policies\Microsoft\Windows\Control Panel\Desktop",
            "ScreenSaveTimeOut", "0", "Screen saver timeout disabled (machine policy)");

        CheckRegistryString(Registry.LocalMachine,
            @"SOFTWARE\Policies\Microsoft\Windows\Control Panel\Desktop",
            "SCRNSAVE.EXE", string.Empty, "Screen saver executable cleared (machine policy)");
    }

    private static void CheckPowerSettings()
    {
        Console.WriteLine();
        Console.WriteLine("--- Power Settings ---");

        // SUB_SLEEP GUID = 238c9fa8-0aad-41ed-83f4-97be242c8f20
        // STANDBYIDLE GUID = 29f6c1db-86da-48c5-9fdb-f2b67b1f44da
        CheckPowerCfgBothAcDc("Sleep timeout", "238c9fa8-0aad-41ed-83f4-97be242c8f20", "29f6c1db-86da-48c5-9fdb-f2b67b1f44da", 0, "Never");

        // Check hibernate
        CheckHibernateDisabled();

        // Lid close action
        // SUB_BUTTONS GUID = 4f971e89-eebd-4455-a8de-9e59040e7347
        // LIDACTION GUID = 5ca83367-6e45-459f-a27b-476b1d01c936
        CheckLidCloseAction();

        // Hybrid sleep
        // HYBRIDSLEEP GUID = 94ac6d29-73ce-41a6-809f-6363ba21b47e
        CheckPowerCfgBothAcDc("Hybrid sleep", "238c9fa8-0aad-41ed-83f4-97be242c8f20", "94ac6d29-73ce-41a6-809f-6363ba21b47e", 0, "Disabled");

        // Monitor timeout
        // SUB_VIDEO GUID = 7516b95f-f776-4464-8c53-06167f40cc99
        // VIDEOIDLE GUID = 3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e
        CheckPowerCfgBothAcDc("Monitor timeout", "7516b95f-f776-4464-8c53-06167f40cc99", "3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e", 0, "Never");

        // Console lock on wake (require sign-in after sleep)
        // SUB_NONE GUID = fea3413e-7e05-4911-9a71-700331f1c294
        // CONSOLELOCK GUID = 0e796bdb-100d-47d6-a2d5-f7d2daa51f51
        CheckPowerCfgBothAcDc("Console lock on wake", "fea3413e-7e05-4911-9a71-700331f1c294", "0e796bdb-100d-47d6-a2d5-f7d2daa51f51", 0, "Disabled");
    }

    private static void CheckPowerCfgBothAcDc(string name, string subgroupGuid, string settingGuid, int expectedValue, string friendlyValue)
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

            var expectedHex = $"0x{expectedValue:x8}";
            var lines = output.Split('\n');
            var acOk = false;
            var dcOk = false;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.Contains("Current AC Power Setting Index", StringComparison.OrdinalIgnoreCase))
                {
                    acOk = trimmed.Contains(expectedHex, StringComparison.OrdinalIgnoreCase);
                }
                else if (trimmed.Contains("Current DC Power Setting Index", StringComparison.OrdinalIgnoreCase))
                {
                    dcOk = trimmed.Contains(expectedHex, StringComparison.OrdinalIgnoreCase);
                }
            }

            if (acOk && dcOk)
                Pass($"{name}: {friendlyValue} (AC and DC)");
            else if (acOk)
                Fail($"{name}: {friendlyValue} on AC, but NOT on DC");
            else if (dcOk)
                Fail($"{name}: {friendlyValue} on DC, but NOT on AC");
            else
                Fail($"{name}: not set to {friendlyValue}");
        }
        catch
        {
            Warn($"Could not check {name}");
        }
    }

    private static void CheckHibernateDisabled()
    {
        try
        {
            // Check if hibernate file exists - definitive test for hibernate being enabled
            var hiberfilePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows).Substring(0, 3),
                "hiberfil.sys");

            if (File.Exists(hiberfilePath))
            {
                Fail("Hibernate is enabled (hiberfil.sys exists)");
            }
            else
            {
                Pass("Hibernate disabled (no hiberfil.sys)");
            }
        }
        catch
        {
            // Can't check file - fall back to powercfg
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powercfg.exe",
                    Arguments = "/availablesleepstates",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };

                using var process = Process.Start(psi);
                var output = process?.StandardOutput.ReadToEnd() ?? "";
                process?.WaitForExit(10_000);

                // Parse line by line. "Hibernate" in a line NOT starting with whitespace or
                // following "The following" is available. Lines with "not available" are disabled.
                var lines = output.Split('\n').Select(l => l.Trim()).ToArray();
                var hibernateAvailable = false;
                foreach (var line in lines)
                {
                    if (line.StartsWith("Hibernate", StringComparison.OrdinalIgnoreCase) &&
                        !line.Contains("not available", StringComparison.OrdinalIgnoreCase))
                    {
                        hibernateAvailable = true;
                    }
                }

                if (hibernateAvailable)
                    Fail("Hibernate appears to be available");
                else
                    Pass("Hibernate disabled");
            }
            catch
            {
                Warn("Could not check hibernate status");
            }
        }
    }

    private static void CheckLidCloseAction()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powercfg.exe",
                Arguments = "/query SCHEME_CURRENT 4f971e89-eebd-4455-a8de-9e59040e7347 5ca83367-6e45-459f-a27b-476b1d01c936",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            var output = process?.StandardOutput.ReadToEnd() ?? "";
            process?.WaitForExit(10_000);

            // Parse both AC and DC lines specifically
            var lines = output.Split('\n');
            var acOk = false;
            var dcOk = false;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.Contains("Current AC Power Setting Index", StringComparison.OrdinalIgnoreCase))
                {
                    acOk = trimmed.Contains("0x00000000", StringComparison.OrdinalIgnoreCase);
                }
                else if (trimmed.Contains("Current DC Power Setting Index", StringComparison.OrdinalIgnoreCase))
                {
                    dcOk = trimmed.Contains("0x00000000", StringComparison.OrdinalIgnoreCase);
                }
            }

            if (acOk && dcOk)
                Pass("Lid close action: Do Nothing (AC and DC)");
            else if (acOk)
                Fail("Lid close action: Do Nothing on AC, but NOT on DC");
            else if (dcOk)
                Fail("Lid close action: Do Nothing on DC, but NOT on AC");
            else
                Fail("Lid close action is not set to 'Do Nothing'");
        }
        catch
        {
            Warn("Could not check lid close action");
        }
    }

    private static void CheckNetworkSettings()
    {
        Console.WriteLine();
        Console.WriteLine("--- Network / WiFi ---");

        // WiFi power saving mode
        // GUID 19cbb8fa-... = Wireless Adapter Settings
        // GUID 12bbebe6-... = Power Saving Mode (0 = Maximum Performance)
        CheckPowerCfgBothAcDc("WiFi power saving", "19cbb8fa-5279-450e-9fac-8a3d5fedd0c1", "12bbebe6-58d6-4636-95bb-3217ef867c1a", 0, "Maximum Performance");

        // USB selective suspend
        // GUID 2a737441-... = USB Settings
        // GUID 48e6b7a6-... = USB Selective Suspend Setting (0 = Disabled)
        CheckPowerCfgBothAcDc("USB selective suspend", "2a737441-1930-4402-8d77-b2bebba308a3", "48e6b7a6-50f5-4782-a5d4-53bb8f07e226", 0, "Disabled");

        CheckWifiAdapterPowerManagement();
    }

    private static void CheckWifiAdapterPowerManagement()
    {
        try
        {
            var adaptersFound = 0;
            using var networkKey = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}");

            if (networkKey == null)
            {
                Warn("Could not open network adapter registry key for PnPCapabilities check.");
                return;
            }

            foreach (var subKeyName in networkKey.GetSubKeyNames())
            {
                if (!int.TryParse(subKeyName, out _))
                {
                    continue;
                }

                using var adapterKey = networkKey.OpenSubKey(subKeyName);
                if (adapterKey == null)
                {
                    continue;
                }

                var driverDesc = adapterKey.GetValue("DriverDesc") as string ?? "";
                var componentId = adapterKey.GetValue("ComponentId") as string ?? "";

                var isWireless = driverDesc.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) ||
                                 driverDesc.Contains("Wireless", StringComparison.OrdinalIgnoreCase) ||
                                 driverDesc.Contains("WLAN", StringComparison.OrdinalIgnoreCase) ||
                                 componentId.Contains("wireless", StringComparison.OrdinalIgnoreCase) ||
                                 componentId.Contains("wlan", StringComparison.OrdinalIgnoreCase);
                var isVirtual = driverDesc.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                                driverDesc.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase) ||
                                componentId.Contains("vwifimp", StringComparison.OrdinalIgnoreCase);

                if (!isWireless || isVirtual)
                {
                    continue;
                }

                adaptersFound++;
                var adapterName = string.IsNullOrWhiteSpace(driverDesc) ? subKeyName : driverDesc;
                var pnpCapabilities = adapterKey.GetValue("PnPCapabilities");
                if (pnpCapabilities is int pnpValue && pnpValue == 24)
                {
                    Pass($"WiFi adapter power management disabled: {adapterName}");
                }
                else
                {
                    Fail($"WiFi adapter power management not disabled: {adapterName} (PnPCapabilities={pnpCapabilities ?? "(not set)"}, expected 24)");
                }
            }

            if (adaptersFound == 0)
            {
                Warn("No WiFi adapters found for PnPCapabilities check (Ethernet-only system?).");
            }
        }
        catch (Exception ex)
        {
            Warn($"Could not check WiFi adapter power management: {ex.Message}");
        }
    }

    private static void CheckService()
    {
        Console.WriteLine();
        Console.WriteLine("--- KeepAlive Service ---");

        try
        {
            using var sc = new ServiceController("KeepAliveService");
            var status = sc.Status;
            var startType = sc.StartType;

            if (status == ServiceControllerStatus.Running)
                Pass($"Service status: {status}");
            else
                Fail($"Service status: {status} (expected Running)");

            if (startType == ServiceStartMode.Automatic)
                Pass($"Service start type: {startType}");
            else
                Fail($"Service start type: {startType} (expected Automatic)");
        }
        catch (InvalidOperationException)
        {
            Fail("KeepAlive service not installed");
        }
        catch (Exception ex)
        {
            Fail($"Could not check service: {ex.Message}");
        }
    }

    private static void CheckTeamViewer()
    {
        Console.WriteLine();
        Console.WriteLine("--- TeamViewer ---");

        // Check service
        try
        {
            using var sc = new ServiceController("TeamViewer");
            if (sc.Status == ServiceControllerStatus.Running)
            {
                Pass("TeamViewer service: Running");
                return;
            }
            else
            {
                Warn($"TeamViewer service: {sc.Status}");
            }
        }
        catch
        {
            // Service not installed - check for process
        }

        // Check process
        var serviceProcs = Process.GetProcessesByName("TeamViewer_Service");
        var uiProcs = Process.GetProcessesByName("TeamViewer");

        if (serviceProcs.Length > 0 || uiProcs.Length > 0)
        {
            Pass("TeamViewer process: Running");
        }
        else
        {
            Fail("TeamViewer: Not running (no service or process found)");
        }

        foreach (var p in serviceProcs) p.Dispose();
        foreach (var p in uiProcs) p.Dispose();
    }

    private static void CheckRegistryDword(RegistryKey root, string path, string name, int expected, string description)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            var value = key?.GetValue(name);

            if (value is int intVal && intVal == expected)
                Pass($"{description} = {expected}");
            else
                Fail($"{description} = {value ?? "(not set)"} (expected {expected})");
        }
        catch
        {
            Fail($"{description} - could not read registry");
        }
    }

    private static void CheckRegistryString(RegistryKey root, string path, string name, string expected, string description)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            var value = key?.GetValue(name) as string;

            if (value == expected)
                Pass($"{description} = {expected}");
            else
                Fail($"{description} = {value ?? "(not set)"} (expected {expected})");
        }
        catch
        {
            Fail($"{description} - could not read registry");
        }
    }

    private static void Pass(string message)
    {
        _passCount++;
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("  [PASS] ");
        Console.ResetColor();
        Console.WriteLine(message);
    }

    private static void Fail(string message)
    {
        _failCount++;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Write("  [FAIL] ");
        Console.ResetColor();
        Console.WriteLine(message);
    }

    private static void Warn(string message)
    {
        _warnCount++;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write("  [WARN] ");
        Console.ResetColor();
        Console.WriteLine(message);
    }
}
