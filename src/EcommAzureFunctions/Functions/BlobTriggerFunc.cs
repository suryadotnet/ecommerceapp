using System.IO;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Ecomm.BlobTrigger.Function;

public class BlobTriggerFunc
{
    private readonly ILogger<BlobTriggerFunc> _logger;

    public BlobTriggerFunc(ILogger<BlobTriggerFunc> logger)
    {
        _logger = logger;
    }

    [Function(nameof(BlobTriggerFunc))]
    public async Task Run(
        [BlobTrigger("product-images/{name}", Connection = "BlobStorageConnection")]
        Stream stream,
        string name)
    {
        var buffer = new byte[81920];
        long bytesProcessed = 0;
        int bytesRead;

        while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            bytesProcessed += bytesRead;
        }

        _logger.LogInformation(
            "Processed product image blob {BlobName} ({BlobSizeBytes} bytes).",
            name,
            bytesProcessed);
    }
}