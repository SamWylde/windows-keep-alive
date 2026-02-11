using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;

namespace KeepAliveService.Setup;

public static class AutoLogonConfigurator
{
    private const string WinlogonPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
    private const string PasswordlessPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\PasswordLess\Device";
    private const string PersonalizationPath = @"SOFTWARE\Policies\Microsoft\Windows\Personalization";
    private const string SystemPolicyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";

    private static int _failures;

    public static bool Configure()
    {
        _failures = 0;

        Console.WriteLine();
        Console.WriteLine("=== Auto-Login Configuration ===");

        DisableWindowsHelloRequirement();
        EnableArso();
        DisableLockScreen();
        ConfigureAutoLogon();

        return _failures == 0;
    }

    /// <summary>
    /// Standalone mode for --update-password: only re-prompts for credentials and re-runs Autologon.
    /// </summary>
    public static void UpdatePassword()
    {
        Console.WriteLine();
        Console.WriteLine("=== Update Auto-Login Password ===");
        ConfigureAutoLogon();
    }

    private static void DisableWindowsHelloRequirement()
    {
        // DevicePasswordLessBuildVersion = 0 allows password-based auto-login
        // (by default Windows 11 requires Windows Hello which can't do auto-login)
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(PasswordlessPath, writable: true);
            key?.SetValue("DevicePasswordLessBuildVersion", 0, RegistryValueKind.DWord);
            WriteSuccess("Windows Hello passwordless requirement -> Disabled");
        }
        catch (Exception ex)
        {
            WriteError($"Disable Windows Hello requirement - {ex.Message}");
            _failures++;
        }
    }

    private static void EnableArso()
    {
        // ARSO (Automatic Restart Sign-On) automatically signs you back in after
        // update reboots using encrypted credentials stored by Windows
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(SystemPolicyPath, writable: true);
            key?.SetValue("DisableAutomaticRestartSignOn", 0, RegistryValueKind.DWord);
            WriteSuccess("ARSO (Automatic Restart Sign-On) -> Enabled");
        }
        catch (Exception ex)
        {
            WriteError($"Enable ARSO - {ex.Message}");
            _failures++;
        }
    }

    private static void DisableLockScreen()
    {
        // Disable lock screen so the machine stays at the desktop after login
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(PersonalizationPath, writable: true);
            key?.SetValue("NoLockScreen", 1, RegistryValueKind.DWord);
            WriteSuccess("Lock screen -> Disabled");
        }
        catch (Exception ex)
        {
            WriteError($"Disable lock screen - {ex.Message}");
            _failures++;
        }

        // Disable workstation locking (Ctrl+Alt+Del -> Lock)
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(WinlogonPath, writable: true);
            key?.SetValue("DisableLockWorkstation", 1, RegistryValueKind.DWord);
            WriteSuccess("Workstation lock -> Disabled");
        }
        catch (Exception ex)
        {
            WriteError($"Disable workstation lock - {ex.Message}");
            _failures++;
        }

        // Disable screen saver password requirement via machine-wide policy
        // Using HKLM policy instead of HKCU so it applies to the autologon user,
        // not just the admin account running setup.
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(
                @"SOFTWARE\Policies\Microsoft\Windows\Control Panel\Desktop", writable: true);
            key?.SetValue("ScreenSaverIsSecure", "0", RegistryValueKind.String);
            WriteSuccess("Screen saver password -> Disabled (machine policy)");
        }
        catch (Exception ex)
        {
            WriteWarning($"Disable screen saver password - {ex.Message}");
        }
    }

    private static void ConfigureAutoLogon()
    {
        // Download Sysinternals Autologon64.exe
        var autologonPath = DownloadAutologon();
        if (autologonPath == null)
        {
            WriteError("Cannot proceed without Autologon64.exe");
            _failures++;
            return;
        }

        // Verify Authenticode signature
        if (!VerifyAuthenticodeSignature(autologonPath))
        {
            _failures++;
            return;
        }

        // Prompt for credentials
        Console.WriteLine();
        Console.WriteLine("  Enter your Windows login credentials.");
        Console.WriteLine("  The password is your Windows/Microsoft account password, NOT your PIN.");
        Console.WriteLine();

        Console.Write("  Username: ");
        var username = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(username))
        {
            WriteError("Username cannot be empty");
            _failures++;
            return;
        }

        // Prompt for account type instead of guessing from username
        Console.WriteLine();
        Console.WriteLine("  Account type:");
        Console.WriteLine("    1. Microsoft account (e.g., user@outlook.com, user@hotmail.com)");
        Console.WriteLine("    2. Local Windows account");
        Console.WriteLine("    3. Domain / Azure AD / Work account (e.g., user@company.com)");
        Console.Write("  Select (1/2/3): ");
        var accountChoice = Console.ReadLine()?.Trim();

        string domain;
        switch (accountChoice)
        {
            case "1":
                domain = "MicrosoftAccount";
                break;
            case "2":
                domain = Environment.MachineName;
                break;
            case "3":
                var detectedDomain = Environment.UserDomainName;
                Console.Write($"  Domain name [{detectedDomain}]: ");
                var customDomain = Console.ReadLine()?.Trim();
                domain = string.IsNullOrEmpty(customDomain) ? detectedDomain : customDomain;
                break;
            default:
                WriteError("Invalid selection. Please enter 1, 2, or 3.");
                _failures++;
                return;
        }

        Console.WriteLine($"  Using domain: {domain}");

        Console.Write("  Password: ");
        var password = ReadPasswordMasked();
        Console.WriteLine();

        if (string.IsNullOrEmpty(password))
        {
            WriteError("Password cannot be empty");
            _failures++;
            return;
        }

        // Warn about brief command-line exposure
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  Note: The password will briefly appear in the process list while Autologon64.exe runs.");
        Console.ResetColor();

        // Run Autologon64.exe (stores credentials encrypted as LSA secrets)
        RunAutologon(autologonPath, username, domain, password);

        // Set ForceAutoLogon to ensure it persists across multiple reboots
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(WinlogonPath, writable: true);
            key?.SetValue("ForceAutoLogon", "1", RegistryValueKind.String);
            WriteSuccess("ForceAutoLogon -> Enabled");
        }
        catch (Exception ex)
        {
            WriteError($"Set ForceAutoLogon - {ex.Message}");
            _failures++;
        }

        // Verify configuration
        VerifyAutoLogon(username);
    }

    private static string? DownloadAutologon()
    {
        var toolsDir = Path.Combine(AppContext.BaseDirectory, "tools");
        Directory.CreateDirectory(toolsDir);
        var autologonPath = Path.Combine(toolsDir, "Autologon64.exe");

        if (File.Exists(autologonPath))
        {
            WriteSuccess("Autologon64.exe already downloaded");
            return autologonPath;
        }

        Console.Write("  Downloading Autologon64.exe from Sysinternals...");

        try
        {
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("KeepAliveService/1.0");
            var bytes = httpClient.GetByteArrayAsync("https://live.sysinternals.com/Autologon64.exe").Result;
            File.WriteAllBytes(autologonPath, bytes);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(" Done");
            Console.ResetColor();
            return autologonPath;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(" Failed");
            Console.ResetColor();
            WriteError($"Download failed: {ex.Message}");
            Console.WriteLine("  You can manually download Autologon64.exe from:");
            Console.WriteLine("  https://learn.microsoft.com/en-us/sysinternals/downloads/autologon");
            Console.WriteLine($"  Place it at: {autologonPath}");
            return null;
        }
    }

    private static bool VerifyAuthenticodeSignature(string filePath)
    {
        try
        {
            var cert = X509Certificate.CreateFromSignedFile(filePath);
            var subject = cert.Subject;

            if (subject.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
            {
                WriteSuccess("Autologon64.exe signature verified (Microsoft)");
                return true;
            }
            else
            {
                WriteError($"Autologon64.exe is signed but NOT by Microsoft: {subject}");
                Console.WriteLine("    The file may have been tampered with. Deleting it.");
                try { File.Delete(filePath); } catch { }
                return false;
            }
        }
        catch (Exception)
        {
            WriteError("Autologon64.exe has no valid digital signature or is corrupted.");
            Console.WriteLine("    The file may have been tampered with. Deleting it.");
            try { File.Delete(filePath); } catch { }
            return false;
        }
    }

    private static void RunAutologon(string autologonPath, string username, string domain, string password)
    {
        try
        {
            // Escape embedded double quotes in password to prevent argument parsing issues
            var escapedPassword = password.Replace("\"", "\\\"");

            var psi = new ProcessStartInfo
            {
                FileName = autologonPath,
                Arguments = $"\"{username}\" \"{domain}\" \"{escapedPassword}\" /accepteula",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            process?.WaitForExit(30_000);

            if (process?.ExitCode == 0)
            {
                WriteSuccess("Autologon configured (credentials stored encrypted as LSA secrets)");
            }
            else
            {
                var error = process?.StandardError.ReadToEnd()?.Trim();
                var output = process?.StandardOutput.ReadToEnd()?.Trim();
                WriteError($"Autologon failed (exit code {process?.ExitCode})");
                if (!string.IsNullOrEmpty(error)) Console.WriteLine($"    Error: {error}");
                if (!string.IsNullOrEmpty(output)) Console.WriteLine($"    Output: {output}");

                Console.WriteLine();
                Console.WriteLine("  This may be caused by Credential Guard. Check:");
                Console.WriteLine("  1. Open 'System Information' (msinfo32)");
                Console.WriteLine("  2. Look for 'Credential Guard' under 'Virtualization-based security Services Running'");
                Console.WriteLine("  3. If present, you may need to disable it for auto-login to work.");
                _failures++;
            }
        }
        catch (Exception ex)
        {
            WriteError($"Failed to run Autologon: {ex.Message}");
            _failures++;
        }
    }

    private static void VerifyAutoLogon(string expectedUsername)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(WinlogonPath);
            var autoAdminLogon = key?.GetValue("AutoAdminLogon") as string;
            var defaultUserName = key?.GetValue("DefaultUserName") as string;

            if (autoAdminLogon != "1")
            {
                WriteError($"Auto-login verification failed: AutoAdminLogon={autoAdminLogon ?? "(not set)"} (expected 1)");
                return;
            }

            if (string.IsNullOrEmpty(defaultUserName))
            {
                WriteError("Auto-login verification failed: DefaultUserName is not set");
                return;
            }

            if (!string.Equals(defaultUserName, expectedUsername, StringComparison.OrdinalIgnoreCase))
            {
                WriteWarning($"Auto-login user mismatch: configured={defaultUserName}, expected={expectedUsername}");
                return;
            }

            WriteSuccess($"Auto-login verified for user: {defaultUserName}");
        }
        catch (Exception ex)
        {
            WriteWarning($"Could not verify auto-login: {ex.Message}");
        }
    }

    private static string ReadPasswordMasked()
    {
        var password = new System.Text.StringBuilder();
        while (true)
        {
            var keyInfo = Console.ReadKey(intercept: true);
            if (keyInfo.Key == ConsoleKey.Enter)
                break;
            if (keyInfo.Key == ConsoleKey.Backspace)
            {
                if (password.Length > 0)
                {
                    password.Length--;
                    Console.Write("\b \b");
                }
            }
            else if (!char.IsControl(keyInfo.KeyChar))
            {
                password.Append(keyInfo.KeyChar);
                Console.Write('*');
            }
        }

        return password.ToString();
    }

    private static void WriteSuccess(string message)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("  [OK] ");
        Console.ResetColor();
        Console.WriteLine(message);
    }

    private static void WriteWarning(string message)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write("  [WARN] ");
        Console.ResetColor();
        Console.WriteLine(message);
    }

    private static void WriteError(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Write("  [FAIL] ");
        Console.ResetColor();
        Console.WriteLine(message);
    }
}
