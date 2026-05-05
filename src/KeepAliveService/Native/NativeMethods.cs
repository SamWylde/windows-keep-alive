using System.Runtime.InteropServices;

namespace KeepAliveService.Native;

internal static partial class NativeMethods
{
    public const int ATTACH_PARENT_PROCESS = -1;
    public const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

    [Flags]
    public enum EXECUTION_STATE : uint
    {
        ES_CONTINUOUS = 0x80000000,
        ES_SYSTEM_REQUIRED = 0x00000001,
        ES_DISPLAY_REQUIRED = 0x00000002,
        ES_AWAYMODE_REQUIRED = 0x00000040,
    }

    public const uint POWER_REQUEST_CONTEXT_VERSION = 0;
    public const uint POWER_REQUEST_CONTEXT_SIMPLE_STRING = 0x1;

    public enum POWER_REQUEST_TYPE
    {
        PowerRequestDisplayRequired = 0,
        PowerRequestSystemRequired = 1,
        PowerRequestAwayModeRequired = 2,
        PowerRequestExecutionRequired = 3,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct REASON_CONTEXT
    {
        public uint Version;
        public uint Flags;
        public IntPtr SimpleReasonString;
    }

    [LibraryImport("kernel32.dll")]
    public static partial uint SetThreadExecutionState(EXECUTION_STATE esFlags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr PowerCreateRequest(in REASON_CONTEXT context);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PowerSetRequest(IntPtr powerRequest, POWER_REQUEST_TYPE requestType);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PowerClearRequest(IntPtr powerRequest, POWER_REQUEST_TYPE requestType);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, int dwFlags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachConsole(int dwProcessId);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW",
        StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterWindowMessage(string lpString);
}
