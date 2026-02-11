using KeepAliveService.Native;
using static KeepAliveService.Native.NativeMethods;

namespace KeepAliveService.Services;

public class PowerKeepAliveWorker : BackgroundService
{
    private readonly ILogger<PowerKeepAliveWorker> _logger;
    private static readonly TimeSpan ReassertInterval = TimeSpan.FromMinutes(5);

    public PowerKeepAliveWorker(ILogger<PowerKeepAliveWorker> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SetKeepAlive();
        _logger.LogInformation("Power keep-alive initialized (ES_CONTINUOUS | ES_SYSTEM_REQUIRED)");

        // Re-assert every 5 minutes as a safety net.
        // ES_CONTINUOUS should persist, but re-asserting guards against
        // edge cases where the state gets cleared (e.g. after unexpected resume).
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ReassertInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            SetKeepAlive();
        }

        ClearKeepAlive();
        _logger.LogInformation("Power keep-alive cleared on shutdown");
    }

    private void SetKeepAlive()
    {
        var result = SetThreadExecutionState(
            EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_SYSTEM_REQUIRED);

        if (result == 0)
        {
            _logger.LogWarning("SetThreadExecutionState failed - system may be able to sleep");
        }
    }

    private void ClearKeepAlive()
    {
        SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);
    }
}
