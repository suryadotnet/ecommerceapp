using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using EcommAzureFunctions.Services;

namespace EcommAzureFunctions.Functions;

public sealed class ServiceBusQueueTriggerFunction
{
    private readonly ILogger<ServiceBusQueueTriggerFunction> _logger;
    private readonly GreetingService _greetingService;

    public ServiceBusQueueTriggerFunction(
        ILogger<ServiceBusQueueTriggerFunction> logger,
        GreetingService greetingService)
    {
        _logger = logger;
        _greetingService = greetingService;
    }

    [Function(nameof(ServiceBusQueueTriggerFunction))]
    public void Run(
        [ServiceBusTrigger("%ServiceBusQueueName%", Connection = "ServiceBusConnection")]
        ServiceBusReceivedMessage message)
    {
        var body = message.Body.ToString();
        var greeting = _greetingService.CreateGreeting(body);

        _logger.LogInformation(
            "Processed Service Bus message {MessageId} from subject {Subject}. Greeting: {Greeting}",
            message.MessageId,
            message.Subject,
            greeting);
    }
}
