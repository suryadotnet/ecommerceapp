namespace OrderService.Infrastructure.ExternalServices;
/// <summary>
/// Request payload sent to InventoryService to reserve stock for an order line item.
/// </summary>
public sealed record ReserveInventoryRequest(Guid OrderId, Guid ProductId, int Quantity);
/// <summary>
/// Response returned by InventoryService after attempting a stock reservation.
/// </summary>
public sealed record ReserveInventoryResponse(Guid OrderId, string Status);
/// <summary>
/// Request payload sent to InventoryService to release previously reserved stock
/// (used for Saga compensation when a later step, e.g. payment, fails).
/// </summary>
public sealed record ReleaseInventoryRequest(Guid OrderId, Guid ProductId, int Quantity);
/// <summary>
/// Typed HTTP client abstraction for talking to InventoryService.
/// Kept intentionally thin - all cross-cutting concerns (retry, timeout,
/// circuit breaker, rate limiting) are configured on the underlying
/// HttpClient's resilience pipeline in Program.cs, NOT here.
/// </summary>
public interface IInventoryServiceClient
{
    Task<ReserveInventoryResponse> ReserveAsync(ReserveInventoryRequest request, CancellationToken cancellationToken);
    Task ReleaseAsync(ReleaseInventoryRequest request, CancellationToken cancellationToken);
}
public sealed class InventoryServiceClient : IInventoryServiceClient
{
    private readonly HttpClient _httpClient;
    public InventoryServiceClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }
    public async Task<ReserveInventoryResponse> ReserveAsync(ReserveInventoryRequest request, CancellationToken cancellationToken)
    {
        // The resilience pipeline attached to this HttpClient (see Program.cs) automatically
        // wraps this call with: Rate Limiter -> Retry -> Circuit Breaker -> Timeout.
        using var response = await _httpClient.PostAsJsonAsync("api/inventory/reserve", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ReserveInventoryResponse>(cancellationToken: cancellationToken);
        return result ?? new ReserveInventoryResponse(request.OrderId, "Unknown");
    }
    public async Task ReleaseAsync(ReleaseInventoryRequest request, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync("api/inventory/release", request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
