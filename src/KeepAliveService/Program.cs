using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using KeepAliveService.Native;
using KeepAliveService.Services;
using KeepAliveService.Setup;
using KeepAliveService.UI;
using KeepAliveService.Update;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;

internal static class Program
{
    private const string GuiMutexName = @"Local\WindowsKeepAlive.Gui.Singleton";
    private const string GuiActivationEventName = @"Local\WindowsKeepAlive.Gui.Activate";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            // --tray-startup is a GUI mode flag, not a CLI command
            if (args[0].Equals("--tray-startup", StringComparison.OrdinalIgnoreCase))
            {
                return RunInteractiveGuiMode(args, startMinimized: true);
            }

            return RunCommandLineMode(args);
        }

        if (Environment.UserInteractive)
        {
            return RunInteractiveGuiMode(args);
        }

        return RunServiceMode(args);
    }

    private static int RunCommandLineMode(string[] args)
    {
        AttachToParentConsole();

        var command = args[0].ToLowerInvariant();
        switch (command)
        {
            case "--setup":
                return SetupManager.RunSetup();

            case "--check":
                return ComplianceChecker.RunCheck();

            case "--update-password":
                if (!RequireAdmin("--update-password"))
                {
                    return 1;
                }

                return AutoLogonConfigurator.UpdatePassword() ? 0 : 1;

            case "--uninstall":
                if (!RequireAdmin("--uninstall"))
                {
                    return 1;
                }

                ServiceInstaller.Uninstall();
                return 0;

            case "--restore":
                if (!RequireAdmin("--restore"))
                {
                    return 1;
                }

                return RestoreManager.RunRestore();

            case "--help":
            case "-h":
            case "/?":
                PrintHelp();
                return 0;

            default:
                Console.WriteLine($"Unknown command: {command}");
                Console.WriteLine();
                PrintHelp();
                return 1;
        }
    }

    private static int RunInteractiveGuiMode(string[] args, bool startMinimized = false)
    {
        Mutex? singleInstanceMutex = null;
        EventWaitHandle? activationEvent = null;
        CancellationTokenSource? activationListenerCts = null;
        Task? activationListenerTask = null;

        try
        {
            if (!IsRunningAsAdmin())
            {
                return RelaunchAsAdministrator(args) ? 0 : 1;
            }

            InstallManager.EnsureProgramDataLayout();
            if (InstallManager.EnsureInstalledAndRelaunchIfNeeded(args))
            {
                return 0;
            }

            if (!TryAcquireGuiMutex(out singleInstanceMutex))
            {
                SignalExistingGuiInstance();
                return 0;
            }

            var app = new System.Windows.Application();
            app.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Light });
            app.Resources.MergedDictionaries.Add(new ControlsDictionary());

            var window = new MainWindow(startMinimized);
            StartActivationListener(window, out activationEvent, out activationListenerCts, out activationListenerTask);
            app.Run(window);
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                var crashLog = Path.Combine(Path.GetTempPath(), "keepalive-crash.log");
                File.WriteAllText(crashLog, $"{DateTime.Now:O}{Environment.NewLine}{ex}");
            }
            catch { /* Best effort */ }

            System.Windows.MessageBox.Show(
                $"Application startup failed: {ex}",
                "Windows Keep Alive",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            return 1;
        }
        finally
        {
            if (activationListenerCts != null)
            {
                activationListenerCts.Cancel();
            }

            if (activationListenerTask != null)
            {
                try
                {
                    activationListenerTask.Wait(1000);
                }
                catch
                {
                    // Best effort only.
                }
            }

            activationEvent?.Dispose();
            activationListenerCts?.Dispose();
            singleInstanceMutex?.ReleaseMutex();
            singleInstanceMutex?.Dispose();
        }
    }

    private static bool TryAcquireGuiMutex(out Mutex? mutex)
    {
        mutex = null;

        try
        {
            mutex = new Mutex(initiallyOwned: true, GuiMutexName, out var createdNew);
            if (createdNew)
            {
                return true;
            }

            mutex.Dispose();
            mutex = null;
            return false;
        }
        catch
        {
            // If single-instance lock fails unexpectedly, continue instead of blocking startup.
            mutex = null;
            return true;
        }
    }

    private static void SignalExistingGuiInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(GuiActivationEventName, out var existing))
            {
                using (existing)
                {
                    existing.Set();
                }
            }
        }
        catch
        {
            // Best effort only.
        }
    }

    private static void StartActivationListener(
        MainWindow window,
        out EventWaitHandle? activationEvent,
        out CancellationTokenSource? listenerCts,
        out Task? listenerTask)
    {
        activationEvent = null;
        listenerCts = null;
        listenerTask = null;

        try
        {
            var localActivationEvent = new EventWaitHandle(
                initialState: false,
                mode: EventResetMode.AutoReset,
                name: GuiActivationEventName);
            activationEvent = localActivationEvent;
            listenerCts = new CancellationTokenSource();

            var token = listenerCts.Token;
            listenerTask = Task.Run(() =>
            {
                var waitHandles = new WaitHandle[] { localActivationEvent, token.WaitHandle };
                while (true)
                {
                    var signaled = WaitHandle.WaitAny(waitHandles);
                    if (signaled == 1)
                    {
                        return;
                    }

                    window.ActivateFromExternalLaunch();
                }
            }, token);
        }
        catch
        {
            activationEvent?.Dispose();
            listenerCts?.Dispose();
            activationEvent = null;
            listenerCts = null;
            listenerTask = null;
        }
    }

    private static int RunServiceMode(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Services.AddWindowsService(options =>
        {
            options.ServiceName = "KeepAliveService";
        });

        builder.Services.AddHostedService<PowerKeepAliveWorker>();
        builder.Services.AddHostedService<ProcessWatchdogWorker>();
        builder.Services.AddHostedService<ComplianceWatchdogWorker>();

        builder.Logging.AddEventLog(settings =>
        {
            settings.SourceName = "KeepAliveService";
            settings.LogName = "Application";
        });

        var host = builder.Build();
        host.Run();
        return 0;
    }

    private static void AttachToParentConsole()
    {
        try
        {
            if (!NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS))
            {
                return;
            }

            var standardOut = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            var standardError = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
            Console.SetOut(standardOut);
            Console.SetError(standardError);
        }
        catch
        {
            // Best effort only.
        }
    }

    private static bool RelaunchAsAdministrator(string[] args)
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return false;
            }

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Verb = "runas",
                UseShellExecute = true,
            };

            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            Process.Start(psi);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // The user cancelled UAC.
            return false;
        }
    }

    private static bool RequireAdmin(string command)
    {
        if (IsRunningAsAdmin())
        {
            return true;
        }

        Console.ForegroundColor = ConsoleColor.Red;
        Console.Write("  [FAIL] ");
        Console.ResetColor();
        Console.WriteLine($"'{command}' requires Administrator privileges.");
        Console.WriteLine("  Right-click the executable and select 'Run as administrator'.");
        return false;
    }

    private static bool IsRunningAsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Windows Keep Alive");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  KeepAliveService.exe --setup             First-time setup (run as Admin)");
        Console.WriteLine("  KeepAliveService.exe --check             Verify all settings are correct");
        Console.WriteLine("  KeepAliveService.exe --update-password   Update auto-login password");
        Console.WriteLine("  KeepAliveService.exe --restore           Restore original settings and uninstall");
        Console.WriteLine("  KeepAliveService.exe --uninstall         Remove the service only");
        Console.WriteLine("  KeepAliveService.exe --tray-startup      Launch GUI minimized to system tray");
        Console.WriteLine("  KeepAliveService.exe --help              Show this help");
        Console.WriteLine();
        Console.WriteLine("When run without arguments:");
        Console.WriteLine("  - In an interactive session: launches the GUI.");
        Console.WriteLine("  - Under Service Control Manager: runs as the background service.");
    }
}
