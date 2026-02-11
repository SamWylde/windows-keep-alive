using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using KeepAliveService.Native;
using KeepAliveService.Services;
using KeepAliveService.Setup;
using KeepAliveService.UI;
using KeepAliveService.Update;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0)
        {
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

    private static int RunInteractiveGuiMode(string[] args)
    {
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

            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Application startup failed: {ex.Message}",
                "Windows Keep Alive",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
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
        Console.WriteLine("  KeepAliveService.exe --uninstall         Remove the service");
        Console.WriteLine("  KeepAliveService.exe --help              Show this help");
        Console.WriteLine();
        Console.WriteLine("When run without arguments:");
        Console.WriteLine("  - In an interactive session: launches the GUI.");
        Console.WriteLine("  - Under Service Control Manager: runs as the background service.");
    }
}
