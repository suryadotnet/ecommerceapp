using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

// if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING")))
// {
//     builder.Services.AddOpenTelemetry()
//         .UseFunctionsWorkerDefaults()
//         .UseAzureMonitorExporter();
// }

builder.Build().Run();
