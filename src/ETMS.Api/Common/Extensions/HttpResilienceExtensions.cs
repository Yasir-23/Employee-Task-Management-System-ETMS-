using ETMS.Api.Services.Billing;
using ETMS.Api.Services.Webhooks;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace ETMS.Api.Common.Extensions;

/// <summary>
/// FR-RESIL-01 / SDD §12.1: Polly v8 pipelines for every outbound HttpClient. Polly is the only retry layer:
/// Stripe.net's own network retries are disabled where its client is built, so attempts are not multiplied.
/// </summary>
public static class HttpResilienceExtensions
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    public const int MaxRetryAttempts = 2; // 3 attempts in total
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan TotalRequestTimeout = TimeSpan.FromSeconds(30);
    public const double CircuitFailureRatio = 0.5;
    public const int CircuitMinimumThroughput = 5;
    public static readonly TimeSpan CircuitSamplingDuration = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan CircuitBreakDuration = TimeSpan.FromSeconds(15);

    public static IServiceCollection AddHttpResilience(this IServiceCollection services)
    {
        // Outbound webhooks: redirects are never followed and the connect callback re-checks resolved addresses
        // (SDD §10.2). One circuit per receiver host, so a failing receiver does not block the others.
        // Deliveries carry X-ETMS-Delivery, which receivers deduplicate on, so the POST is safe to retry.
        services.AddHttpClient(WebhookDeliveryService.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                ConnectCallback = SsrfGuard.ConnectAsync
            })
            .AddStandardResilienceHandler(options => ConfigureStandardPipeline(options, WebhookSigner.DeliveryHeader))
            .SelectPipelineByAuthority();

        // Stripe: checkout creation always carries an Idempotency-Key, so its POST is retried safely.
        services.AddHttpClient(StripePaymentGatewayService.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .AddStandardResilienceHandler(options => ConfigureStandardPipeline(options, IdempotencyKeyHeader));

        return services;
    }

    /// <summary>Retry with exponential backoff and jitter, per-attempt and total timeouts, and a circuit breaker.</summary>
    /// <param name="options">The standard pipeline options to configure.</param>
    /// <param name="idempotencyHeader">A request header that makes a non-idempotent method safe to retry.</param>
    public static void ConfigureStandardPipeline(HttpStandardResilienceOptions options, string idempotencyHeader)
    {
        options.Retry.MaxRetryAttempts = MaxRetryAttempts;
        options.Retry.BackoffType = DelayBackoffType.Exponential;
        options.Retry.UseJitter = true;
        // Transient = network error, timeout, 408, 429 (Retry-After is honored) or 5xx.
        options.Retry.ShouldHandle = args => ValueTask.FromResult(
            HttpClientResiliencePredicates.IsTransient(args.Outcome)
            && IsSafeToRetry(args.Context.GetRequestMessage(), idempotencyHeader));

        options.AttemptTimeout.Timeout = AttemptTimeout;
        options.TotalRequestTimeout.Timeout = TotalRequestTimeout;

        options.CircuitBreaker.FailureRatio = CircuitFailureRatio;
        options.CircuitBreaker.MinimumThroughput = CircuitMinimumThroughput;
        options.CircuitBreaker.SamplingDuration = CircuitSamplingDuration;
        options.CircuitBreaker.BreakDuration = CircuitBreakDuration;
    }

    /// <summary>FR-RESIL-01: non-idempotent requests are retried only when they carry an idempotency key.</summary>
    public static bool IsSafeToRetry(HttpRequestMessage? request, string idempotencyHeader)
    {
        if (request is null)
        {
            return false;
        }

        var method = request.Method;
        return method == HttpMethod.Get || method == HttpMethod.Head || method == HttpMethod.Options
            || method == HttpMethod.Put || method == HttpMethod.Delete
            || request.Headers.Contains(idempotencyHeader);
    }
}
