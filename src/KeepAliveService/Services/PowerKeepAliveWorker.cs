using KeepAliveService.Native;
using System.Runtime.InteropServices;
using static KeepAliveService.Native.NativeMethods;

namespace KeepAliveService.Services;

public class PowerKeepAliveWorker : BackgroundService
{
    private readonly ILogger<PowerKeepAliveWorker> _logger;
    private const string PowerRequestReason = "Windows Keep Alive is keeping this computer awake.";
    private static readonly TimeSpan LegacyReassertInterval = TimeSpan.FromMinutes(5);
    private IntPtr _powerRequestHandle = IntPtr.Zero;
    private IntPtr _reasonStringHandle = IntPtr.Zero;
    private bool _systemRequestSet;
    private bool _usingLegacyExecutionState;

    public PowerKeepAliveWorker(ILogger<PowerKeepAliveWorker> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (TryStartPowerRequest())
        {
            _logger.LogInformation("Power keep-alive initialized (PowerRequestSystemRequired)");
        }
        else
        {
            _usingLegacyExecutionState = true;
            SetLegacyKeepAlive();
            _logger.LogWarning("Power keep-alive fell back to SetThreadExecutionState");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(LegacyReassertInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (_usingLegacyExecutionState)
            {
                SetLegacyKeepAlive();
            }
        }

        StopPowerRequest();
        if (_usingLegacyExecutionState)
        {
            ClearLegacyKeepAlive();
        }

        _logger.LogInformation("Power keep-alive cleared on shutdown");
    }

    private bool TryStartPowerRequest()
    {
        try
        {
            _reasonStringHandle = Marshal.StringToHGlobalUni(PowerRequestReason);
            var context = new REASON_CONTEXT
            {
                Version = POWER_REQUEST_CONTEXT_VERSION,
                Flags = POWER_REQUEST_CONTEXT_SIMPLE_STRING,
                SimpleReasonString = _reasonStringHandle,
            };

            _powerRequestHandle = PowerCreateRequest(in context);
            if (_powerRequestHandle == IntPtr.Zero || _powerRequestHandle == new IntPtr(-1))
            {
                _logger.LogWarning("PowerCreateRequest failed with Win32 error {ErrorCode}", Marshal.GetLastWin32Error());
                StopPowerRequest();
                return false;
            }

            if (!PowerSetRequest(_powerRequestHandle, POWER_REQUEST_TYPE.PowerRequestSystemRequired))
            {
                _logger.LogWarning("PowerSetRequest(SystemRequired) failed with Win32 error {ErrorCode}", Marshal.GetLastWin32Error());
                StopPowerRequest();
                return false;
            }

            _systemRequestSet = true;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not initialize PowerRequest keep-alive");
            StopPowerRequest();
            return false;
        }
    }

    private void StopPowerRequest()
    {
        if (_powerRequestHandle != IntPtr.Zero && _powerRequestHandle != new IntPtr(-1))
        {
            if (_systemRequestSet &&
                !PowerClearRequest(_powerRequestHandle, POWER_REQUEST_TYPE.PowerRequestSystemRequired))
            {
                _logger.LogWarning("PowerClearRequest(SystemRequired) failed with Win32 error {ErrorCode}", Marshal.GetLastWin32Error());
            }

            _systemRequestSet = false;
            if (!CloseHandle(_powerRequestHandle))
            {
                _logger.LogWarning("CloseHandle(power request) failed with Win32 error {ErrorCode}", Marshal.GetLastWin32Error());
            }

            _powerRequestHandle = IntPtr.Zero;
        }

        if (_reasonStringHandle != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_reasonStringHandle);
            _reasonStringHandle = IntPtr.Zero;
        }
    }

    private void SetLegacyKeepAlive()
    {
        var result = SetThreadExecutionState(
            EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_SYSTEM_REQUIRED);

        if (result == 0)
        {
            _logger.LogWarning("SetThreadExecutionState failed - system may be able to sleep");
        }
    }

    private void ClearLegacyKeepAlive()
    {
        SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);
    }
}
