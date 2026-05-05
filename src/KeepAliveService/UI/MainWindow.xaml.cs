using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using KeepAliveService.Native;
using KeepAliveService.Setup;
using KeepAliveService.Update;
using Wpf.Ui.Controls;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;
using WinFormsNotifyIcon = System.Windows.Forms.NotifyIcon;
using WinFormsContextMenuStrip = System.Windows.Forms.ContextMenuStrip;
using WinFormsToolStripMenuItem = System.Windows.Forms.ToolStripMenuItem;
using WinFormsToolTipIcon = System.Windows.Forms.ToolTipIcon;

namespace KeepAliveService.UI;

public sealed partial class MainWindow : FluentWindow
{
    private const int MaxLogChars = 250_000;
    private const int StatusTabAutoCheckDebounceSeconds = 30;

    private readonly AppSettings _settings;
    private readonly GitHubUpdateChecker _updateChecker;
    private readonly TextWriter _originalConsoleOut;
    private readonly TextWriter _originalConsoleError;

    private readonly WpfOutputWriter _outputWriter;
    private readonly Paragraph _outputParagraph;
    private readonly DispatcherTimer _updateTimer;
    private readonly DispatcherTimer _logTimer;
    private readonly DispatcherTimer _credentialPersistTimer;
    private readonly WinFormsNotifyIcon _trayIcon;

    private bool _isOperationRunning;
    private bool _isUpdateCheckRunning;
    private bool _suppressCredentialPersistence;
    private bool _passwordDecryptFailed;
    private bool _isStartupUpdateCheck;
    private bool _startupUpdatePromptShown;
    private readonly bool _startMinimized;
    private bool _allowClose;
    private bool _settingsPersistenceDisabled;
    private bool _applicationUninstalled;
    private bool _trayHintShown;
    private bool _isHiddenToTray;
    private uint _taskbarCreatedMessageId;
    private bool _serviceInstalled;
    private ServiceControllerStatus? _currentServiceStatus;
    private long _lastLogLength = -1;
    private DateTime _lastStatusTabCheckUtc = DateTime.MinValue;
    private UpdateCheckResult? _lastUpdateResult;

    public MainWindow(bool startMinimized = false)
    {
        _startMinimized = startMinimized;
        _settings = AppSettings.Load();
        _updateChecker = new GitHubUpdateChecker(_settings);
        _originalConsoleOut = Console.Out;
        _originalConsoleError = Console.Error;

        // Must be set before InitializeComponent — XAML event handlers
        // (TextChanged, SelectionChanged) fire during parsing and would
        // call QueueCredentialPersistence() before timers are initialized.
        _suppressCredentialPersistence = true;

        InitializeComponent();

        var version = FormatVersion(Assembly.GetExecutingAssembly().GetName().Version);
        Title = $"Windows Keep Alive v{version}";
        TryApplyWindowIcon();

        // Set up output console
        _outputParagraph = new Paragraph { Margin = new Thickness(0) };
        OutputBox.Document = new FlowDocument(_outputParagraph)
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 14,
        };

        _outputWriter = new WpfOutputWriter(OutputBox, _outputParagraph);
        Console.SetOut(_outputWriter);
        Console.SetError(_outputWriter);

        // Timers (must be initialized before ComboBox population, which fires events)
        _updateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromHours(Math.Max(1, _settings.UpdateCheckIntervalHours)),
        };
        _updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync(force: false);

        _logTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _logTimer.Tick += (_, _) => RefreshLogViewer();

        _credentialPersistTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _credentialPersistTimer.Tick += (_, _) =>
        {
            _credentialPersistTimer.Stop();
            PersistCredentialInputs();
        };

        ModeBox.Items.Add(new ModeOption("Keep awake only", KeepAliveMode.KeepAwakeOnly));
        ModeBox.Items.Add(new ModeOption("Keep awake + remote access watchdog", KeepAliveMode.RemoteAccessWatchdog));
        ModeBox.Items.Add(new ModeOption("Full unattended auto-login", KeepAliveMode.FullUnattended));
        SelectMode(_settings.GetOperationMode());
        ApplyModeUiState();

        // Account type combo (fires SelectionChanged → Credential_Changed → QueueCredentialPersistence)
        _suppressCredentialPersistence = true;
        AccountTypeBox.Items.Add(new AccountTypeOption("Microsoft Account", AccountType.MicrosoftAccount));
        AccountTypeBox.Items.Add(new AccountTypeOption("Local Account", AccountType.LocalAccount));
        AccountTypeBox.Items.Add(new AccountTypeOption("Domain / Work Account", AccountType.DomainOrWorkAccount));
        AccountTypeBox.SelectedIndex = 0;
        _suppressCredentialPersistence = false;

        // Status bar
        VersionStatus.Text = $"v{version}";
        AdminStatus.Text = $"Admin: {(IsRunningAsAdmin() ? "Yes" : "No")}";

        // Tray icon
        _trayIcon = CreateTrayIcon();

        // Prevent visible window flash during tray-startup. Application.Run()
        // calls Show() before Loaded fires, so without this the window briefly
        // appears with broken rendering while the desktop is still loading.
        if (_startMinimized)
        {
            WindowState = WindowState.Minimized;
            ShowInTaskbar = false;
        }

        Loaded += async (_, _) => await OnLoadedAsync();
        Closing += OnWindowClosing;
        Closed += (_, _) => RestoreConsoleOutput();

        if (System.Windows.Application.Current != null)
        {
            System.Windows.Application.Current.SessionEnding += (_, _) =>
            {
                _allowClose = true;
            };
        }
    }

    private async Task OnLoadedAsync()
    {
        // Install TaskbarCreated hook BEFORE showing the tray icon so we never
        // miss the first recovery message if explorer finishes loading after us.
        InstallTaskbarCreatedHook();

        if (_startMinimized)
        {
            // Wait for the Windows shell to be ready before showing the tray icon.
            // At logon the scheduled task can fire before explorer.exe has finished
            // initializing the notification area, so the icon would be silently lost.
            await WaitForShellReadyAsync();
            HideToTray();
        }

        InstallManager.EnsureProgramDataLayout();
        _settings.InstallPath = InstallManager.CanonicalExePath;
        _settings.Save();

        LoadSavedCredentialInputs();
        RefreshSetupStatus();

        // Auto-repair startup task if setup was completed but the task is missing/broken.
        TryAutoRepairStartupTask();

        Console.WriteLine("[INFO] Windows Keep Alive GUI started.");
        Console.WriteLine($"[INFO] Install path: {InstallManager.CanonicalExePath}");

        RefreshServiceStatus();
        if (_settings.SetupCompletedUtc == null)
        {
            ComplianceLabel.Text = "Setup has not been completed. Go to the Setup tab to configure this machine.";
            ComplianceLabel.Foreground = Brushes.DarkGoldenrod;
        }

        RefreshLogViewer();
        _logTimer.Start();

        Console.WriteLine("[INFO] Checking for updates...");
        _isStartupUpdateCheck = true;
        await CheckForUpdatesAsync(force: true);
        _isStartupUpdateCheck = false;
        _updateTimer.Start();
    }

    // ───────────────────── Setup operations ─────────────────────

    private async void RunSetup_Click(object sender, RoutedEventArgs e) => await RunSetupAsync();
    private async void TestCredentials_Click(object sender, RoutedEventArgs e) => await TestCredentialsAsync();
    private async void UpdatePassword_Click(object sender, RoutedEventArgs e) => await UpdatePasswordAsync();
    private async void Restore_Click(object sender, RoutedEventArgs e) => await RestoreAsync();
    private async void Uninstall_Click(object sender, RoutedEventArgs e) => await UninstallAsync();

    private async Task RunSetupAsync()
    {
        var mode = SelectedMode;
        CredentialInfo? credentials = null;

        if (mode.UsesAutoLogin() && !TryReadCredentials(out credentials, out var validationError))
        {
            MessageBox.Show(this, validationError, "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        else if (!mode.UsesAutoLogin())
        {
            FlushPendingCredentialPersistence();
        }

        await RunOperationAsync(
            "Running setup",
            async () =>
            {
                var exitCode = await Task.Run(() => SetupManager.RunSetup(mode, credentials));
                if (exitCode == 0)
                {
                    _settings.SetOperationMode(mode);
                    _settings.SetupCompletedUtc = DateTime.UtcNow;
                    try { _settings.Save(); }
                    catch { /* Best effort */ }
                    SyncInMemorySetupState(AppSettings.Load());
                    Console.WriteLine("[OK] Setup completed successfully.");
                }
                else
                {
                    SyncInMemorySetupState(AppSettings.Load());
                    Console.WriteLine("[FAIL] Setup finished with one or more failures.");
                }
            });
    }

    private async Task UpdatePasswordAsync()
    {
        if (!TryReadCredentials(out var credentials, out var validationError))
        {
            MessageBox.Show(this, validationError, "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await RunOperationAsync(
            "Updating auto-login password",
            async () =>
            {
                var ok = await Task.Run(() => AutoLogonConfigurator.UpdatePassword(credentials!));
                Console.WriteLine(ok ? "[OK] Auto-login password updated." : "[FAIL] Password update failed.");
            });
    }

    private async Task TestCredentialsAsync()
    {
        if (!TryReadCredentials(out var credentials, out var validationError))
        {
            MessageBox.Show(this, validationError, "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await RunOperationAsync(
            "Testing credentials",
            async () =>
            {
                var readiness = SignInReadinessDetector.Assess(credentials!);
                if (readiness.Status == SignInReadinessStatus.Blocked)
                {
                    Console.WriteLine($"[FAIL] {readiness.Message}");
                    foreach (var step in readiness.RemediationSteps)
                        Console.WriteLine($"  - {step}");

                    var remediationText = readiness.RemediationSteps.Count == 0
                        ? string.Empty
                        : Environment.NewLine + Environment.NewLine +
                          string.Join(Environment.NewLine, readiness.RemediationSteps.Select(s => $"- {s}"));

                    MessageBox.Show(this,
                        readiness.Message + remediationText,
                        "Password Sign-In Blocked",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Console.WriteLine(readiness.Status == SignInReadinessStatus.Warning
                    ? $"[WARN] {readiness.Message}"
                    : $"[PASS] {readiness.Message}");

                var result = await Task.Run(() => CredentialValidator.Validate(credentials!));
                if (result.Status == CredentialValidationStatus.Valid)
                {
                    Console.WriteLine($"[PASS] {result.Message}");
                    Console.WriteLine("[INFO] This validates credentials only. Full auto-login still depends on policy and setup checks.");
                    return;
                }

                if (result.Status == CredentialValidationStatus.Warning)
                {
                    Console.WriteLine($"[WARN] {result.Message}");
                    MessageBox.Show(this,
                        "Credentials could not be strongly verified for this Microsoft account setup.\n" +
                        "Setup can still continue, but verify after reboot with --check or the Status tab.",
                        "Credential Test Warning",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                Console.WriteLine($"[FAIL] {result.Message}");
                MessageBox.Show(this,
                    "Credential validation failed. Check username/account type/domain/password and try again.",
                    "Credential Test Failed",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            });
    }

    private async Task RestoreAsync()
    {
        var confirm = MessageBox.Show(this,
            "This will:\n" +
            "  - Stop and remove the KeepAlive service\n" +
            "  - Restore all Windows settings to their original values\n" +
            "  - Remove the startup task\n\n" +
            "The program will remain installed so you can re-run setup later.\n" +
            "A reboot will be needed for all changes to take effect.\n\nContinue?",
            "Confirm Restore",
            MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
            return;

        await RunOperationAsync(
            "Restoring settings",
            async () =>
            {
                var exitCode = await Task.Run(RestoreManager.RunRestore);
                if (exitCode == 0)
                {
                    ResetInMemorySetupState();
                }
                Console.WriteLine(exitCode == 0
                    ? "[OK] Settings restored. The program is still installed — you can re-run setup at any time."
                    : "[WARN] Restore completed with some failures. Check output above.");
            });
    }

    private async Task UninstallAsync()
    {
        var confirm = MessageBox.Show(this,
            "This will:\n" +
            "  - Stop and remove the KeepAlive service\n" +
            "  - Restore all Windows settings to their original values\n" +
            "  - Remove the startup task, desktop shortcut, and program files\n\n" +
            "The program will be completely removed from this machine.\n" +
            "A reboot will be needed for all changes to take effect.\n\nContinue?",
            "Confirm Uninstall",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes)
            return;

        await RunOperationAsync(
            "Uninstalling",
            async () =>
            {
                var exitCode = await Task.Run(RestoreManager.RunUninstall);
                if (exitCode == 0)
                {
                    ResetInMemorySetupState();
                    _settingsPersistenceDisabled = true;
                    _applicationUninstalled = true;
                    _logTimer.Stop();
                    _updateTimer.Stop();
                }
                Console.WriteLine(exitCode == 0
                    ? "[OK] Uninstall completed. You can close this window."
                    : "[WARN] Uninstall completed with some failures. Check output above.");
            });
    }

    // ───────────────────── Service operations ─────────────────────

    private async void StartService_Click(object sender, RoutedEventArgs e) => await StartServiceAsync();
    private async void StopService_Click(object sender, RoutedEventArgs e) => await StopServiceAsync();
    private async void RestartService_Click(object sender, RoutedEventArgs e) => await RestartServiceAsync();
    private async void RunCheck_Click(object sender, RoutedEventArgs e) => await RunComplianceCheckAsync();

    private async Task StartServiceAsync()
    {
        await RunOperationAsync("Starting service", async () =>
        {
            await Task.Run(() =>
            {
                try
                {
                    using var service = new ServiceController("KeepAliveService");
                    _ = service.Status;
                    if (service.Status == ServiceControllerStatus.Running)
                    {
                        Console.WriteLine("[INFO] Service is already running.");
                        return;
                    }
                    service.Start();
                    service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                    Console.WriteLine("[OK] Service started.");
                }
                catch (InvalidOperationException ex)
                {
                    throw new InvalidOperationException("Service is not installed. Run Setup first.", ex);
                }
                catch (System.ServiceProcess.TimeoutException ex)
                {
                    throw new InvalidOperationException("Timed out while starting the service. Check service health in Services.msc.", ex);
                }
            });
        });
    }

    private async Task StopServiceAsync()
    {
        await RunOperationAsync("Stopping service", async () =>
        {
            await Task.Run(() =>
            {
                try
                {
                    using var service = new ServiceController("KeepAliveService");
                    _ = service.Status;
                    if (service.Status == ServiceControllerStatus.Stopped)
                    {
                        Console.WriteLine("[INFO] Service is already stopped.");
                        return;
                    }
                    service.Stop();
                    service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                    Console.WriteLine("[OK] Service stopped.");
                }
                catch (InvalidOperationException ex)
                {
                    throw new InvalidOperationException("Service is not installed. Run Setup first.", ex);
                }
                catch (System.ServiceProcess.TimeoutException ex)
                {
                    throw new InvalidOperationException("Timed out while stopping the service. Check service health in Services.msc.", ex);
                }
            });
        });
    }

    private async Task RestartServiceAsync()
    {
        await RunOperationAsync("Restarting service", async () =>
        {
            await Task.Run(() =>
            {
                try
                {
                    using var service = new ServiceController("KeepAliveService");
                    _ = service.Status;
                    if (service.Status != ServiceControllerStatus.Stopped)
                    {
                        service.Stop();
                        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                    }
                    service.Start();
                    service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                    Console.WriteLine("[OK] Service restarted.");
                }
                catch (InvalidOperationException ex)
                {
                    throw new InvalidOperationException("Service is not installed. Run Setup first.", ex);
                }
                catch (System.ServiceProcess.TimeoutException ex)
                {
                    throw new InvalidOperationException("Timed out while restarting the service. Check service health in Services.msc.", ex);
                }
            });
        });
    }

    // ───────────────────── Compliance check ─────────────────────

    private async void MainTabControl_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (MainTabControl.SelectedItem is System.Windows.Controls.TabItem tab && tab.Header?.ToString() == "Status")
        {
            await RunStatusTabAutoCheckAsync();
        }
    }

    private async Task RunStatusTabAutoCheckAsync()
    {
        if (_settings.SetupCompletedUtc == null)
        {
            ComplianceLabel.Text = "Setup has not been completed. Go to the Setup tab to configure this machine.";
            ComplianceLabel.Foreground = Brushes.DarkGoldenrod;
            return;
        }

        var now = DateTime.UtcNow;
        if (now - _lastStatusTabCheckUtc < TimeSpan.FromSeconds(StatusTabAutoCheckDebounceSeconds))
            return;

        _lastStatusTabCheckUtc = now;
        await RunComplianceCheckAsync();
    }

    private async Task RunComplianceCheckAsync()
    {
        await RunOperationAsync("Running compliance check", async () =>
        {
            using var capture = new StringWriter();
            using var teeWriter = new TeeTextWriter(_outputWriter, capture);

            var previousOut = Console.Out;
            var previousError = Console.Error;
            Console.SetOut(teeWriter);
            Console.SetError(teeWriter);

            int exitCode;
            try
            {
                exitCode = await Task.Run(() => ComplianceChecker.RunCheck());
            }
            finally
            {
                teeWriter.Flush();
                Console.SetOut(previousOut);
                Console.SetError(previousError);
            }

            var capturedOutput = capture.ToString();
            ParseComplianceSummary(capturedOutput, exitCode);
            LastCheckLabel.Text = $"Last check: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            StatusOutputBox.Text = capturedOutput.TrimEnd();
            StatusOutputBox.ScrollToEnd();
        });
    }

    private void ParseComplianceSummary(string output, int exitCode)
    {
        var match = Regex.Match(output,
            @"Results:\s*(\d+)\s+passed,\s*(\d+)\s+failed,\s*(\d+)\s+warnings",
            RegexOptions.IgnoreCase);

        if (match.Success)
        {
            var passed = int.Parse(match.Groups[1].Value);
            var failed = int.Parse(match.Groups[2].Value);
            var warnings = int.Parse(match.Groups[3].Value);
            var total = passed + failed;
            ComplianceLabel.Text = $"Compliance: {passed}/{total} passed ({warnings} warnings)";
            ComplianceLabel.Foreground = failed > 0
                ? Brushes.Firebrick
                : warnings > 0 ? Brushes.DarkGoldenrod : Brushes.ForestGreen;
            return;
        }

        ComplianceLabel.Text = exitCode == 0 ? "Compliance: Passed" : "Compliance: Failed";
        ComplianceLabel.Foreground = exitCode == 0 ? Brushes.ForestGreen : Brushes.Firebrick;
    }

    // ───────────────────── Update operations ─────────────────────

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(force: true);
    private async void UpdateNow_Click(object sender, RoutedEventArgs e) => await ApplyUpdateAsync();

    private async Task CheckForUpdatesAsync(bool force)
    {
        if (_isUpdateCheckRunning)
            return;

        // Skip timer-triggered checks while an operation holds Console.Out/Error redirected,
        // to avoid polluting captured compliance output.
        if (!force && _isOperationRunning)
            return;

        _isUpdateCheckRunning = true;
        try
        {
            UpdateStatusStrip.Text = "Update: checking...";
            var result = await _updateChecker.CheckForUpdateAsync(force);
            _lastUpdateResult = result;
            LastUpdateCheckLabel.Text = FormatLastUpdateCheckLabel(_settings.LastUpdateCheckUtc);

            CurrentVersionLabel.Text = $"Current: v{FormatVersion(result.CurrentVersion)}";

            if (result.LatestVersion != null)
                LatestVersionLabel.Text = $"Latest: v{FormatVersion(result.LatestVersion)}";
            else if (!string.IsNullOrWhiteSpace(result.LatestTag))
                LatestVersionLabel.Text = $"Latest tag: {result.LatestTag}";
            else
                LatestVersionLabel.Text = "Latest: unknown";

            ReleaseNotesBox.Text = string.IsNullOrWhiteSpace(result.ReleaseNotes)
                ? result.Message
                : result.ReleaseNotes;

            UpdateNowButton.IsEnabled = result.IsUpdateAvailable;
            UpdateStatusStrip.Text = result.IsUpdateAvailable
                ? $"Update available: v{FormatVersion(result.LatestVersion)}"
                : "Update: up to date";

            if (result.IsUpdateAvailable && _isStartupUpdateCheck && !_startupUpdatePromptShown)
            {
                _startupUpdatePromptShown = true;
                await PromptForStartupUpdateAsync(result);
            }

            if (force || result.WasChecked)
                Console.WriteLine($"[INFO] {result.Message}");
        }
        catch (Exception ex)
        {
            UpdateStatusStrip.Text = "Update: check failed";
            Console.WriteLine($"[FAIL] Update check failed: {ex.Message}");
        }
        finally
        {
            _isUpdateCheckRunning = false;
        }
    }

    private async Task ApplyUpdateAsync(bool skipConfirmation = false)
    {
        if (_lastUpdateResult is null || !_lastUpdateResult.IsUpdateAvailable)
            await CheckForUpdatesAsync(force: true);

        if (_lastUpdateResult is null || !_lastUpdateResult.IsUpdateAvailable)
        {
            MessageBox.Show(this, "No update is currently available.", "Updates",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!skipConfirmation)
        {
            var confirm = MessageBox.Show(this,
                $"Apply update to v{FormatVersion(_lastUpdateResult.LatestVersion)} now?\n\nThe app will restart automatically.",
                "Apply Update", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes)
                return;
        }

        var shouldClose = false;

        await RunOperationAsync("Applying update", async () =>
        {
            var progress = new Progress<DownloadProgress>(p =>
            {
                Dispatcher.Invoke(() =>
                {
                    UpdateStatusStrip.Text = p.Percentage.HasValue
                        ? $"Downloading update: {p.Percentage.Value}% ({FormatBytes(p.BytesReceived)} / {FormatBytes(p.TotalBytes)})"
                        : $"Downloading update: {FormatBytes(p.BytesReceived)}";
                });
            });

            var applyResult = await _updateChecker.ApplyUpdateAsync(_lastUpdateResult, progress);
            if (!applyResult.Started)
            {
                Console.WriteLine($"[FAIL] {applyResult.Message}");
                MessageBox.Show(this, applyResult.Message, "Update Failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            Console.WriteLine($"[OK] {applyResult.Message}");
            MessageBox.Show(this,
                "Update process has started. This window will close and relaunch after the update is applied.",
                "Updating", MessageBoxButton.OK, MessageBoxImage.Information);

            shouldClose = true;
        });

        if (shouldClose)
        {
            _allowClose = true;
            Close();
        }
    }

    private async Task PromptForStartupUpdateAsync(UpdateCheckResult result)
    {
        var latest = FormatVersion(result.LatestVersion);
        var current = FormatVersion(result.CurrentVersion);
        var prompt = MessageBox.Show(this,
            $"A new version is available.\n\nCurrent: v{current}\nLatest: v{latest}\n\nDo you want to install the update now?",
            "Update Available", MessageBoxButton.YesNo, MessageBoxImage.Information);

        if (prompt == MessageBoxResult.Yes)
            await ApplyUpdateAsync(skipConfirmation: true);
    }

    // ───────────────────── Operation wrapper ─────────────────────

    private async Task RunOperationAsync(string operationName, Func<Task> action)
    {
        if (_isOperationRunning)
            return;

        _isOperationRunning = true;
        SetControlsEnabled(false);
        var previousStatusText = UpdateStatusStrip.Text;
        UpdateStatusStrip.Text = $"{operationName}...";

        try
        {
            Console.WriteLine();
            Console.WriteLine($"[INFO] {operationName}");
            await action();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] {operationName} failed: {ex.Message}");
        }
        finally
        {
            _isOperationRunning = false;
            RefreshServiceStatus();

            if (_applicationUninstalled)
            {
                SetControlsEnabled(false);
                SetupStatusLabel.Text = "Status: Uninstalled - close this window to finish.";
                SetupStatusLabel.Foreground = Brushes.DarkGoldenrod;
                UpdateStatusStrip.Text = "Uninstalled";
            }
            else
            {
                SetControlsEnabled(true);
                RefreshSetupStatus();
                RefreshLogViewer();
                UpdateStatusStrip.Text = _lastUpdateResult?.IsUpdateAvailable == true
                    ? $"Update available: v{FormatVersion(_lastUpdateResult.LatestVersion)}"
                    : previousStatusText;
            }
        }
    }

    // ───────────────────── Credentials ─────────────────────

    private void Credential_Changed(object sender, EventArgs e) => QueueCredentialPersistence();

    private void Mode_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ApplyModeUiState();
    }

    private void Password_Changed(object sender, RoutedEventArgs e)
    {
        _passwordDecryptFailed = false;
        QueueCredentialPersistence();
    }

    private void AccountType_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ApplyAccountTypeDefaults();
        ApplyModeUiState();
        QueueCredentialPersistence();
    }

    private void ShowPassword_Changed(object sender, RoutedEventArgs e)
    {
        if (ShowPasswordCheck.IsChecked == true)
        {
            PasswordVisible.Text = PasswordHidden.Password;
            PasswordHidden.Visibility = Visibility.Collapsed;
            PasswordVisible.Visibility = Visibility.Visible;
            PasswordVisible.Focus();
        }
        else
        {
            PasswordHidden.Password = PasswordVisible.Text;
            PasswordVisible.Visibility = Visibility.Collapsed;
            PasswordHidden.Visibility = Visibility.Visible;
            PasswordHidden.Focus();
        }
    }

    private string CurrentPassword => ShowPasswordCheck.IsChecked == true
        ? PasswordVisible.Text
        : PasswordHidden.Password;

    private KeepAliveMode SelectedMode =>
        ModeBox.SelectedItem is ModeOption option
            ? option.Mode
            : KeepAliveMode.KeepAwakeOnly;

    private bool TryReadCredentials(out CredentialInfo? credentials, out string error)
    {
        credentials = null;
        error = string.Empty;

        var username = UsernameBox.Text.Trim();
        var password = CurrentPassword;

        if (string.IsNullOrWhiteSpace(username))
        {
            error = "Username is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            error = "Password is required. Use your Windows/Microsoft account password, not PIN.";
            return false;
        }

        if (AccountTypeBox.SelectedItem is not AccountTypeOption option)
        {
            error = "Select an account type.";
            return false;
        }

        credentials = new CredentialInfo(
            Username: username,
            Password: password,
            AccountType: option.Type,
            Domain: DomainBox.Text.Trim());

        FlushPendingCredentialPersistence();
        return true;
    }

    private void ApplyAccountTypeDefaults()
    {
        if (AccountTypeBox.SelectedItem is not AccountTypeOption option)
            return;

        switch (option.Type)
        {
            case AccountType.MicrosoftAccount:
                DomainBox.Text = "MicrosoftAccount";
                DomainBox.IsEnabled = false;
                break;
            case AccountType.LocalAccount:
                DomainBox.Text = Environment.MachineName;
                DomainBox.IsEnabled = true;
                break;
            case AccountType.DomainOrWorkAccount:
                DomainBox.Text = Environment.UserDomainName;
                DomainBox.IsEnabled = true;
                break;
        }
    }

    private void ApplyModeUiState()
    {
        var mode = SelectedMode;
        var usesAutoLogin = mode.UsesAutoLogin();

        UsernameBox.IsEnabled = usesAutoLogin;
        AccountTypeBox.IsEnabled = usesAutoLogin;
        DomainBox.IsEnabled = usesAutoLogin &&
                              AccountTypeBox.SelectedItem is AccountTypeOption option &&
                              option.Type != AccountType.MicrosoftAccount;
        PasswordHidden.IsEnabled = usesAutoLogin;
        PasswordVisible.IsEnabled = usesAutoLogin;
        ShowPasswordCheck.IsEnabled = usesAutoLogin;

        ModeWarningText.Text = mode switch
        {
            KeepAliveMode.KeepAwakeOnly =>
                "Only power settings and the keep-awake service will be configured.",
            KeepAliveMode.RemoteAccessWatchdog =>
                "Adds TeamViewer monitoring and network power hardening. Auto-login stays untouched.",
            KeepAliveMode.FullUnattended =>
                "Configures auto-login, lock-screen policy, Windows Update policy, network hardening, and TeamViewer monitoring.",
            _ => string.Empty,
        };

        TestCredentialsButton.IsEnabled = !_isOperationRunning && usesAutoLogin;
        UpdatePasswordButton.IsEnabled = !_isOperationRunning && usesAutoLogin;
    }

    private void QueueCredentialPersistence()
    {
        if (_suppressCredentialPersistence || _credentialPersistTimer == null)
            return;
        _credentialPersistTimer.Stop();
        _credentialPersistTimer.Start();
    }

    private void FlushPendingCredentialPersistence()
    {
        if (_settingsPersistenceDisabled)
            return;

        if (_credentialPersistTimer.IsEnabled)
            _credentialPersistTimer.Stop();
        PersistCredentialInputs();
    }

    private void LoadSavedCredentialInputs()
    {
        _suppressCredentialPersistence = true;
        try
        {
            UsernameBox.Text = string.IsNullOrWhiteSpace(_settings.SavedUsername)
                ? Environment.UserName
                : _settings.SavedUsername;

            var savedType = ParseSavedAccountType(_settings.SavedAccountType);
            SelectAccountType(savedType ?? AccountType.MicrosoftAccount);
            ApplyAccountTypeDefaults();

            if (!string.IsNullOrWhiteSpace(_settings.SavedDomain))
                DomainBox.Text = _settings.SavedDomain;

            var savedPassword = _settings.GetSavedPassword();
            _passwordDecryptFailed = !string.IsNullOrEmpty(_settings.SavedPasswordEncrypted)
                                     && savedPassword == null;
            if (!string.IsNullOrEmpty(savedPassword))
                PasswordHidden.Password = savedPassword;
        }
        finally
        {
            _suppressCredentialPersistence = false;
        }

        PersistCredentialInputs();
        ApplyModeUiState();
    }

    private void PersistCredentialInputs()
    {
        if (_suppressCredentialPersistence || _settingsPersistenceDisabled)
            return;

        try
        {
            _settings.SavedUsername = UsernameBox.Text.Trim();
            _settings.SavedDomain = DomainBox.Text.Trim();
            if (AccountTypeBox.SelectedItem is AccountTypeOption option)
                _settings.SavedAccountType = option.Type.ToString();
            if (!_passwordDecryptFailed)
                _settings.SetSavedPassword(CurrentPassword);
            _settings.Save();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Could not persist credential inputs: {ex.Message}");
        }
    }

    private static AccountType? ParseSavedAccountType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        return Enum.TryParse<AccountType>(raw, ignoreCase: true, out var parsed) ? parsed : null;
    }

    private void SelectAccountType(AccountType type)
    {
        for (var i = 0; i < AccountTypeBox.Items.Count; i++)
        {
            if (AccountTypeBox.Items[i] is AccountTypeOption option && option.Type == type)
            {
                AccountTypeBox.SelectedIndex = i;
                return;
            }
        }
    }

    private void SelectMode(KeepAliveMode mode)
    {
        for (var i = 0; i < ModeBox.Items.Count; i++)
        {
            if (ModeBox.Items[i] is ModeOption option && option.Mode == mode)
            {
                ModeBox.SelectedIndex = i;
                return;
            }
        }
    }

    private void ResetInMemorySetupState()
    {
        _settings.SetupCompletedUtc = null;
        _settings.OriginalSettingsBackup = null;
        _settings.StartupTaskUser = null;
        _settings.SetOperationMode(KeepAliveMode.KeepAwakeOnly);
        SelectMode(KeepAliveMode.KeepAwakeOnly);
        ApplyModeUiState();
    }

    private void SyncInMemorySetupState(AppSettings current)
    {
        var mode = current.GetOperationMode();
        _settings.SetupCompletedUtc = current.SetupCompletedUtc;
        _settings.OriginalSettingsBackup = current.OriginalSettingsBackup;
        _settings.StartupTaskUser = current.StartupTaskUser;
        _settings.SetOperationMode(mode);
        SelectMode(mode);
        ApplyModeUiState();
    }

    // ───────────────────── Service status ─────────────────────

    private void RefreshServiceStatus()
    {
        try
        {
            using var service = new ServiceController("KeepAliveService");
            var status = service.Status;
            var startMode = service.StartType;
            _serviceInstalled = true;
            _currentServiceStatus = status;
            ServiceStatusLabel.Text = $"Service: {status} ({startMode})";
            ServiceStatusStrip.Text = $"Service: {status}";
        }
        catch (InvalidOperationException)
        {
            _serviceInstalled = false;
            _currentServiceStatus = null;
            ServiceStatusLabel.Text = "Service: Not installed";
            ServiceStatusStrip.Text = "Service: Not installed";
        }
        catch (Exception ex)
        {
            _serviceInstalled = false;
            _currentServiceStatus = null;
            ServiceStatusLabel.Text = $"Service: Error ({ex.Message})";
            ServiceStatusStrip.Text = "Service: Error";
        }

        ApplyServiceButtonState();
    }

    // ───────────────────── Log viewer ─────────────────────

    private void RefreshLogViewer()
    {
        try
        {
            if (!File.Exists(AppSettings.LogPath))
            {
                if (_lastLogLength != 0)
                {
                    LogViewerBox.Text = string.Empty;
                    _lastLogLength = 0;
                }
                return;
            }

            var fileInfo = new FileInfo(AppSettings.LogPath);
            if (fileInfo.Length == _lastLogLength)
                return;

            using var stream = new FileStream(AppSettings.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            // Log was truncated/rotated — full re-read
            if (fileInfo.Length < _lastLogLength || _lastLogLength <= 0)
            {
                _lastLogLength = fileInfo.Length;
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var text = reader.ReadToEnd();
                if (text.Length > MaxLogChars)
                    text = text[^MaxLogChars..];
                LogViewerBox.Text = text;
                LogViewerBox.ScrollToEnd();
                return;
            }

            // Incremental tail — seek to last position and append only new content
            stream.Seek(_lastLogLength, SeekOrigin.Begin);
            _lastLogLength = fileInfo.Length;
            using var tailReader = new StreamReader(stream, Encoding.UTF8);
            var newContent = tailReader.ReadToEnd();

            if (string.IsNullOrEmpty(newContent))
                return;

            LogViewerBox.AppendText(newContent);

            // Trim from the front if total length exceeds cap
            if (LogViewerBox.Text.Length > MaxLogChars)
                LogViewerBox.Text = LogViewerBox.Text[^MaxLogChars..];

            LogViewerBox.ScrollToEnd();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Could not refresh log viewer: {ex.Message}");
        }
    }

    // ───────────────────── Output actions ─────────────────────

    private void CopySelected_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(OutputBox.Selection.Text))
            System.Windows.Clipboard.SetText(OutputBox.Selection.Text);
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e) => CopyOutputToClipboard();

    private void SelectAll_Click(object sender, RoutedEventArgs e) => OutputBox.SelectAll();

    private void CopyOutput_Click(object sender, RoutedEventArgs e) => CopyOutputToClipboard();

    private void ClearOutput_Click(object sender, RoutedEventArgs e)
    {
        _outputParagraph.Inlines.Clear();
    }

    private void CopyOutputToClipboard()
    {
        try
        {
            var range = new TextRange(OutputBox.Document.ContentStart, OutputBox.Document.ContentEnd);
            var text = range.Text;
            if (!string.IsNullOrWhiteSpace(text))
            {
                System.Windows.Clipboard.SetText(text);
                UpdateStatusStrip.Text = "Output copied to clipboard";
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Copy to clipboard failed: {ex.Message}");
        }
    }

    // ───────────────────── Tray icon ─────────────────────

    private WinFormsNotifyIcon CreateTrayIcon()
    {
        var menu = new WinFormsContextMenuStrip();
        menu.Items.Add(new WinFormsToolStripMenuItem("Open", null, (_, _) => Dispatcher.Invoke(RestoreFromTray)));
        menu.Items.Add(new WinFormsToolStripMenuItem("Exit", null, (_, _) => Dispatcher.Invoke(() =>
        {
            _allowClose = true;
            Close();
        })));

        System.Drawing.Icon? icon = null;
        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
                icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
        }
        catch { /* Best effort */ }

        var tray = new WinFormsNotifyIcon
        {
            Text = "Windows Keep Alive",
            ContextMenuStrip = menu,
            Icon = icon ?? System.Drawing.SystemIcons.Application,
            Visible = false,
        };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
        return tray;
    }

    private static async Task WaitForShellReadyAsync()
    {
        // Poll for explorer.exe — once it's running the notification area should be
        // available (or will become available shortly after via TaskbarCreated).
        const int maxWaitSeconds = 30;
        for (var i = 0; i < maxWaitSeconds; i++)
        {
            try
            {
                var explorers = System.Diagnostics.Process.GetProcessesByName("explorer");
                var found = explorers.Length > 0;
                foreach (var p in explorers) p.Dispose();
                if (found) return;
            }
            catch
            {
                // Best effort.
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        Console.WriteLine("[WARN] Shell readiness timeout — explorer.exe not detected after 30 s. Proceeding anyway.");
    }

    private void InstallTaskbarCreatedHook()
    {
        try
        {
            _taskbarCreatedMessageId = NativeMethods.RegisterWindowMessage("TaskbarCreated");
            if (_taskbarCreatedMessageId != 0)
            {
                var hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
                hwndSource?.AddHook(WndProc);
            }
        }
        catch
        {
            // Best effort — tray icon will still work, just won't survive explorer restarts.
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_taskbarCreatedMessageId != 0 && msg == (int)_taskbarCreatedMessageId)
        {
            // Explorer has (re)created the taskbar. Re-show the tray icon only
            // if we are actually hidden to tray (not just minimized normally).
            // Toggle Visible off→on to force Shell_NotifyIcon(NIM_ADD)
            // re-registration with the new shell.
            if (_isHiddenToTray)
            {
                _trayIcon.Visible = false;
                _trayIcon.Visible = true;
            }

            handled = false;
        }

        return IntPtr.Zero;
    }

    private void TryAutoRepairStartupTask()
    {
        try
        {
            if (_settings.SetupCompletedUtc == null || !IsRunningAsAdmin())
                return;

            var mode = _settings.GetOperationMode();
            var (exists, correct, detail) = StartupTaskManager.ValidateTask(mode);
            if (exists && correct)
                return;

            Console.WriteLine($"[INFO] Startup task needs repair: {detail ?? "missing"}. Re-creating...");
            if (StartupTaskManager.EnsureTask(mode))
                Console.WriteLine("[INFO] Startup task repaired successfully.");
            else
                Console.WriteLine("[WARN] Startup task repair failed.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Startup task auto-repair failed: {ex.Message}");
        }
    }

    private void HideToTray()
    {
        Hide();
        _isHiddenToTray = true;
        _trayIcon.Visible = true;

        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _trayIcon.ShowBalloonTip(2500, "Windows Keep Alive",
                "The app is still running in the notification area.",
                WinFormsToolTipIcon.Info);
        }
    }

    private void RestoreFromTray()
    {
        _isHiddenToTray = false;
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        _trayIcon.Visible = false;
        Activate();
    }

    public void ActivateFromExternalLaunch()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;

        if (!Dispatcher.CheckAccess())
        {
            try { Dispatcher.BeginInvoke(ActivateFromExternalLaunch); }
            catch { /* Best effort */ }
            return;
        }

        if (!IsVisible || WindowState == WindowState.Minimized || _trayIcon.Visible)
        {
            RestoreFromTray();
        }
        else
        {
            Show();
            WindowState = WindowState.Normal;
        }

        Topmost = true;
        Topmost = false;
        Activate();
        Focus();
    }

    // ───────────────────── Window lifecycle ─────────────────────

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        FlushPendingCredentialPersistence();

        if (_allowClose || !IsVisible)
            return;

        e.Cancel = true;
        HideToTray();
    }

    private void RestoreConsoleOutput()
    {
        FlushPendingCredentialPersistence();
        _updateTimer.Stop();
        _logTimer.Stop();
        _credentialPersistTimer.Stop();
        _trayIcon.Visible = false;
        var trayIcon = _trayIcon.Icon;
        _trayIcon.Dispose();
        if (trayIcon != null && trayIcon != System.Drawing.SystemIcons.Application)
            trayIcon.Dispose();
        _outputWriter.Flush();
        Console.SetOut(_originalConsoleOut);
        Console.SetError(_originalConsoleError);
        _updateChecker.Dispose();
    }

    // ───────────────────── UI state helpers ─────────────────────

    private void SetControlsEnabled(bool enabled)
    {
        ModeBox.IsEnabled = enabled;
        RunSetupButton.IsEnabled = enabled;
        TestCredentialsButton.IsEnabled = enabled && SelectedMode.UsesAutoLogin();
        UpdatePasswordButton.IsEnabled = enabled && SelectedMode.UsesAutoLogin();
        RestoreButton.IsEnabled = enabled;
        UninstallButton.IsEnabled = enabled;
        RunCheckButton.IsEnabled = enabled;
        CheckUpdatesButton.IsEnabled = enabled;
        UpdateNowButton.IsEnabled = enabled && (_lastUpdateResult?.IsUpdateAvailable ?? false);

        if (!enabled)
        {
            StartServiceButton.IsEnabled = false;
            StopServiceButton.IsEnabled = false;
            RestartServiceButton.IsEnabled = false;
            return;
        }

        ApplyModeUiState();
        ApplyServiceButtonState();
    }

    private void RefreshSetupStatus()
    {
        // Reload from disk in case setup/restore/uninstall changed the state
        var current = AppSettings.Load(createIfMissing: !_applicationUninstalled);

        if (current.SetupCompletedUtc != null)
        {
            var completed = current.SetupCompletedUtc.Value.ToLocalTime();
            SetupStatusLabel.Text = $"Status: Active - {current.GetOperationMode().ToDisplayName()} setup completed {completed:yyyy-MM-dd HH:mm}";
            SetupStatusLabel.Foreground = Brushes.ForestGreen;
        }
        else
        {
            SetupStatusLabel.Text = "Status: Not configured - choose a mode and click Run Setup to get started.";
            SetupStatusLabel.Foreground = Brushes.DarkGoldenrod;
        }
    }

    private void ApplyServiceButtonState()
    {
        if (_isOperationRunning || !_serviceInstalled || _currentServiceStatus == null)
        {
            StartServiceButton.IsEnabled = false;
            StopServiceButton.IsEnabled = false;
            RestartServiceButton.IsEnabled = false;
            return;
        }

        switch (_currentServiceStatus.Value)
        {
            case ServiceControllerStatus.Running:
                StartServiceButton.IsEnabled = false;
                StopServiceButton.IsEnabled = true;
                RestartServiceButton.IsEnabled = true;
                break;
            case ServiceControllerStatus.Stopped:
                StartServiceButton.IsEnabled = true;
                StopServiceButton.IsEnabled = false;
                RestartServiceButton.IsEnabled = false;
                break;
            default:
                StartServiceButton.IsEnabled = true;
                StopServiceButton.IsEnabled = true;
                RestartServiceButton.IsEnabled = true;
                break;
        }
    }

    private void TryApplyWindowIcon()
    {
        try
        {
            Icon = new BitmapImage(new Uri("pack://application:,,,/assets/app.ico"));
        }
        catch
        {
            // Best effort.
        }
    }

    // ───────────────────── Static helpers ─────────────────────

    private static string FormatLastUpdateCheckLabel(DateTime? utc)
    {
        return utc.HasValue
            ? $"Last checked: {utc.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
            : "Last checked: never";
    }

    internal static string FormatVersion(Version? version)
    {
        if (version == null)
            return "unknown";
        var build = version.Build < 0 ? 0 : version.Build;
        return $"{version.Major}.{version.Minor}.{build}";
    }

    private static string FormatBytes(long? bytes) => Helpers.FormatBytes(bytes);

    private static bool IsRunningAsAdmin() => Helpers.IsRunningAsAdmin();

    // ───────────────────── Inner types ─────────────────────

    private sealed class AccountTypeOption(string name, AccountType type)
    {
        public string Name { get; } = name;
        public AccountType Type { get; } = type;
        public override string ToString() => Name;
    }

    private sealed class ModeOption(string name, KeepAliveMode mode)
    {
        public string Name { get; } = name;
        public KeepAliveMode Mode { get; } = mode;
        public override string ToString() => Name;
    }

    private sealed class TeeTextWriter(params TextWriter[] writers) : TextWriter
    {
        private readonly TextWriter[] _writers = writers;
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            foreach (var writer in _writers) writer.Write(value);
        }

        public override void Write(string? value)
        {
            foreach (var writer in _writers) writer.Write(value);
        }

        public override void WriteLine(string? value)
        {
            foreach (var writer in _writers) writer.WriteLine(value);
        }

        public override void Flush()
        {
            foreach (var writer in _writers) writer.Flush();
        }
    }
}
