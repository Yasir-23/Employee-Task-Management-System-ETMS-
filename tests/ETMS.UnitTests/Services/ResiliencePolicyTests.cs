using System.Net;
using ETMS.Api.Common.Exceptions;
using ETMS.Api.Common.Extensions;
using ETMS.Api.Common.Settings;
using ETMS.Api.Models.Entities;
using ETMS.Api.Services.Billing;
using ETMS.Api.Services.Webhooks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;

namespace ETMS.UnitTests.Services;

/// <summary>FR-RESIL-01 / SDD §12.1: the Polly v8 pipelines on the outbound HttpClients.</summary>
public sealed class ResiliencePolicyTests : IDisposable
{
    private readonly StubHandler _handler = new();
    private readonly ServiceProvider _provider;

    public ResiliencePolicyTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpResilience();

        // Real pipelines over a stubbed network, with backoff delays removed to keep the tests fast.
        services.AddHttpClient(WebhookDeliveryService.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _handler);
        services.AddHttpClient(StripePaymentGatewayService.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _handler);
        services.PostConfigureAll<HttpStandardResilienceOptions>(o => o.Retry.Delay = TimeSpan.Zero);

        _provider = services.BuildServiceProvider();
    }

    public void Dispose() => _provider.Dispose();

    private HttpClient CreateClient(string name) => _provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);

    // ---------------------------------------------------------------------
    // Pipeline configuration
    // ---------------------------------------------------------------------

    [Fact]
    public void ConfigureStandardPipeline_AppliesRetryTimeoutAndCircuitBreakerPolicy()
    {
        var options = new HttpStandardResilienceOptions();

        HttpResilienceExtensions.ConfigureStandardPipeline(options, HttpResilienceExtensions.IdempotencyKeyHeader);

        Assert.Equal(2, options.Retry.MaxRetryAttempts); // 3 attempts in total
        Assert.Equal(DelayBackoffType.Exponential, options.Retry.BackoffType);
        Assert.True(options.Retry.UseJitter);
        Assert.Equal(TimeSpan.FromSeconds(10), options.AttemptTimeout.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(30), options.TotalRequestTimeout.Timeout);
        Assert.Equal(0.5, options.CircuitBreaker.FailureRatio);
        Assert.Equal(5, options.CircuitBreaker.MinimumThroughput);
        Assert.Equal(TimeSpan.FromSeconds(30), options.CircuitBreaker.SamplingDuration);
        Assert.Equal(TimeSpan.FromSeconds(15), options.CircuitBreaker.BreakDuration);
    }

    [Theory]
    [InlineData("GET", false, true)]
    [InlineData("PUT", false, true)]
    [InlineData("DELETE", false, true)]
    [InlineData("POST", false, false)]
    [InlineData("POST", true, true)]
    [InlineData("PATCH", false, false)]
    public void IsSafeToRetry_RequiresIdempotentMethodOrKey(string method, bool withKey, bool expected)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), "https://api.example.com/");
        if (withKey)
        {
            request.Headers.Add(HttpResilienceExtensions.IdempotencyKeyHeader, Guid.NewGuid().ToString());
        }

        Assert.Equal(expected, HttpResilienceExtensions.IsSafeToRetry(request, HttpResilienceExtensions.IdempotencyKeyHeader));
    }

    // ---------------------------------------------------------------------
    // Retry behaviour through the registered clients
    // ---------------------------------------------------------------------

    [Fact]
    public async Task TransientFailure_IsRetriedUntilSuccess()
    {
        _handler.Enqueue(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);

        using var response = await CreateClient(StripePaymentGatewayService.HttpClientName).GetAsync("https://api.stripe.test/v1/x");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, _handler.Calls);
    }

    [Fact]
    public async Task PersistentTransientFailure_StopsAfterThreeAttempts()
    {
        _handler.Default = HttpStatusCode.InternalServerError;

        using var response = await CreateClient(StripePaymentGatewayService.HttpClientName).GetAsync("https://api.stripe.test/v1/x");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(3, _handler.Calls);
    }

    [Fact]
    public async Task NonTransientFailure_IsNotRetried()
    {
        _handler.Default = HttpStatusCode.BadRequest;

        using var response = await CreateClient(StripePaymentGatewayService.HttpClientName).GetAsync("https://api.stripe.test/v1/x");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1, _handler.Calls);
    }

    [Fact]
    public async Task PostWithoutIdempotencyKey_IsNotRetried()
    {
        _handler.Default = HttpStatusCode.ServiceUnavailable;

        using var response = await CreateClient(StripePaymentGatewayService.HttpClientName)
            .PostAsync("https://api.stripe.test/v1/charges", new StringContent("a=1"));

        Assert.Equal(1, _handler.Calls);
    }

    [Fact]
    public async Task PostWithIdempotencyKey_IsRetried()
    {
        _handler.Default = HttpStatusCode.ServiceUnavailable;
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.stripe.test/v1/checkout/sessions")
        {
            Content = new StringContent("a=1")
        };
        request.Headers.Add(HttpResilienceExtensions.IdempotencyKeyHeader, "checkout:abc");

        using var response = await CreateClient(StripePaymentGatewayService.HttpClientName).SendAsync(request);

        Assert.Equal(3, _handler.Calls);
    }

    [Fact]
    public async Task WebhookDeliveryPost_IsRetriedBecauseItCarriesADeliveryId()
    {
        _handler.Enqueue(HttpStatusCode.BadGateway, HttpStatusCode.NoContent);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://hooks.partner.example.com/etms")
        {
            Content = new StringContent("{}")
        };
        request.Headers.Add(WebhookSigner.DeliveryHeader, Guid.NewGuid().ToString());

        using var response = await CreateClient(WebhookDeliveryService.HttpClientName).SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(2, _handler.Calls);
    }

    // ---------------------------------------------------------------------
    // Circuit breaker
    // ---------------------------------------------------------------------

    [Fact]
    public async Task RepeatedFailures_OpenTheCircuitAndFailFast()
    {
        _handler.Default = HttpStatusCode.ServiceUnavailable;
        var client = CreateClient(StripePaymentGatewayService.HttpClientName);

        // Request 1: 3 failed attempts. Request 2: the 5th failure (minimum throughput, 100% ratio) opens the
        // circuit, so its final retry is already rejected.
        (await client.GetAsync("https://api.stripe.test/v1/x")).Dispose();
        await Assert.ThrowsAsync<BrokenCircuitException>(() => client.GetAsync("https://api.stripe.test/v1/x"));
        Assert.Equal(HttpResilienceExtensions.CircuitMinimumThroughput, _handler.Calls);

        await Assert.ThrowsAsync<BrokenCircuitException>(() => client.GetAsync("https://api.stripe.test/v1/x"));
        Assert.Equal(HttpResilienceExtensions.CircuitMinimumThroughput, _handler.Calls); // failed fast, no network call
    }

    [Fact]
    public async Task WebhookCircuit_IsTrackedPerReceiverHost()
    {
        var client = CreateClient(WebhookDeliveryService.HttpClientName);
        _handler.Default = HttpStatusCode.ServiceUnavailable;
        (await client.GetAsync("https://failing.example.com/hook")).Dispose();
        await Assert.ThrowsAsync<BrokenCircuitException>(() => client.GetAsync("https://failing.example.com/hook"));
        await Assert.ThrowsAsync<BrokenCircuitException>(() => client.GetAsync("https://failing.example.com/hook"));

        _handler.Default = HttpStatusCode.OK;
        using var healthy = await client.GetAsync("https://healthy.example.com/hook");

        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
    }

    // ---------------------------------------------------------------------
    // Consumers of an open circuit (SDD §12.1 failure table)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task StripeCheckout_WhenCircuitOpens_Returns503WithRetryAfter()
    {
        _handler.Default = HttpStatusCode.ServiceUnavailable;
        var stripeClient = new Stripe.StripeClient("sk_test_unit",
            httpClient: new Stripe.SystemNetHttpClient(CreateClient(StripePaymentGatewayService.HttpClientName), maxNetworkRetries: 0));
        var gateway = new StripePaymentGatewayService(stripeClient, Options.Create(new StripeSettings { WebhookSecret = "whsec_unit" }),
            NullLogger<StripePaymentGatewayService>.Instance);

        // Stripe still answering (with 503s) is an API error: 502. Once the circuit opens, calls fail fast with 503.
        await Assert.ThrowsAsync<BadGatewayException>(() => gateway.GetCheckoutSessionAsync("cs_test_1"));
        await Assert.ThrowsAsync<ServiceUnavailableException>(() => gateway.GetCheckoutSessionAsync("cs_test_1"));
        var callsWhenOpened = _handler.Calls;
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => gateway.GetCheckoutSessionAsync("cs_test_1"));
        Assert.Equal(callsWhenOpened, _handler.Calls);

        Assert.Equal(503, ex.StatusCode);
        Assert.Equal(HttpResilienceExtensions.CircuitBreakDuration, ex.RetryAfter);
    }

    [Fact]
    public async Task WebhookDelivery_WhenCircuitIsOpen_IsPostponedWithoutUsingAnAttempt()
    {
        using var ctx = new TaskServiceTestContext();
        var dataProtection = new EphemeralDataProtectionProvider();
        var subscription = new WebhookSubscription
        {
            Id = Guid.NewGuid(),
            Url = "https://hooks.partner.example.com/etms",
            SecretKey = dataProtection.CreateProtector(WebhookSubscriptionService.SecretProtectionPurpose).Protect("whsec_etms_test"),
            EventType = WebhookEventTypes.Wildcard,
            CreatedAtUtc = DateTime.UtcNow
        };
        ctx.Db.WebhookSubscriptions.Add(subscription);
        ctx.Db.WebhookDeliveryLogs.Add(new WebhookDeliveryLog
        {
            Id = Guid.NewGuid(),
            SubscriptionId = subscription.Id,
            EventType = WebhookEventTypes.TaskCompleted,
            Payload = "{}",
            AttemptCount = 2,
            TimestampUtc = DateTime.UtcNow,
            NextRetryUtc = DateTime.UtcNow.AddSeconds(-1)
        });
        ctx.Db.SaveChanges();

        _handler.Exception = new BrokenCircuitException("open");
        var factory = new Moq.Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(WebhookDeliveryService.HttpClientName)).Returns(() => new HttpClient(_handler, disposeHandler: false));
        var delivery = new WebhookDeliveryService(ctx.Db, factory.Object, dataProtection,
            Options.Create(new WebhookSettings()), NullLogger<WebhookDeliveryService>.Instance);

        var before = DateTime.UtcNow;
        await delivery.DeliverDueAsync();

        var stored = await ctx.Db.WebhookDeliveryLogs.AsNoTracking().SingleAsync();
        Assert.Equal(2, stored.AttemptCount);
        Assert.Null(stored.StatusCode);
        Assert.False(stored.IsSuccess);
        Assert.True(stored.NextRetryUtc >= before + HttpResilienceExtensions.CircuitBreakDuration);
    }

    /// <summary>Plays queued status codes, then <see cref="Default"/>; counts every call that reaches the "network".</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode> _queued = new();
        private int _calls;

        public HttpStatusCode Default { get; set; } = HttpStatusCode.OK;
        public Exception? Exception { get; set; }
        public int Calls => _calls;

        public void Enqueue(params HttpStatusCode[] statusCodes)
        {
            foreach (var statusCode in statusCodes)
            {
                _queued.Enqueue(statusCode);
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            if (Exception is not null)
            {
                throw Exception;
            }

            var statusCode = _queued.Count > 0 ? _queued.Dequeue() : Default;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent("""{"error":{"type":"api_error","message":"unavailable"}}""")
            });
        }

        // The factory may dispose handlers it built; the shared stub stays usable across clients.
        protected override void Dispose(bool disposing)
        {
        }
    }
}
