namespace KeepAliveService.Setup;

internal static class ConsoleOutput
{
    public static void Success(string message)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("  [OK] ");
        Console.ResetColor();
        Console.WriteLine(message);
    }

    public static void Warning(string message)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write("  [WARN] ");
        Console.ResetColor();
        Console.WriteLine(message);
    }

    public static void Error(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Write("  [FAIL] ");
        Console.ResetColor();
        Console.WriteLine(message);
    }

    public static void Info(string message)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write("  [INFO] ");
        Console.ResetColor();
        Console.WriteLine(message);
    }
}
