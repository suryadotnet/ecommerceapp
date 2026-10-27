using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using EcommAzureFunctions.Services;

public class HelloFunction
{
    private readonly ILogger<HelloFunction> _logger;
    private readonly GreetingService _greetingService;

    public HelloFunction(
        ILogger<HelloFunction> logger,
        GreetingService greetingService)
    {
        _logger = logger;
        _greetingService = greetingService;
    }

    [Function("HelloFunction")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(
            AuthorizationLevel.Anonymous,
            "get",
            "post")] HttpRequestData req)
    {
        _logger.LogInformation("HelloFunction executed.");

        var response = req.CreateResponse(HttpStatusCode.OK);

        await response.WriteStringAsync(_greetingService.CreateGreeting());

        return response;
    }
}