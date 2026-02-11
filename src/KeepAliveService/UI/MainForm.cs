using System.Reflection;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using KeepAliveService.Setup;
using KeepAliveService.Update;

namespace KeepAliveService.UI;

public sealed class MainForm : Form
{
    private const int MaxLogChars = 250_000;

    private readonly AppSettings _settings;
    private readonly GitHubUpdateChecker _updateChecker;
    private readonly TextWriter _originalConsoleOut;
    private readonly TextWriter _originalConsoleError;

    private readonly RichTextBox _setupOutputBox;
    private readonly RichTextBox _logViewerBox;
    private readonly TextBox _usernameTextBox;
    private readonly ComboBox _accountTypeComboBox;
    private readonly TextBox _domainTextBox;
    private readonly TextBox _passwordTextBox;
    private readonly Button _runSetupButton;
    private readonly Button _testCredentialsButton;
    private readonly Button _updatePasswordButton;
    private readonly Button _uninstallButton;
    private readonly Button _runCheckButton;
    private readonly Button _startServiceButton;
    private readonly Button _stopServiceButton;
    private readonly Button _restartServiceButton;
    private readonly Label _serviceStatusLabel;
    private readonly Label _complianceLabel;
    private readonly Label _lastCheckLabel;
    private readonly Label _currentVersionLabel;
    private readonly Label _latestVersionLabel;
    private readonly RichTextBox _releaseNotesBox;
    private readonly Button _checkUpdatesButton;
    private readonly Button _updateNowButton;
    private readonly ToolStripStatusLabel _versionStatus;
    private readonly ToolStripStatusLabel _adminStatus;
    private readonly ToolStripStatusLabel _serviceStatusStrip;
    private readonly ToolStripStatusLabel _updateStatusStrip;
    private readonly RichTextBoxWriter _richTextWriter;
    private readonly System.Windows.Forms.Timer _updateTimer;
    private readonly System.Windows.Forms.Timer _logTimer;
    private readonly System.Windows.Forms.Timer _credentialPersistTimer;
    private readonly NotifyIcon _trayIcon;

    private bool _isOperationRunning;
    private bool _isUpdateCheckRunning;
    private bool _suppressCredentialPersistence;
    private bool _statusTabAutoCheckTriggered;
    private bool _isStartupUpdateCheck;
    private bool _startupUpdatePromptShown;
    private bool _allowClose;
    private bool _trayHintShown;
    private long _lastLogLength = -1;
    private UpdateCheckResult? _lastUpdateResult;

    public MainForm()
    {
        _settings = AppSettings.Load();
        _updateChecker = new GitHubUpdateChecker(_settings);
        _originalConsoleOut = Console.Out;
        _originalConsoleError = Console.Error;

        Text = "Windows Keep Alive";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 650);
        Size = new Size(1080, 760);
        TryApplyAppIcon();
        _trayIcon = CreateTrayIcon();

        var tabControl = new TabControl
        {
            Dock = DockStyle.Fill,
        };

        var setupTab = new TabPage("Setup");
        var statusTab = new TabPage("Status");
        var updatesTab = new TabPage("Updates");
        var logsTab = new TabPage("Logs");
        tabControl.TabPages.Add(setupTab);
        tabControl.TabPages.Add(statusTab);
        tabControl.TabPages.Add(updatesTab);
        tabControl.TabPages.Add(logsTab);
        tabControl.Selected += async (_, _) =>
        {
            if (tabControl.SelectedTab == statusTab && !_statusTabAutoCheckTriggered)
            {
                _statusTabAutoCheckTriggered = true;
                await RunComplianceCheckAsync();
            }
        };
        Controls.Add(tabControl);

        var statusStrip = new StatusStrip();
        _versionStatus = new ToolStripStatusLabel();
        _adminStatus = new ToolStripStatusLabel();
        _serviceStatusStrip = new ToolStripStatusLabel();
        _updateStatusStrip = new ToolStripStatusLabel("Update: idle");
        var copyStatus = new ToolStripStatusLabel("Copy") { IsLink = true };
        copyStatus.Click += (_, _) => CopyOutputToClipboard();
        var clearStatus = new ToolStripStatusLabel("Clear") { IsLink = true };
        clearStatus.Click += (_, _) =>
        {
            _setupOutputBox?.Clear();
        };

        statusStrip.Items.Add(_versionStatus);
        statusStrip.Items.Add(new ToolStripStatusLabel(" | "));
        statusStrip.Items.Add(_adminStatus);
        statusStrip.Items.Add(new ToolStripStatusLabel(" | "));
        statusStrip.Items.Add(_serviceStatusStrip);
        statusStrip.Items.Add(new ToolStripStatusLabel(" | "));
        statusStrip.Items.Add(_updateStatusStrip);
        statusStrip.Items.Add(new ToolStripStatusLabel { Spring = true });
        statusStrip.Items.Add(copyStatus);
        statusStrip.Items.Add(new ToolStripStatusLabel(" "));
        statusStrip.Items.Add(clearStatus);
        Controls.Add(statusStrip);

        // Setup tab
        var setupRoot = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(10),
        };
        setupRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        setupRoot.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        setupTab.Controls.Add(setupRoot);

        var quickSetupGroup = new GroupBox
        {
            Text = "Quick Setup",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12),
        };
        setupRoot.Controls.Add(quickSetupGroup, 0, 0);

        var quickSetupLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 5,
            AutoSize = true,
        };
        quickSetupLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        quickSetupLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        quickSetupLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        quickSetupLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        quickSetupGroup.Controls.Add(quickSetupLayout);

        var setupDescription = new Label
        {
            AutoSize = true,
            Text = "Configure keep-alive, auto-login, update policy, network, and service with one click.",
            Margin = new Padding(0, 0, 0, 12),
        };
        quickSetupLayout.Controls.Add(setupDescription, 0, 0);
        quickSetupLayout.SetColumnSpan(setupDescription, 4);

        quickSetupLayout.Controls.Add(new Label { Text = "Username:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        _usernameTextBox = new TextBox { Dock = DockStyle.Fill };
        quickSetupLayout.Controls.Add(_usernameTextBox, 1, 1);
        quickSetupLayout.SetColumnSpan(_usernameTextBox, 3);

        quickSetupLayout.Controls.Add(new Label { Text = "Account:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        _accountTypeComboBox = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _accountTypeComboBox.Items.Add(new AccountTypeOption("Microsoft Account", AccountType.MicrosoftAccount));
        _accountTypeComboBox.Items.Add(new AccountTypeOption("Local Account", AccountType.LocalAccount));
        _accountTypeComboBox.Items.Add(new AccountTypeOption("Domain / Work Account", AccountType.DomainOrWorkAccount));
        _accountTypeComboBox.SelectedIndex = 0;
        _accountTypeComboBox.SelectedIndexChanged += (_, _) =>
        {
            ApplyAccountTypeDefaults();
            QueueCredentialPersistence();
        };
        quickSetupLayout.Controls.Add(_accountTypeComboBox, 1, 2);

        quickSetupLayout.Controls.Add(new Label { Text = "Domain:", AutoSize = true, Anchor = AnchorStyles.Left }, 2, 2);
        _domainTextBox = new TextBox { Dock = DockStyle.Fill };
        _domainTextBox.TextChanged += (_, _) => QueueCredentialPersistence();
        quickSetupLayout.Controls.Add(_domainTextBox, 3, 2);

        quickSetupLayout.Controls.Add(new Label { Text = "Password:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
        _passwordTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            PasswordChar = '*',
        };
        _passwordTextBox.TextChanged += (_, _) => QueueCredentialPersistence();
        quickSetupLayout.Controls.Add(_passwordTextBox, 1, 3);
        quickSetupLayout.SetColumnSpan(_passwordTextBox, 3);

        _usernameTextBox.TextChanged += (_, _) => QueueCredentialPersistence();

        var setupButtonFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 12, 0, 0),
        };
        _runSetupButton = new Button
        {
            Text = "Run Setup",
            AutoSize = true,
        };
        _runSetupButton.Click += async (_, _) => await RunSetupAsync();
        setupButtonFlow.Controls.Add(_runSetupButton);

        _testCredentialsButton = new Button
        {
            Text = "Test Credentials",
            AutoSize = true,
        };
        _testCredentialsButton.Click += async (_, _) => await TestCredentialsAsync();
        setupButtonFlow.Controls.Add(_testCredentialsButton);

        _updatePasswordButton = new Button
        {
            Text = "Update Password",
            AutoSize = true,
        };
        _updatePasswordButton.Click += async (_, _) => await UpdatePasswordAsync();
        setupButtonFlow.Controls.Add(_updatePasswordButton);

        _uninstallButton = new Button
        {
            Text = "Uninstall",
            AutoSize = true,
        };
        _uninstallButton.Click += async (_, _) => await UninstallAsync();
        setupButtonFlow.Controls.Add(_uninstallButton);

        quickSetupLayout.Controls.Add(setupButtonFlow, 0, 4);
        quickSetupLayout.SetColumnSpan(setupButtonFlow, 4);

        var outputGroup = new GroupBox
        {
            Text = "Output",
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
        };
        _setupOutputBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = Color.FromArgb(28, 31, 36),
            ForeColor = Color.Gainsboro,
            BorderStyle = BorderStyle.None,
            Font = new Font("Consolas", 10.5f),
            WordWrap = false,
        };
        var outputMenu = new ContextMenuStrip();
        outputMenu.Items.Add("Copy Selected", null, (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_setupOutputBox.SelectedText))
            {
                Clipboard.SetText(_setupOutputBox.SelectedText);
            }
        });
        outputMenu.Items.Add("Copy All", null, (_, _) => CopyOutputToClipboard());
        outputMenu.Items.Add("Select All", null, (_, _) => _setupOutputBox.SelectAll());
        _setupOutputBox.ContextMenuStrip = outputMenu;
        outputGroup.Controls.Add(_setupOutputBox);
        setupRoot.Controls.Add(outputGroup, 0, 1);

        // Status tab
        var statusLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(16),
        };
        statusLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        statusLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        statusLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        statusLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        statusLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        statusTab.Controls.Add(statusLayout);

        _serviceStatusLabel = new Label { AutoSize = true };
        _complianceLabel = new Label { AutoSize = true, Text = "Compliance: not checked" };
        _lastCheckLabel = new Label { AutoSize = true, Text = "Last check: never" };
        _runCheckButton = new Button { Text = "Run Check", AutoSize = true };
        _runCheckButton.Click += async (_, _) => await RunComplianceCheckAsync();
        _startServiceButton = new Button { Text = "Start Service", AutoSize = true };
        _startServiceButton.Click += async (_, _) => await StartServiceAsync();
        _stopServiceButton = new Button { Text = "Stop Service", AutoSize = true };
        _stopServiceButton.Click += async (_, _) => await StopServiceAsync();
        _restartServiceButton = new Button { Text = "Restart Service", AutoSize = true };
        _restartServiceButton.Click += async (_, _) => await RestartServiceAsync();

        var statusButtonRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };
        statusButtonRow.Controls.Add(_runCheckButton);
        statusButtonRow.Controls.Add(_startServiceButton);
        statusButtonRow.Controls.Add(_stopServiceButton);
        statusButtonRow.Controls.Add(_restartServiceButton);

        statusLayout.Controls.Add(_serviceStatusLabel, 0, 0);
        statusLayout.Controls.Add(_complianceLabel, 0, 1);
        statusLayout.Controls.Add(_lastCheckLabel, 0, 2);
        statusLayout.Controls.Add(statusButtonRow, 0, 3);

        // Updates tab
        var updatesLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(16),
        };
        updatesLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        updatesLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        updatesLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        updatesLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        updatesLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        updatesTab.Controls.Add(updatesLayout);

        _currentVersionLabel = new Label { AutoSize = true };
        _latestVersionLabel = new Label { AutoSize = true, Text = "Latest: unknown" };
        var releaseNotesLabel = new Label { AutoSize = true, Text = "Release notes:" };
        _releaseNotesBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            Font = new Font("Consolas", 10),
            BackColor = Color.FromArgb(245, 245, 245),
        };

        var updateButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };
        _checkUpdatesButton = new Button { Text = "Check Now", AutoSize = true };
        _checkUpdatesButton.Click += async (_, _) => await CheckForUpdatesAsync(force: true);
        updateButtons.Controls.Add(_checkUpdatesButton);

        _updateNowButton = new Button { Text = "Update Now", AutoSize = true, Enabled = false };
        _updateNowButton.Click += async (_, _) => await ApplyUpdateAsync();
        updateButtons.Controls.Add(_updateNowButton);

        updatesLayout.Controls.Add(_currentVersionLabel, 0, 0);
        updatesLayout.Controls.Add(_latestVersionLabel, 0, 1);
        updatesLayout.Controls.Add(releaseNotesLabel, 0, 2);
        updatesLayout.Controls.Add(_releaseNotesBox, 0, 3);
        updatesLayout.Controls.Add(updateButtons, 0, 4);

        // Logs tab
        _logViewerBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            Font = new Font("Consolas", 10),
            BackColor = Color.FromArgb(245, 245, 245),
            WordWrap = false,
        };
        logsTab.Controls.Add(_logViewerBox);

        _richTextWriter = new RichTextBoxWriter(_setupOutputBox);
        Console.SetOut(_richTextWriter);
        Console.SetError(_richTextWriter);

        _updateTimer = new System.Windows.Forms.Timer
        {
            Interval = ToTimerIntervalMilliseconds(_settings.UpdateCheckIntervalHours),
        };
        _updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync(force: false);

        _logTimer = new System.Windows.Forms.Timer
        {
            Interval = 5000,
        };
        _logTimer.Tick += (_, _) => RefreshLogViewer();

        _credentialPersistTimer = new System.Windows.Forms.Timer
        {
            Interval = 1000,
        };
        _credentialPersistTimer.Tick += (_, _) =>
        {
            _credentialPersistTimer.Stop();
            PersistCredentialInputs();
        };

        _versionStatus.Text = $"v{FormatVersion(Assembly.GetExecutingAssembly().GetName().Version)}";
        _adminStatus.Text = $"Admin: {(IsRunningAsAdmin() ? "Yes" : "No")}";

        Shown += async (_, _) => await OnShownAsync();
        FormClosing += OnFormClosing;
        FormClosed += (_, _) => RestoreConsoleOutput();
    }

    private async Task OnShownAsync()
    {
        InstallManager.EnsureProgramDataLayout();
        _settings.InstallPath = InstallManager.CanonicalExePath;
        _settings.Save();

        LoadSavedCredentialInputs();

        Console.WriteLine("[INFO] Windows Keep Alive GUI started.");
        Console.WriteLine($"[INFO] Install path: {InstallManager.CanonicalExePath}");

        RefreshServiceStatus();
        RefreshLogViewer();
        _logTimer.Start();

        // Always perform a real update check on app startup.
        _isStartupUpdateCheck = true;
        await CheckForUpdatesAsync(force: true);
        _isStartupUpdateCheck = false;
        _updateTimer.Start();
    }

    private async Task RunSetupAsync()
    {
        if (!TryReadCredentials(out var credentials, out var validationError))
        {
            MessageBox.Show(validationError, "Invalid Input", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        await RunOperationAsync(
            "Running setup",
            async () =>
            {
                var exitCode = await Task.Run(() => SetupManager.RunSetup(credentials!));
                if (exitCode == 0)
                {
                    Console.WriteLine("[OK] Setup completed successfully.");
                }
                else
                {
                    Console.WriteLine("[FAIL] Setup finished with one or more failures.");
                }
            });
    }

    private async Task UpdatePasswordAsync()
    {
        if (!TryReadCredentials(out var credentials, out var validationError))
        {
            MessageBox.Show(validationError, "Invalid Input", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        await RunOperationAsync(
            "Updating auto-login password",
            async () =>
            {
                var ok = await Task.Run(() => AutoLogonConfigurator.UpdatePassword(credentials!));
                if (ok)
                {
                    Console.WriteLine("[OK] Auto-login password updated.");
                }
                else
                {
                    Console.WriteLine("[FAIL] Password update failed.");
                }
            });
    }

    private async Task TestCredentialsAsync()
    {
        if (!TryReadCredentials(out var credentials, out var validationError))
        {
            MessageBox.Show(validationError, "Invalid Input", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
                    {
                        Console.WriteLine($"  - {step}");
                    }

                    var remediationText = readiness.RemediationSteps.Count == 0
                        ? string.Empty
                        : Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, readiness.RemediationSteps.Select(s => $"- {s}"));

                    MessageBox.Show(
                        readiness.Message + remediationText,
                        "Password Sign-In Blocked",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }

                if (readiness.Status == SignInReadinessStatus.Warning)
                {
                    Console.WriteLine($"[WARN] {readiness.Message}");
                }
                else
                {
                    Console.WriteLine($"[PASS] {readiness.Message}");
                }

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
                    MessageBox.Show(
                        "Credentials could not be strongly verified for this Microsoft account setup.\n" +
                        "Setup can still continue, but verify after reboot with --check or the Status tab.",
                        "Credential Test Warning",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }

                Console.WriteLine($"[FAIL] {result.Message}");
                MessageBox.Show(
                    "Credential validation failed. Check username/account type/domain/password and try again.",
                    "Credential Test Failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            });
    }

    private async Task UninstallAsync()
    {
        var confirm = MessageBox.Show(
            "This will stop and remove the KeepAlive service. Continue?",
            "Confirm Uninstall",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (confirm != DialogResult.Yes)
        {
            return;
        }

        await RunOperationAsync(
            "Uninstalling service",
            async () =>
            {
                await Task.Run(ServiceInstaller.Uninstall);
                Console.WriteLine("[OK] Uninstall flow completed.");
            });
    }

    private async Task StartServiceAsync()
    {
        await RunOperationAsync(
            "Starting service",
            async () =>
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
                        throw new InvalidOperationException(
                            "Timed out while starting the service. Check service health in Services.msc.",
                            ex);
                    }
                });
            });
    }

    private async Task StopServiceAsync()
    {
        await RunOperationAsync(
            "Stopping service",
            async () =>
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
                        throw new InvalidOperationException(
                            "Timed out while stopping the service. Check service health in Services.msc.",
                            ex);
                    }
                });
            });
    }

    private async Task RestartServiceAsync()
    {
        await RunOperationAsync(
            "Restarting service",
            async () =>
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
                        throw new InvalidOperationException(
                            "Timed out while restarting the service. Check service health in Services.msc.",
                            ex);
                    }
                });
            });
    }

    private async Task RunComplianceCheckAsync()
    {
        await RunOperationAsync(
            "Running compliance check",
            async () =>
            {
                var capture = new StringWriter();
                var teeWriter = new TeeTextWriter(_richTextWriter, capture);

                var previousOut = Console.Out;
                var previousError = Console.Error;
                Console.SetOut(teeWriter);
                Console.SetError(teeWriter);

                int exitCode;
                try
                {
                    exitCode = await Task.Run(ComplianceChecker.RunCheck);
                }
                finally
                {
                    teeWriter.Flush();
                    Console.SetOut(previousOut);
                    Console.SetError(previousError);
                }

                ParseComplianceSummary(capture.ToString(), exitCode);
                _lastCheckLabel.Text = $"Last check: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            });
    }

    private async Task CheckForUpdatesAsync(bool force)
    {
        if (_isUpdateCheckRunning)
        {
            return;
        }

        _isUpdateCheckRunning = true;
        try
        {
            _updateStatusStrip.Text = "Update: checking...";
            var result = await _updateChecker.CheckForUpdateAsync(force);
            _lastUpdateResult = result;

            _currentVersionLabel.Text = $"Current: v{FormatVersion(result.CurrentVersion)}";

            if (result.LatestVersion != null)
            {
                _latestVersionLabel.Text = $"Latest: v{FormatVersion(result.LatestVersion)}";
            }
            else if (!string.IsNullOrWhiteSpace(result.LatestTag))
            {
                _latestVersionLabel.Text = $"Latest tag: {result.LatestTag}";
            }
            else
            {
                _latestVersionLabel.Text = "Latest: unknown";
            }

            _releaseNotesBox.Text = string.IsNullOrWhiteSpace(result.ReleaseNotes)
                ? result.Message
                : result.ReleaseNotes;

            _updateNowButton.Enabled = result.IsUpdateAvailable;
            _updateStatusStrip.Text = result.IsUpdateAvailable
                ? $"Update available: v{FormatVersion(result.LatestVersion)}"
                : "Update: up to date";

            if (result.IsUpdateAvailable && _isStartupUpdateCheck && !_startupUpdatePromptShown)
            {
                _startupUpdatePromptShown = true;
                await PromptForStartupUpdateAsync(result);
            }

            if (force || result.WasChecked)
            {
                Console.WriteLine($"[INFO] {result.Message}");
            }
        }
        catch (Exception ex)
        {
            _updateStatusStrip.Text = "Update: check failed";
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
        {
            await CheckForUpdatesAsync(force: true);
        }

        if (_lastUpdateResult is null || !_lastUpdateResult.IsUpdateAvailable)
        {
            MessageBox.Show("No update is currently available.", "Updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!skipConfirmation)
        {
            var confirm = MessageBox.Show(
                $"Apply update to v{FormatVersion(_lastUpdateResult.LatestVersion)} now?\n\nThe app will restart automatically.",
                "Apply Update",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (confirm != DialogResult.Yes)
            {
                return;
            }
        }

        await RunOperationAsync(
            "Applying update",
            async () =>
            {
                var progress = new Progress<DownloadProgress>(p =>
                {
                    if (p.Percentage.HasValue)
                    {
                        _updateStatusStrip.Text =
                            $"Downloading update: {p.Percentage.Value}% ({FormatBytes(p.BytesReceived)} / {FormatBytes(p.TotalBytes)})";
                    }
                    else
                    {
                        _updateStatusStrip.Text = $"Downloading update: {FormatBytes(p.BytesReceived)}";
                    }
                });

                var applyResult = await _updateChecker.ApplyUpdateAsync(_lastUpdateResult, progress);
                if (!applyResult.Started)
                {
                    Console.WriteLine($"[FAIL] {applyResult.Message}");
                    MessageBox.Show(applyResult.Message, "Update Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                Console.WriteLine($"[OK] {applyResult.Message}");
                MessageBox.Show(
                    "Update process has started. This window will close and relaunch after the update is applied.",
                    "Updating",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);

                _allowClose = true;
                Close();
            });
    }

    private async Task RunOperationAsync(string operationName, Func<Task> action)
    {
        if (_isOperationRunning)
        {
            return;
        }

        _isOperationRunning = true;
        SetControlsEnabled(false);
        _updateStatusStrip.Text = $"{operationName}...";

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
            RefreshServiceStatus();
            RefreshLogViewer();
            _updateStatusStrip.Text = "Update: idle";
            SetControlsEnabled(true);
            _isOperationRunning = false;
        }
    }

    private async Task PromptForStartupUpdateAsync(UpdateCheckResult result)
    {
        var latest = FormatVersion(result.LatestVersion);
        var current = FormatVersion(result.CurrentVersion);
        var prompt = MessageBox.Show(
            $"A new version is available.\n\nCurrent: v{current}\nLatest: v{latest}\n\nDo you want to install the update now?",
            "Update Available",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Information);

        if (prompt == DialogResult.Yes)
        {
            await ApplyUpdateAsync(skipConfirmation: true);
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        FlushPendingCredentialPersistence();

        if (_allowClose || e.CloseReason != CloseReason.UserClosing)
        {
            return;
        }

        e.Cancel = true;
        HideToTray();
    }

    private NotifyIcon CreateTrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Exit", null, (_, _) =>
        {
            _allowClose = true;
            Close();
        });

        var tray = new NotifyIcon
        {
            Text = "Windows Keep Alive",
            ContextMenuStrip = menu,
            Icon = Icon ?? SystemIcons.Application,
            Visible = false,
        };
        tray.DoubleClick += (_, _) => RestoreFromTray();
        return tray;
    }

    private void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
        _trayIcon.Visible = true;

        if (_trayHintShown)
        {
            return;
        }

        _trayHintShown = true;
        _trayIcon.ShowBalloonTip(
            2500,
            "Windows Keep Alive",
            "The app is still running in the notification area.",
            ToolTipIcon.Info);
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        ShowInTaskbar = true;
        _trayIcon.Visible = false;
        Activate();
    }

    public void ActivateFromExternalLaunch()
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke((System.Windows.Forms.MethodInvoker)ActivateFromExternalLaunch);
            }
            catch
            {
                // Best effort only.
            }

            return;
        }

        if (!Visible || !ShowInTaskbar || WindowState == FormWindowState.Minimized || _trayIcon.Visible)
        {
            RestoreFromTray();
        }
        else
        {
            Show();
            WindowState = FormWindowState.Normal;
        }

        TopMost = true;
        TopMost = false;
        BringToFront();
        Activate();
        Focus();
    }

    private void QueueCredentialPersistence()
    {
        if (_suppressCredentialPersistence)
        {
            return;
        }

        _credentialPersistTimer.Stop();
        _credentialPersistTimer.Start();
    }

    private void FlushPendingCredentialPersistence()
    {
        if (_credentialPersistTimer.Enabled)
        {
            _credentialPersistTimer.Stop();
        }

        PersistCredentialInputs();
    }

    private void RefreshServiceStatus()
    {
        try
        {
            using var service = new ServiceController("KeepAliveService");
            var status = service.Status;
            var startMode = service.StartType;
            _serviceStatusLabel.Text = $"Service: {status} ({startMode})";
            _serviceStatusStrip.Text = $"Service: {status}";
        }
        catch (InvalidOperationException)
        {
            _serviceStatusLabel.Text = "Service: Not installed";
            _serviceStatusStrip.Text = "Service: Not installed";
        }
        catch (Exception ex)
        {
            _serviceStatusLabel.Text = $"Service: Error ({ex.Message})";
            _serviceStatusStrip.Text = "Service: Error";
        }
    }

    private void RefreshLogViewer()
    {
        try
        {
            if (!File.Exists(AppSettings.LogPath))
            {
                if (_lastLogLength != 0)
                {
                    _logViewerBox.Text = string.Empty;
                    _lastLogLength = 0;
                }

                return;
            }

            var fileInfo = new FileInfo(AppSettings.LogPath);
            if (fileInfo.Length == _lastLogLength)
            {
                return;
            }

            _lastLogLength = fileInfo.Length;

            using var stream = new FileStream(AppSettings.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();
            if (text.Length > MaxLogChars)
            {
                text = text[^MaxLogChars..];
            }

            _logViewerBox.Text = text;
            _logViewerBox.SelectionStart = _logViewerBox.TextLength;
            _logViewerBox.ScrollToCaret();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Could not refresh log viewer: {ex.Message}");
        }
    }

    private void ParseComplianceSummary(string output, int exitCode)
    {
        var match = Regex.Match(
            output,
            @"Results:\s*(\d+)\s+passed,\s*(\d+)\s+failed,\s*(\d+)\s+warnings",
            RegexOptions.IgnoreCase);

        if (match.Success)
        {
            var passed = int.Parse(match.Groups[1].Value);
            var failed = int.Parse(match.Groups[2].Value);
            var warnings = int.Parse(match.Groups[3].Value);
            var total = passed + failed;
            _complianceLabel.Text = $"Compliance: {passed}/{total} passed ({warnings} warnings)";
            return;
        }

        _complianceLabel.Text = exitCode == 0
            ? "Compliance: Passed"
            : "Compliance: Failed";
    }

    private bool TryReadCredentials(out CredentialInfo? credentials, out string error)
    {
        credentials = null;
        error = string.Empty;

        var username = _usernameTextBox.Text.Trim();
        var password = _passwordTextBox.Text;

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

        if (_accountTypeComboBox.SelectedItem is not AccountTypeOption option)
        {
            error = "Select an account type.";
            return false;
        }

        credentials = new CredentialInfo(
            Username: username,
            Password: password,
            AccountType: option.Type,
            Domain: _domainTextBox.Text.Trim());

        FlushPendingCredentialPersistence();

        return true;
    }

    private void ApplyAccountTypeDefaults()
    {
        if (_accountTypeComboBox.SelectedItem is not AccountTypeOption option)
        {
            return;
        }

        switch (option.Type)
        {
            case AccountType.MicrosoftAccount:
                _domainTextBox.Text = "MicrosoftAccount";
                _domainTextBox.Enabled = false;
                break;
            case AccountType.LocalAccount:
                _domainTextBox.Text = Environment.MachineName;
                _domainTextBox.Enabled = true;
                break;
            case AccountType.DomainOrWorkAccount:
                _domainTextBox.Text = Environment.UserDomainName;
                _domainTextBox.Enabled = true;
                break;
        }
    }

    private void CopyOutputToClipboard()
    {
        try
        {
            var text = _setupOutputBox.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            Clipboard.SetText(text);
            _updateStatusStrip.Text = "Output copied to clipboard";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Copy to clipboard failed: {ex.Message}");
        }
    }

    private void LoadSavedCredentialInputs()
    {
        _suppressCredentialPersistence = true;
        try
        {
            _usernameTextBox.Text = string.IsNullOrWhiteSpace(_settings.SavedUsername)
                ? Environment.UserName
                : _settings.SavedUsername;

            var savedType = ParseSavedAccountType(_settings.SavedAccountType);
            SelectAccountType(savedType ?? AccountType.MicrosoftAccount);

            ApplyAccountTypeDefaults();

            if (!string.IsNullOrWhiteSpace(_settings.SavedDomain))
            {
                _domainTextBox.Text = _settings.SavedDomain;
            }

            var savedPassword = _settings.GetSavedPassword();
            if (!string.IsNullOrEmpty(savedPassword))
            {
                _passwordTextBox.Text = savedPassword;
            }
        }
        finally
        {
            _suppressCredentialPersistence = false;
        }

        PersistCredentialInputs();
    }

    private void PersistCredentialInputs()
    {
        if (_suppressCredentialPersistence)
        {
            return;
        }

        try
        {
            _settings.SavedUsername = _usernameTextBox.Text.Trim();
            _settings.SavedDomain = _domainTextBox.Text.Trim();
            if (_accountTypeComboBox.SelectedItem is AccountTypeOption option)
            {
                _settings.SavedAccountType = option.Type.ToString();
            }

            _settings.SetSavedPassword(_passwordTextBox.Text);
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
        {
            return null;
        }

        return Enum.TryParse<AccountType>(raw, ignoreCase: true, out var parsed)
            ? parsed
            : null;
    }

    private void SelectAccountType(AccountType type)
    {
        for (var i = 0; i < _accountTypeComboBox.Items.Count; i++)
        {
            if (_accountTypeComboBox.Items[i] is AccountTypeOption option && option.Type == type)
            {
                _accountTypeComboBox.SelectedIndex = i;
                return;
            }
        }
    }

    private void TryApplyAppIcon()
    {
        try
        {
            using var extracted = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (extracted != null)
            {
                Icon = (Icon)extracted.Clone();
            }
        }
        catch
        {
            // Best effort only.
        }
    }

    private void SetControlsEnabled(bool enabled)
    {
        _runSetupButton.Enabled = enabled;
        _testCredentialsButton.Enabled = enabled;
        _updatePasswordButton.Enabled = enabled;
        _uninstallButton.Enabled = enabled;
        _runCheckButton.Enabled = enabled;
        _startServiceButton.Enabled = enabled;
        _stopServiceButton.Enabled = enabled;
        _restartServiceButton.Enabled = enabled;
        _checkUpdatesButton.Enabled = enabled;
        _updateNowButton.Enabled = enabled && (_lastUpdateResult?.IsUpdateAvailable ?? false);
    }

    private void RestoreConsoleOutput()
    {
        FlushPendingCredentialPersistence();
        _updateTimer.Stop();
        _logTimer.Stop();
        _credentialPersistTimer.Stop();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        Console.SetOut(_originalConsoleOut);
        Console.SetError(_originalConsoleError);
        _updateChecker.Dispose();
    }

    private static int ToTimerIntervalMilliseconds(int hours)
    {
        var boundedHours = hours <= 0 ? 24 : hours;
        var ms = TimeSpan.FromHours(boundedHours).TotalMilliseconds;
        return ms > int.MaxValue ? int.MaxValue : (int)ms;
    }

    private static string FormatVersion(Version? version)
    {
        if (version == null)
        {
            return "unknown";
        }

        var build = version.Build < 0 ? 0 : version.Build;
        return $"{version.Major}.{version.Minor}.{build}";
    }

    private static string FormatBytes(long? bytes)
    {
        if (bytes == null)
        {
            return "unknown";
        }

        var value = bytes.Value;
        string[] suffixes = ["B", "KB", "MB", "GB"];
        var order = 0;
        double size = value;
        while (size >= 1024 && order < suffixes.Length - 1)
        {
            order++;
            size /= 1024;
        }

        return $"{size:0.##} {suffixes[order]}";
    }

    private static bool IsRunningAsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private sealed class AccountTypeOption(string name, AccountType type)
    {
        public string Name { get; } = name;

        public AccountType Type { get; } = type;

        public override string ToString() => Name;
    }

    private sealed class TeeTextWriter(params TextWriter[] writers) : TextWriter
    {
        private readonly TextWriter[] _writers = writers;

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            foreach (var writer in _writers)
            {
                writer.Write(value);
            }
        }

        public override void Write(string? value)
        {
            foreach (var writer in _writers)
            {
                writer.Write(value);
            }
        }

        public override void WriteLine(string? value)
        {
            foreach (var writer in _writers)
            {
                writer.WriteLine(value);
            }
        }

        public override void Flush()
        {
            foreach (var writer in _writers)
            {
                writer.Flush();
            }
        }
    }
}
