using System.Diagnostics;
using KeepAliveService.UI;
using KeepAliveService.Update;
using Microsoft.Win32;

namespace KeepAliveService.Setup;

public static class AutoLogonConfigurator
{
    private const string WinlogonPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
    private const string PasswordlessPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\PasswordLess\Device";
    private const string PersonalizationPath = @"SOFTWARE\Policies\Microsoft\Windows\Personalization";
    private const string SystemPolicyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string DesktopPolicyPath = @"SOFTWARE\Policies\Microsoft\Windows\Control Panel\Desktop";

    private static int _failures;

    public static bool Configure()
    {
        _failures = 0;

        Console.WriteLine();
        Console.WriteLine("=== Auto-Login Configuration ===");
        WarnIfPolicyMayBeIgnoredOnHome();

        DisableWindowsHelloRequirement();
        EnableArso();
        DisableLockScreen();
        var credentials = PromptForCredentialsFromConsole();
        if (credentials != null)
        {
            ConfigureAutoLogonWithCredentials(credentials);
        }

        return _failures == 0;
    }

    public static bool Configure(CredentialInfo credentials)
    {
        _failures = 0;

        Console.WriteLine();
        Console.WriteLine("=== Auto-Login Configuration ===");
        WarnIfPolicyMayBeIgnoredOnHome();

        DisableWindowsHelloRequirement();
        EnableArso();
        DisableLockScreen();
        ConfigureAutoLogonWithCredentials(credentials);

        return _failures == 0;
    }

    public static bool ApplyNonCredentialSettings()
    {
        _failures = 0;

        Console.WriteLine();
        Console.WriteLine("=== Auto-Login Settings (non-credential) ===");
        WarnIfPolicyMayBeIgnoredOnHome();
        ClearAutoLoginBlockers();
        DisableWindowsHelloRequirement();
        EnableArso();
        DisableLockScreen();
        RemoveAutoLogonCount();
        ReassertForceAutoLogon();

        return _failures == 0;
    }

    private static void WarnIfPolicyMayBeIgnoredOnHome()
    {
        if (!WindowsEditionHelper.TryGetWindowsEditionInfo(out var info, out _)
            || info?.IsHomeOrCore != true)
        {
            return;
        }

        WriteWarning("Windows Home/Core detected: policy-based lock-screen and screen-saver settings may not be fully enforced.");
        WriteWarning("Auto-logon still depends on runtime behavior after reboot; verify with --check or the Status tab.");
    }

    /// <summary>
    /// Standalone mode for --update-password: only re-prompts for credentials and re-runs Autologon.
    /// </summary>
    public static bool UpdatePassword()
    {
        _failures = 0;
        Console.WriteLine();
        Console.WriteLine("=== Update Auto-Login Password ===");
        var credentials = PromptForCredentialsFromConsole();
        if (credentials != null)
        {
            ConfigureAutoLogonWithCredentials(credentials);
        }
        return _failures == 0;
    }

    public static bool UpdatePassword(CredentialInfo credentials)
    {
        _failures = 0;
        Console.WriteLine();
        Console.WriteLine("=== Update Auto-Login Password ===");
        ConfigureAutoLogonWithCredentials(credentials);
        return _failures == 0;
    }

    private static void DisableWindowsHelloRequirement()
    {
        // DevicePasswordLessBuildVersion = 0 allows password-based auto-login where supported.
        // This key may not exist on every Windows 10/11 edition/build.
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PasswordlessPath, writable: true);
            if (key == null)
            {
                WriteInfo("Windows Hello passwordless requirement key not present; skipping (not applicable on this build/edition).");
                return;
            }

            key.SetValue("DevicePasswordLessBuildVersion", 0, RegistryValueKind.DWord);
            WriteSuccess("Windows Hello passwordless requirement -> Disabled");
        }
        catch (Exception ex)
        {
            WriteWarning($"Disable Windows Hello requirement - {ex.Message} (continuing)");
            if (WindowsEditionHelper.TryGetWindowsEditionInfo(out var info, out _) && info?.IsWindows11 == true)
            {
                _failures++;
            }
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
            using var key = Registry.LocalMachine.CreateSubKey(DesktopPolicyPath, writable: true);
            if (key == null)
            {
                WriteWarning("Disable screen saver policy - Could not create/open registry key");
                return;
            }

            key.SetValue("ScreenSaverIsSecure", "0", RegistryValueKind.String);
            key.SetValue("ScreenSaveActive", "0", RegistryValueKind.String);
            key.SetValue("ScreenSaveTimeOut", "0", RegistryValueKind.String);
            key.SetValue("SCRNSAVE.EXE", string.Empty, RegistryValueKind.String);
            WriteSuccess("Screen saver -> Disabled (machine policy)");
            WriteSuccess("Screen saver password -> Disabled (machine policy)");
        }
        catch (Exception ex)
        {
            WriteWarning($"Disable screen saver password - {ex.Message}");
        }
    }

    private static void ConfigureAutoLogonWithCredentials(CredentialInfo credentials)
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

        var username = credentials.Username.Trim();
        if (string.IsNullOrWhiteSpace(username))
        {
            WriteError("Username cannot be empty");
            _failures++;
            return;
        }

        var password = credentials.Password;
        if (string.IsNullOrWhiteSpace(password))
        {
            WriteError("Password cannot be empty");
            _failures++;
            return;
        }

        var domain = credentials.ResolveDomain();
        if (string.IsNullOrWhiteSpace(domain))
        {
            WriteError("Domain could not be resolved");
            _failures++;
            return;
        }

        var readiness = SignInReadinessDetector.Assess(credentials);
        if (readiness.Status == SignInReadinessStatus.Blocked)
        {
            WriteError(readiness.Message);
            foreach (var step in readiness.RemediationSteps)
            {
                Console.WriteLine($"    - {step}");
            }

            _failures++;
            return;
        }

        if (readiness.Status == SignInReadinessStatus.Warning)
        {
            WriteWarning(readiness.Message);
        }
        else
        {
            WriteSuccess(readiness.Message);
        }

        var credentialCheck = CredentialValidator.Validate(credentials);
        if (credentialCheck.Status == CredentialValidationStatus.Invalid)
        {
            WriteError(credentialCheck.Message);
            _failures++;
            return;
        }

        if (credentialCheck.Status == CredentialValidationStatus.Warning)
        {
            WriteWarning(credentialCheck.Message);
        }
        else
        {
            WriteSuccess("Credential validation passed");
        }

        Console.WriteLine($"  Using domain: {domain}");

        // Warn about brief command-line exposure
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  Note: The password will briefly appear in the process list while Autologon64.exe runs.");
        Console.ResetColor();

        // Run Autologon64.exe (stores credentials encrypted as LSA secrets)
        RunAutologon(autologonPath, username, domain, password);

        // Set ForceAutoLogon to ensure it persists across multiple reboots
        ReassertForceAutoLogon();

        // Verify configuration
        VerifyAutoLogon(username, domain);
    }

    private static CredentialInfo? PromptForCredentialsFromConsole()
    {
        Console.WriteLine();
        Console.WriteLine("  Enter your Windows login credentials.");
        Console.WriteLine("  The password is your Windows/Microsoft account password, NOT your PIN.");
        Console.WriteLine();

        Console.Write("  Username: ");
        var username = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(username))
        {
            WriteError("Username cannot be empty");
            _failures++;
            return null;
        }

        Console.WriteLine();
        Console.WriteLine("  Account type:");
        Console.WriteLine("    1. Microsoft account (e.g., user@outlook.com, user@hotmail.com)");
        Console.WriteLine("    2. Local Windows account");
        Console.WriteLine("    3. Domain / Azure AD / Work account (e.g., user@company.com)");
        Console.Write("  Select (1/2/3): ");
        var accountChoice = Console.ReadLine()?.Trim();

        var accountType = accountChoice switch
        {
            "1" => AccountType.MicrosoftAccount,
            "2" => AccountType.LocalAccount,
            "3" => AccountType.DomainOrWorkAccount,
            _ => (AccountType?)null,
        };

        if (accountType == null)
        {
            WriteError("Invalid selection. Please enter 1, 2, or 3.");
            _failures++;
            return null;
        }

        string domain;
        if (accountType == AccountType.MicrosoftAccount)
        {
            domain = "MicrosoftAccount";
        }
        else if (accountType == AccountType.LocalAccount)
        {
            domain = Environment.MachineName;
        }
        else
        {
            var detectedDomain = Environment.UserDomainName;
            Console.Write($"  Domain name [{detectedDomain}]: ");
            var customDomain = Console.ReadLine()?.Trim();
            domain = string.IsNullOrWhiteSpace(customDomain) ? detectedDomain : customDomain;
        }

        Console.WriteLine($"  Using domain: {domain}");
        Console.Write("  Password: ");
        var password = ReadPasswordMasked();
        Console.WriteLine();

        if (string.IsNullOrWhiteSpace(password))
        {
            WriteError("Password cannot be empty");
            _failures++;
            return null;
        }

        return new CredentialInfo(
            Username: username,
            Password: password,
            AccountType: accountType.Value,
            Domain: domain);
    }

    private static string? DownloadAutologon()
    {
        AppSettings.EnsureDirectories();
        var toolsDir = AppSettings.ToolsDirectory;
        var autologonPath = Path.Combine(toolsDir, "Autologon64.exe");

        if (File.Exists(autologonPath))
        {
            WriteSuccess("Autologon64.exe already downloaded");
            return autologonPath;
        }

        WriteInfo("Downloading Autologon64.exe from Sysinternals...");

        try
        {
            using var httpClient = new HttpClient();
            var version = typeof(AutoLogonConfigurator).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"KeepAliveService/{version}");

            using var request = new HttpRequestMessage(HttpMethod.Get, "https://live.sysinternals.com/Autologon64.exe");
            using var response = httpClient.Send(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var contentLength = response.Content.Headers.ContentLength;
            using var source = response.Content.ReadAsStream();
            using var destination = File.Create(autologonPath);

            var buffer = new byte[81920];
            long totalRead = 0;
            var nextPercentReport = 10;
            var nextByteReport = 2L * 1024L * 1024L;

            while (true)
            {
                var bytesRead = source.Read(buffer, 0, buffer.Length);
                if (bytesRead <= 0)
                {
                    break;
                }

                destination.Write(buffer, 0, bytesRead);
                totalRead += bytesRead;

                if (contentLength is > 0)
                {
                    var percent = (int)Math.Clamp(totalRead * 100L / contentLength.Value, 0L, 100L);
                    if (percent >= nextPercentReport)
                    {
                        WriteInfo($"Autologon download: {percent}% ({FormatBytes(totalRead)} / {FormatBytes(contentLength.Value)})");
                        while (percent >= nextPercentReport)
                        {
                            nextPercentReport += 10;
                        }
                    }
                }
                else if (totalRead >= nextByteReport)
                {
                    WriteInfo($"Autologon download: {FormatBytes(totalRead)}");
                    nextByteReport += 2L * 1024L * 1024L;
                }
            }

            if (contentLength is > 0)
            {
                WriteInfo($"Autologon download: 100% ({FormatBytes(totalRead)} / {FormatBytes(contentLength.Value)})");
            }

            WriteSuccess("Autologon64.exe downloaded");
            return autologonPath;
        }
        catch (Exception ex)
        {
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
            var escapedPath = filePath.Replace("'", "''");
            var verificationScript =
                "$sig = Get-AuthenticodeSignature -FilePath '" + escapedPath + "'; " +
                "$subj = if ($sig.SignerCertificate) { $sig.SignerCertificate.Subject } else { '' }; " +
                "if ($sig.Status -eq 'Valid' -and $subj -like '*Microsoft Corporation*') { Write-Output 'VALID'; exit 0 } " +
                "else { Write-Output ($sig.Status.ToString() + '|' + $subj); exit 1 }";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{verificationScript}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                WriteError("Autologon64.exe signature verification failed: could not start powershell.");
                return false;
            }

            var outputReadTask = process.StandardOutput.ReadToEndAsync();
            var errorReadTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(30_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best effort only.
                }

                WriteError("Autologon64.exe signature verification timed out.");
                Console.WriteLine("    The file may be tampered with or untrusted. Deleting it.");
                try { File.Delete(filePath); } catch { }
                return false;
            }

            var readsCompleted = Task.WaitAll([outputReadTask, errorReadTask], 5000);
            var output = readsCompleted ? outputReadTask.Result.Trim() : string.Empty;
            var error = readsCompleted ? errorReadTask.Result.Trim() : string.Empty;
            if (!readsCompleted)
            {
                WriteWarning("Autologon64.exe signature verification output read timed out.");
            }

            if (process.ExitCode == 0 && output.Contains("VALID", StringComparison.OrdinalIgnoreCase))
            {
                WriteSuccess("Autologon64.exe Authenticode signature verified (Valid, Microsoft Corporation)");
                return true;
            }

            WriteError($"Autologon64.exe signature verification failed: {output}");
            if (!string.IsNullOrWhiteSpace(error))
                Console.WriteLine($"    Error: {error}");
            Console.WriteLine("    The file may be tampered with or untrusted. Deleting it.");
            try { File.Delete(filePath); } catch { }
            return false;
        }
        catch (Exception ex)
        {
            WriteError($"Autologon64.exe signature verification failed: {ex.Message}");
            Console.WriteLine("    The file may be tampered with or untrusted. Deleting it.");
            try { File.Delete(filePath); } catch { }
            return false;
        }
    }

    private static void RunAutologon(string autologonPath, string username, string domain, string password)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = autologonPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(username);
            psi.ArgumentList.Add(domain);
            psi.ArgumentList.Add(password);
            psi.ArgumentList.Add("/accepteula");

            using var process = Process.Start(psi);
            if (process == null)
            {
                WriteError("Failed to start Autologon64.exe.");
                _failures++;
                return;
            }

            var errorReadTask = process.StandardError.ReadToEndAsync();
            var outputReadTask = process.StandardOutput.ReadToEndAsync();

            if (!process.WaitForExit(30_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best effort only.
                }

                WriteError("Autologon timed out after 30 seconds.");
                _failures++;
                return;
            }

            Task.WaitAll([errorReadTask, outputReadTask], 5000);
            var error = errorReadTask.Result.Trim();
            var output = outputReadTask.Result.Trim();

            if (process.ExitCode == 0)
            {
                WriteSuccess("Autologon configured (credentials stored encrypted as LSA secrets)");
            }
            else
            {
                WriteError($"Autologon failed (exit code {process.ExitCode})");
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

    private static void VerifyAutoLogon(string expectedUsername, string expectedDomain)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(WinlogonPath);
            var autoAdminLogon = key?.GetValue("AutoAdminLogon") as string;
            var defaultUserName = key?.GetValue("DefaultUserName") as string;

            if (autoAdminLogon != "1")
            {
                WriteError($"Auto-login verification failed: AutoAdminLogon={autoAdminLogon ?? "(not set)"} (expected 1)");
                _failures++;
                return;
            }

            if (string.IsNullOrEmpty(defaultUserName))
            {
                WriteError("Auto-login verification failed: DefaultUserName is not set");
                _failures++;
                return;
            }

            var acceptedUserNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                expectedUsername,
                $"{expectedDomain}\\{expectedUsername}",
            };
            var atIndex = expectedUsername.IndexOf('@');
            if (atIndex > 0)
            {
                var shortName = expectedUsername[..atIndex];
                acceptedUserNames.Add(shortName);
                acceptedUserNames.Add($"{expectedDomain}\\{shortName}");
            }

            if (!acceptedUserNames.Contains(defaultUserName))
            {
                WriteWarning($"Auto-login user mismatch: configured={defaultUserName}, expected one of [{string.Join(", ", acceptedUserNames)}]");
                _failures++;
                return;
            }

            WriteSuccess($"Auto-login verified for user: {defaultUserName}");
        }
        catch (Exception ex)
        {
            WriteWarning($"Could not verify auto-login: {ex.Message}");
        }
    }

    private static void ReassertForceAutoLogon()
    {
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
    }

    private static void RemoveAutoLogonCount()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(WinlogonPath, writable: true);
            if (key?.GetValue("AutoLogonCount") != null)
            {
                key.DeleteValue("AutoLogonCount", throwOnMissingValue: false);
                WriteSuccess("AutoLogonCount -> Removed");
            }
        }
        catch (Exception ex)
        {
            WriteWarning($"Remove AutoLogonCount - {ex.Message}");
        }
    }

    private static void ClearAutoLoginBlockers()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SystemPolicyPath);
            var legalNotice = key?.GetValue("LegalNoticeText") as string;
            var legalCaption = key?.GetValue("LegalNoticeCaption") as string;

            if (!string.IsNullOrWhiteSpace(legalNotice) || !string.IsNullOrWhiteSpace(legalCaption))
            {
                WriteWarning("LegalNoticeText/LegalNoticeCaption is set (blocks auto-login). Removing...");
                try
                {
                    using var writableKey = Registry.LocalMachine.OpenSubKey(SystemPolicyPath, writable: true);
                    if (writableKey == null)
                    {
                        WriteError("Could not open policy key for LegalNotice auto-fix.");
                        _failures++;
                    }
                    else
                    {
                        writableKey.SetValue("LegalNoticeText", string.Empty, RegistryValueKind.String);
                        writableKey.SetValue("LegalNoticeCaption", string.Empty, RegistryValueKind.String);

                        using var verifyKey = Registry.LocalMachine.OpenSubKey(SystemPolicyPath);
                        var legalNoticeAfter = verifyKey?.GetValue("LegalNoticeText") as string;
                        var legalCaptionAfter = verifyKey?.GetValue("LegalNoticeCaption") as string;

                        if (!string.IsNullOrWhiteSpace(legalNoticeAfter) || !string.IsNullOrWhiteSpace(legalCaptionAfter))
                        {
                            WriteError("Could not remove LegalNotice blocker (value persisted, likely managed policy).");
                            _failures++;
                        }
                        else
                        {
                            WriteSuccess("LegalNoticeText/LegalNoticeCaption -> Cleared");
                        }
                    }
                }
                catch (Exception ex)
                {
                    WriteError($"Could not remove LegalNotice blocker: {ex.Message}");
                    _failures++;
                }
            }

            var dontDisplay = key?.GetValue("DontDisplayLastUserName");
            if (dontDisplay is int d && d == 1)
            {
                try
                {
                    using var writableKey = Registry.LocalMachine.OpenSubKey(SystemPolicyPath, writable: true);
                    if (writableKey == null)
                    {
                        WriteWarning("DontDisplayLastUserName auto-fix failed: policy key not writable.");
                    }
                    else
                    {
                        writableKey.SetValue("DontDisplayLastUserName", 0, RegistryValueKind.DWord);
                        WriteSuccess("DontDisplayLastUserName -> Set to 0 (was 1)");
                    }
                }
                catch (Exception ex)
                {
                    WriteWarning($"DontDisplayLastUserName auto-fix failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            WriteWarning($"Could not inspect auto-login blockers: {ex.Message}");
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

    private static void WriteInfo(string message)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write("  [INFO] ");
        Console.ResetColor();
        Console.WriteLine(message);
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB"];
        var order = 0;
        double size = bytes;
        while (size >= 1024 && order < suffixes.Length - 1)
        {
            order++;
            size /= 1024;
        }

        return $"{size:0.##} {suffixes[order]}";
    }
}
