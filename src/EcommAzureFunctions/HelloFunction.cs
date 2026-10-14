using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;

public class HelloFunction
{
    private readonly ILogger<HelloFunction> _logger;

    public HelloFunction(ILogger<HelloFunction> logger)
    {
        _logger = logger;
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

        await response.WriteStringAsync(
            "Hello from Azure Functions!");

        return response;
    }
}