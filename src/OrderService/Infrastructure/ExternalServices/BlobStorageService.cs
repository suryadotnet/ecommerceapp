using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;

namespace OrderService.Infrastructure.ExternalServices;

public interface IBlobStorageService
{
    /// <summary>
    /// Uploads a file stream to the given blob container (created if missing) and
    /// returns the public blob URL to store alongside the order/order item.
    /// </summary>
    Task<string> UploadAsync(
        string containerName,
        string fileName,
        Stream content,
        string contentType,
        CancellationToken cancellationToken);
}

/// <summary>
/// Wraps the singleton BlobServiceClient (registered in Program.cs - connection
/// string in Development, Managed Identity in other environments) to upload
/// product images and return their public URL for persistence in SQL.
/// </summary>
public sealed class BlobStorageService : IBlobStorageService
{
    private readonly BlobServiceClient _blobServiceClient;
    private readonly ILogger<BlobStorageService> _logger;

    public BlobStorageService(BlobServiceClient blobServiceClient, ILogger<BlobStorageService> logger)
    {
        _blobServiceClient = blobServiceClient;
        _logger = logger;
    }

    public async Task<string> UploadAsync(
        string containerName,
        string fileName,
        Stream content,
        string contentType,
        CancellationToken cancellationToken)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
        await containerClient.CreateIfNotExistsAsync(PublicAccessType.Blob, cancellationToken: cancellationToken);

        // Prefix with a GUID to avoid filename collisions across orders.
        var blobName = $"{Guid.NewGuid():N}-{fileName}";
        var blobClient = containerClient.GetBlobClient(blobName);

        await blobClient.UploadAsync(
            content,
            new BlobHttpHeaders { ContentType = contentType },
            cancellationToken: cancellationToken);

        _logger.LogInformation(
            "Uploaded blob {BlobName} to container {ContainerName}", blobName, containerName);

        return blobClient.Uri.ToString();
    }
}

