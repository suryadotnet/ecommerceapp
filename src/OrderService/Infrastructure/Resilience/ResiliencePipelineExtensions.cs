using System.Net;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Retry;
using Polly.Timeout;

namespace OrderService.Infrastructure.Resilience;

/// <summary>
/// Centralizes the configuration of the .NET Resilience Pipeline (built on Polly v8)
/// applied to every outbound HttpClient call this service makes to downstream
/// microservices (InventoryService, PaymentService, etc.).
///
/// The pipeline executes strategies in the order they are added. With
/// AddResilienceHandler, the recommended / most common ordering (outside-in) is:
///
///   Rate Limiter  -> Retry -> Circuit Breaker -> Timeout (per attempt)
///
/// Meaning: a request first passes through the rate limiter (reject fast if we're
/// sending too many concurrent/queued requests), then the retry strategy wraps
/// the circuit breaker + timeout, so each retry attempt is time-boxed and also
/// observed by the circuit breaker for failure counting.
/// </summary>
public static class ResiliencePipelineExtensions
{
    /// <summary>
    /// Attaches a named, fully-custom resilience pipeline (Retry + Circuit Breaker +
    /// Timeout + Rate Limiter) to the given IHttpClientBuilder.
    /// Use this when you need fine-grained control per downstream dependency.
    /// </summary>
    public static IHttpClientBuilder AddCustomResiliencePipeline(this IHttpClientBuilder builder, string pipelineName)
    {
        // AddResilienceHandler returns IHttpResiliencePipelineBuilder (used to further
        // configure the pipeline), not IHttpClientBuilder. We discard it and return the
        // original builder so this extension method stays fluent-chainable.
        builder.AddResilienceHandler(pipelineName, pipelineBuilder =>
        {
            // ----------------------------------------------------------------------
            // 1) RATE LIMITER
            // Protects BOTH this service and the downstream dependency from being
            // overwhelmed. Caps the number of concurrent outbound requests through
            // this HttpClient and queues a small burst above that before rejecting
            // with a RateLimiterRejectedException (surfaced as 503-like failure).
            // ----------------------------------------------------------------------
            pipelineBuilder.AddRateLimiter(new HttpRateLimiterStrategyOptions
            {
                RateLimiter = args => new ValueTask<RateLimitLease>(
                    new ConcurrencyLimiter(new ConcurrencyLimiterOptions
                    {
                        // Max number of requests allowed to execute concurrently.
                        PermitLimit = 10,
                        // Extra requests above the permit limit get queued (FIFO) instead
                        // of being rejected immediately, smoothing short bursts.
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 20
                    }).AttemptAcquire())
            });

            // ----------------------------------------------------------------------
            // 2) RETRY
            // Automatically retries transient failures (5xx, 408, network errors,
            // HttpRequestException, TaskCanceledException from timeouts) using an
            // exponential backoff with jitter to avoid "retry storms" against a
            // struggling downstream service.
            // ----------------------------------------------------------------------
            pipelineBuilder.AddRetry(new HttpRetryStrategyOptions
            {
                // Only retry failures considered transient by the default predicate
                // (network errors, timeouts, 5xx and 408 responses).
                ShouldHandle = static args => args.Outcome switch
                {
                    { Result.StatusCode: HttpStatusCode.RequestTimeout } => PredicateResult.True(),
                    { Result.StatusCode: >= HttpStatusCode.InternalServerError } => PredicateResult.True(),
                    { Exception: HttpRequestException or TimeoutRejectedException } => PredicateResult.True(),
                    _ => PredicateResult.False()
                },
                MaxRetryAttempts = 3,
                // Exponential backoff: 1st retry ~1s, 2nd ~2s, 3rd ~4s (plus jitter)
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromSeconds(1),
                OnRetry = static args =>
                {
                    Console.WriteLine(
                        $"[Retry] Attempt {args.AttemptNumber + 1} after {args.RetryDelay.TotalMilliseconds}ms " +
                        $"due to: {args.Outcome.Exception?.Message ?? args.Outcome.Result?.StatusCode.ToString()}");
                    return default;
                }
            });

            // ----------------------------------------------------------------------
            // 3) CIRCUIT BREAKER
            // Monitors the failure ratio of calls flowing through this pipeline.
            // If too many fail within the sampling window, the circuit "opens" and
            // all further calls fail FAST (ShortCircuit) without hitting the network,
            // giving the downstream service time to recover. After the break
            // duration, it moves to "half-open" and allows a trial request through.
            // ----------------------------------------------------------------------
            pipelineBuilder.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                // Open the circuit if >= 50% of requests fail...
                FailureRatio = 0.5,
                // ...within a rolling 30 second sampling window...
                SamplingDuration = TimeSpan.FromSeconds(30),
                // ...and at least 8 requests went through the window (avoids
                // tripping the breaker on a tiny sample size).
                MinimumThroughput = 8,
                // Once open, stay open for 15 seconds before allowing a trial call.
                BreakDuration = TimeSpan.FromSeconds(15),
                ShouldHandle = static args => args.Outcome switch
                {
                    { Result.StatusCode: >= HttpStatusCode.InternalServerError } => PredicateResult.True(),
                    { Exception: HttpRequestException or TimeoutRejectedException } => PredicateResult.True(),
                    _ => PredicateResult.False()
                },
                OnOpened = static args =>
                {
                    Console.WriteLine($"[CircuitBreaker] Circuit OPENED for {args.BreakDuration.TotalSeconds}s.");
                    return default;
                },
                OnClosed = static _ =>
                {
                    Console.WriteLine("[CircuitBreaker] Circuit CLOSED - downstream recovered.");
                    return default;
                },
                OnHalfOpened = static _ =>
                {
                    Console.WriteLine("[CircuitBreaker] Circuit HALF-OPEN - testing downstream with a trial request.");
                    return default;
                }
            });

            // ----------------------------------------------------------------------
            // 4) TIMEOUT (per attempt)
            // Bounds how long a single HTTP attempt (including retries) is allowed
            // to run. If exceeded, a TimeoutRejectedException is thrown, which the
            // Retry strategy above will catch and retry (up to MaxRetryAttempts).
            // ----------------------------------------------------------------------
            pipelineBuilder.AddTimeout(new HttpTimeoutStrategyOptions
            {
                Timeout = TimeSpan.FromSeconds(5),
                OnTimeout = static args =>
                {
                    Console.WriteLine($"[Timeout] Request exceeded {args.Timeout.TotalSeconds}s and was aborted.");
                    return default;
                }
            });
        });

        return builder;
    }

    /// <summary>
    /// Alternative, zero-config option: Microsoft's built-in "standard" resilience
    /// handler, which bundles sensible defaults for Rate Limiting (total request
    /// timeout), Retry, Circuit Breaker, and Attempt Timeout in one call.
    /// Not used by default in this project (we prefer the explicit pipeline above
    /// for teaching purposes), but exposed here for convenience/quick use, e.g.:
    ///
    ///     builder.Services.AddHttpClient("X").AddStandardResilience();
    /// </summary>
    public static IHttpClientBuilder AddStandardResilience(this IHttpClientBuilder builder)
    {
        builder.AddStandardResilienceHandler();
        return builder;
    }
}

