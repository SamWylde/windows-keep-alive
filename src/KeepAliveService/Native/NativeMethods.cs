using System.Runtime.InteropServices;

namespace KeepAliveService.Native;

internal static partial class NativeMethods
{
    public const int ATTACH_PARENT_PROCESS = -1;

    [Flags]
    public enum EXECUTION_STATE : uint
    {
        ES_CONTINUOUS = 0x80000000,
        ES_SYSTEM_REQUIRED = 0x00000001,
        ES_DISPLAY_REQUIRED = 0x00000002,
        ES_AWAYMODE_REQUIRED = 0x00000040,
    }

    [LibraryImport("kernel32.dll")]
    public static partial uint SetThreadExecutionState(EXECUTION_STATE esFlags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachConsole(int dwProcessId);
}
