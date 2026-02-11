using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Win32;

namespace KeepAliveService.Setup;

public static class ComplianceChecker
{
    private static int _passCount;
    private static int _failCount;
    private static int _warnCount;

    public static int RunCheck()
    {
        _passCount = 0;
        _failCount = 0;
        _warnCount = 0;

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

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var edition = key?.GetValue("EditionID") as string ?? "Unknown";
            var build = key?.GetValue("CurrentBuildNumber") as string ?? "Unknown";

            if (edition.Contains("Pro", StringComparison.OrdinalIgnoreCase) ||
                edition.Contains("Enterprise", StringComparison.OrdinalIgnoreCase) ||
                edition.Contains("Education", StringComparison.OrdinalIgnoreCase))
            {
                Pass($"Windows Edition: {edition} (Build {build})");
            }
            else
            {
                Fail($"Windows Edition: {edition} - requires Pro, Enterprise, or Education");
            }
        }
        catch
        {
            Fail("Could not determine Windows edition");
        }
    }

    private static void CheckUpdatePolicy()
    {
        Console.WriteLine();
        Console.WriteLine("--- Windows Update Policy ---");

        CheckRegistryDword(Registry.LocalMachine,
            @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU",
            "NoAutoRebootWithLoggedOnUsers", 1,
            "No auto-reboot with logged-on users");

        CheckRegistryDword(Registry.LocalMachine,
            @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings",
            "IsActiveHoursEnabled", 1,
            "Active Hours enabled");
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

        // Check Windows Hello is not required
        CheckRegistryDword(Registry.LocalMachine,
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\PasswordLess\Device",
            "DevicePasswordLessBuildVersion", 0,
            "Windows Hello passwordless requirement disabled");

        // Check for blockers
        CheckAutoLoginBlockers();
    }

    private static void CheckAutoLoginBlockers()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");

            var legalNotice = key?.GetValue("LegalNoticeText") as string;
            if (!string.IsNullOrWhiteSpace(legalNotice))
            {
                Fail("LegalNoticeText is set - this blocks auto-login (user must click OK)");
            }
            else
            {
                Pass("No LegalNoticeText blocker");
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

        CheckRegistryDword(Registry.LocalMachine,
            @"SOFTWARE\Policies\Microsoft\Windows\Personalization",
            "NoLockScreen", 1, "Lock screen disabled");

        CheckRegistryDword(Registry.LocalMachine,
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon",
            "DisableLockWorkstation", 1, "Workstation lock disabled");
    }

    private static void CheckPowerSettings()
    {
        Console.WriteLine();
        Console.WriteLine("--- Power Settings ---");

        // Check sleep timeout via powercfg query
        // SUB_SLEEP GUID = 238c9fa8-0aad-41ed-83f4-97be242c8f20
        // STANDBYIDLE GUID = 29f6c1db-86da-48c5-9fdb-f2b67b1f44da
        CheckPowerCfgValue("Sleep timeout (AC)", "238c9fa8-0aad-41ed-83f4-97be242c8f20", "29f6c1db-86da-48c5-9fdb-f2b67b1f44da", 0);

        // Check hibernate - use powercfg /hibernate to see if hiberfile exists
        CheckHibernateDisabled();

        // Check lid close action
        // SUB_BUTTONS GUID = 4f971e89-eebd-4455-a8de-9e59040e7347
        // LIDACTION GUID = 5ca83367-6e45-459f-a27b-476b1d01c936
        CheckLidCloseAction();
    }

    private static void CheckPowerCfgValue(string name, string subgroupGuid, string settingGuid, int expectedValue)
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

            // Parse "Current AC Power Setting Index: 0x00000000"
            var expectedHex = $"0x{expectedValue:x8}";
            var acLine = output.Split('\n')
                .FirstOrDefault(l => l.Contains("Current AC Power Setting Index", StringComparison.OrdinalIgnoreCase));

            if (acLine != null && acLine.Contains(expectedHex, StringComparison.OrdinalIgnoreCase))
            {
                Pass($"{name} = {expectedValue} (Never)");
            }
            else
            {
                var actual = acLine?.Trim().Split(':').LastOrDefault()?.Trim() ?? "(could not read)";
                Fail($"{name} = {actual} (expected {expectedHex})");
            }
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
