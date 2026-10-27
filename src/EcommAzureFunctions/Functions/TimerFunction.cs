using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace EcommTimerFunctions;

public sealed class TimerFunction
{
    private readonly ILogger<TimerFunction> _logger;

    public TimerFunction(ILogger<TimerFunction> logger)
    {
        _logger = logger;
    }

    [Function(nameof(TimerFunction))]
    public void Run([TimerTrigger("*/10 * * * * *")] TimerInfo timerInfo)
    {
        _logger.LogInformation("Ecomm timer function executed at {ExecutionTimeUtc}.", DateTime.UtcNow);

        if (timerInfo.ScheduleStatus is not null)
        {
            _logger.LogInformation("Next timer execution is scheduled for {NextExecutionUtc}.",
                timerInfo.ScheduleStatus.Next);
        }
    }
}
