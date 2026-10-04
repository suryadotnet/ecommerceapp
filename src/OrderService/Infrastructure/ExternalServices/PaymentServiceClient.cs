namespace OrderService.Infrastructure.ExternalServices;

// Request payload sent to PaymentService to process a payment for an order.
public sealed record ProcessPaymentRequest(Guid OrderId, Guid CustomerId, decimal Amount);

// Response returned by PaymentService after attempting to process a payment.
public sealed record ProcessPaymentResponse(Guid OrderId, Guid PaymentId, string Status);

// Typed HTTP client abstraction for talking to PaymentService.
// All retry/timeout/circuit-breaker/rate-limit behavior lives in the
// resilience pipeline registered against this HttpClient in Program.cs.
public interface IPaymentServiceClient
{
    Task<ProcessPaymentResponse> ProcessPaymentAsync(ProcessPaymentRequest request, CancellationToken cancellationToken);
}

public sealed class PaymentServiceClient : IPaymentServiceClient
{
    private readonly HttpClient _httpClient;

    public PaymentServiceClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<ProcessPaymentResponse> ProcessPaymentAsync(ProcessPaymentRequest request, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync("api/payments/process", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ProcessPaymentResponse>(cancellationToken: cancellationToken);
        return result ?? new ProcessPaymentResponse(request.OrderId, Guid.Empty, "Unknown");
    }
}

