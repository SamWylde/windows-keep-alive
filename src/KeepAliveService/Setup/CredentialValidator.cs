using System.ComponentModel;
using System.Runtime.InteropServices;
using KeepAliveService.UI;

namespace KeepAliveService.Setup;

public static partial class CredentialValidator
{
    private const int LOGON32_LOGON_INTERACTIVE = 2;
    private const int LOGON32_PROVIDER_DEFAULT = 0;

    public static CredentialValidationResult Validate(CredentialInfo credentials)
    {
        if (string.IsNullOrWhiteSpace(credentials.Username))
        {
            return new CredentialValidationResult(false, "Username is required.", null);
        }

        if (string.IsNullOrWhiteSpace(credentials.Password))
        {
            return new CredentialValidationResult(false, "Password is required.", null);
        }

        var username = credentials.Username.Trim();
        var domain = credentials.ResolveDomain();

        var ok = LogonUser(
            username,
            domain,
            credentials.Password,
            LOGON32_LOGON_INTERACTIVE,
            LOGON32_PROVIDER_DEFAULT,
            out var tokenHandle);

        if (ok)
        {
            if (tokenHandle != IntPtr.Zero)
            {
                _ = CloseHandle(tokenHandle);
            }

            return new CredentialValidationResult(
                true,
                $"Credential check passed for '{username}' ({domain}).",
                null);
        }

        var errorCode = Marshal.GetLastWin32Error();
        var errorText = new Win32Exception(errorCode).Message;
        return new CredentialValidationResult(
            false,
            $"Credential check failed ({errorCode}): {errorText}",
            errorCode);
    }

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LogonUser(
        string lpszUsername,
        string lpszDomain,
        string lpszPassword,
        int dwLogonType,
        int dwLogonProvider,
        out IntPtr phToken);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);
}

public sealed record CredentialValidationResult(
    bool IsValid,
    string Message,
    int? ErrorCode);
