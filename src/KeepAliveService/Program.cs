using System.Security.Principal;
using KeepAliveService.Services;
using KeepAliveService.Setup;

// Parse command-line mode
if (args.Length > 0)
{
    var command = args[0].ToLowerInvariant();

    switch (command)
    {
        case "--setup":
            SetupManager.RunSetup();
            return;

        case "--check":
            var exitCode = ComplianceChecker.RunCheck();
            Environment.Exit(exitCode);
            return;

        case "--update-password":
            RequireAdmin("--update-password");
            var passwordUpdateResult = AutoLogonConfigurator.UpdatePassword();
            Environment.Exit(passwordUpdateResult ? 0 : 1);
            return;

        case "--uninstall":
            RequireAdmin("--uninstall");
            ServiceInstaller.Uninstall();
            return;

        case "--help":
        case "-h":
        case "/?":
            PrintHelp();
            return;

        default:
            Console.WriteLine($"Unknown command: {command}");
            Console.WriteLine();
            PrintHelp();
            Environment.Exit(1);
            return;
    }
}

// No arguments: run as Windows Service
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

static void RequireAdmin(string command)
{
    using var identity = WindowsIdentity.GetCurrent();
    var principal = new WindowsPrincipal(identity);
    if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Write("  [FAIL] ");
        Console.ResetColor();
        Console.WriteLine($"'{command}' requires Administrator privileges.");
        Console.WriteLine("  Right-click the executable and select 'Run as administrator'.");
        Environment.Exit(1);
    }
}

static void PrintHelp()
{
    Console.WriteLine("Windows Keep Alive Service");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  KeepAliveService.exe --setup             First-time setup (run as Admin)");
    Console.WriteLine("  KeepAliveService.exe --check             Verify all settings are correct");
    Console.WriteLine("  KeepAliveService.exe --update-password   Update auto-login password");
    Console.WriteLine("  KeepAliveService.exe --uninstall         Remove the service");
    Console.WriteLine("  KeepAliveService.exe --help              Show this help");
    Console.WriteLine();
    Console.WriteLine("When run without arguments, operates as a Windows Service.");
}
