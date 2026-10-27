
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace EcommAzureFunctions.Functions;

public sealed class ServiceBusTopicTriggerFunction
{
    private readonly ILogger<ServiceBusTopicTriggerFunction> _logger;

    public ServiceBusTopicTriggerFunction(
        ILogger<ServiceBusTopicTriggerFunction> logger)
    {
        _logger = logger;
    }

    [Function(nameof(ServiceBusTopicTriggerFunction))]
    public void Run(
        [ServiceBusTrigger(
            "ordersvctopic",
            "ordersvctopicsubscription",
            Connection = "ServiceBusConnection")]
        ServiceBusReceivedMessage message)
    {
        var body = message.Body.ToString();

        _logger.LogInformation(
            "Received message from Service Bus topic. " +
            "MessageId: {MessageId}, Subject: {Subject}, " +
            "Topic: ordersvctopic, Subscription: ordersvctopicsubscription, " +
            "Body: {MessageBody}",
            message.MessageId,
            message.Subject,
            body);
    }
}
