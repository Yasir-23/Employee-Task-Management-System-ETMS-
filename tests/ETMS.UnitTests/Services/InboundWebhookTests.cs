using System.Text;
using System.Text.Json;
using ETMS.Api.Common.Exceptions;
using ETMS.Api.Common.Settings;
using ETMS.Api.Controllers;
using ETMS.Api.Models.Entities;
using ETMS.Api.Models.Enums;
using ETMS.Api.Services.Billing;
using ETMS.Api.Services.Webhooks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ETMS.UnitTests.Services;

/// <summary>FR-HOOK-01 / BR-08: inbound Stripe webhooks, from raw HTTP body to the billing state machine.</summary>
public sealed class InboundWebhookTests : IDisposable
{
    private const string WebhookSecret = "whsec_unit_test_secret";
    private const string SessionId = "cs_test_current";

    private readonly TaskServiceTestContext _ctx = new();
    private readonly StripePaymentGatewayService _gateway;
    private readonly BillingService _billing;

    public InboundWebhookTests()
    {
        var settings = Options.Create(new StripeSettings
        {
            ApiKey = "sk_test_unit",
            PublishableKey = "pk_test_unit",
            WebhookSecret = WebhookSecret,
            SuccessUrl = "https://app.example.com/success",
            CancelUrl = "https://app.example.com/cancel"
        });

        // The Stripe API client is never called while handling webhooks.
        _gateway = new StripePaymentGatewayService(new Mock<Stripe.IStripeClient>(MockBehavior.Strict).Object, settings,
            NullLogger<StripePaymentGatewayService>.Instance);
        _billing = new BillingService(_ctx.Db, _gateway, settings, NullLogger<BillingService>.Instance);
    }

    public void Dispose() => _ctx.Dispose();

    // ---------------------------------------------------------------------
    // BR-08: authentication happens before anything else
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Receive_MissingSignatureHeader_Returns400WithoutProcessing()
    {
        var billingService = new Mock<IBillingService>(MockBehavior.Strict);
        var controller = CreateController(SessionEvent("checkout.session.completed"), signatureHeader: null, billingService.Object);

        var ex = await Assert.ThrowsAsync<WebhookSignatureException>(() => controller.Receive(CancellationToken.None));

        Assert.Equal(StatusCodes.Status400BadRequest, ex.StatusCode);
        billingService.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("t=1700000000")]          // no v1 signature
    [InlineData("v1=abcdef")]             // no timestamp
    [InlineData("t=yesterday,v1=abcdef")] // non-numeric timestamp
    [InlineData("t=1700000000,v1=")]      // empty signature
    public async Task Receive_MalformedSignatureHeader_Returns400WithoutProcessing(string header)
    {
        var billingService = new Mock<IBillingService>(MockBehavior.Strict);
        var controller = CreateController(SessionEvent("checkout.session.completed"), header, billingService.Object);

        var ex = await Assert.ThrowsAsync<WebhookSignatureException>(() => controller.Receive(CancellationToken.None));

        Assert.Equal(StatusCodes.Status400BadRequest, ex.StatusCode);
        billingService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Receive_SignedWithWrongSecret_Returns401WithoutProcessing()
    {
        var body = SessionEvent("checkout.session.completed");
        var billingService = new Mock<IBillingService>(MockBehavior.Strict);
        var controller = CreateController(body, Sign(body, secret: "whsec_attacker"), billingService.Object);

        var ex = await Assert.ThrowsAsync<WebhookSignatureException>(() => controller.Receive(CancellationToken.None));

        Assert.Equal(StatusCodes.Status401Unauthorized, ex.StatusCode);
        billingService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Receive_TamperedBody_Returns401AndLeavesBillingUnchanged()
    {
        var billing = AddBilling(BillingStatus.Processing);
        var signedBody = SessionEvent("checkout.session.expired");
        var tamperedBody = SessionEvent("checkout.session.completed");
        var controller = CreateController(tamperedBody, Sign(signedBody));

        var ex = await Assert.ThrowsAsync<WebhookSignatureException>(() => controller.Receive(CancellationToken.None));

        Assert.Equal(StatusCodes.Status401Unauthorized, ex.StatusCode);
        Assert.Equal(BillingStatus.Processing, await StatusOf(billing.Id));
    }

    [Fact]
    public async Task Receive_TimestampOutsideTolerance_Returns401()
    {
        var body = SessionEvent("checkout.session.completed");
        var stale = DateTimeOffset.UtcNow.AddSeconds(-(StripePaymentGatewayService.WebhookToleranceSeconds + 60));
        var billingService = new Mock<IBillingService>(MockBehavior.Strict);
        var controller = CreateController(body, Sign(body, timestamp: stale), billingService.Object);

        var ex = await Assert.ThrowsAsync<WebhookSignatureException>(() => controller.Receive(CancellationToken.None));

        Assert.Equal(StatusCodes.Status401Unauthorized, ex.StatusCode);
        billingService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Receive_ValidSignatureButBodyNotJson_Returns400()
    {
        const string body = "this is not json";
        var controller = CreateController(body, Sign(body));

        var ex = await Assert.ThrowsAsync<BadRequestException>(() => controller.Receive(CancellationToken.None));

        Assert.Equal(StatusCodes.Status400BadRequest, ex.StatusCode);
    }

    [Fact]
    public async Task Receive_BodyOver256Kb_Returns413()
    {
        var billingService = new Mock<IBillingService>(MockBehavior.Strict);
        var body = new string('x', StripeWebhooksController.MaxBodyBytes + 1);
        var controller = CreateController(body, Sign(body), billingService.Object);

        var ex = await Assert.ThrowsAsync<PayloadTooLargeException>(() => controller.Receive(CancellationToken.None));

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, ex.StatusCode);
        billingService.VerifyNoOtherCalls();
    }

    // ---------------------------------------------------------------------
    // Verified events drive the billing state machine (FR-PAY-03)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Receive_CheckoutSessionCompleted_MarksBillingPaidAndReturns200()
    {
        var billing = AddBilling(BillingStatus.Processing);
        var body = SessionEvent("checkout.session.completed", paymentIntent: "pi_test_paid");

        var result = await CreateController(body, Sign(body)).Receive(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("""{"received":true}""", JsonSerializer.Serialize(ok.Value));

        var stored = await Reload(billing.Id);
        Assert.Equal(BillingStatus.Paid, stored.Status);
        Assert.Equal("pi_test_paid", stored.StripePaymentIntentId);
        Assert.NotNull(stored.PaidAtUtc);
        Assert.True(stored.PaidAtUtc > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task Receive_PaymentIntentSucceeded_CorrelatesByMetadataAndMarksPaid()
    {
        var billing = AddBilling(BillingStatus.Processing);
        var body = PaymentIntentEvent("payment_intent.succeeded", billing.Id, "succeeded");

        await CreateController(body, Sign(body)).Receive(CancellationToken.None);

        var stored = await Reload(billing.Id);
        Assert.Equal(BillingStatus.Paid, stored.Status);
        Assert.Equal("pi_test_intent", stored.StripePaymentIntentId);
    }

    [Fact]
    public async Task Receive_PaymentIntentFailed_MarksBillingFailed()
    {
        var billing = AddBilling(BillingStatus.Processing);
        var body = PaymentIntentEvent("payment_intent.payment_failed", billing.Id, "requires_payment_method");

        await CreateController(body, Sign(body)).Receive(CancellationToken.None);

        var stored = await Reload(billing.Id);
        Assert.Equal(BillingStatus.Failed, stored.Status);
        Assert.Null(stored.PaidAtUtc);
    }

    [Fact]
    public async Task Receive_SessionExpired_ReturnsBillingToUnpaid()
    {
        var billing = AddBilling(BillingStatus.Processing);
        var body = SessionEvent("checkout.session.expired", paymentStatus: "unpaid", paymentIntent: null);

        await CreateController(body, Sign(body)).Receive(CancellationToken.None);

        Assert.Equal(BillingStatus.Unpaid, await StatusOf(billing.Id));
    }

    [Fact]
    public async Task Receive_CompletedWithDelayedPayment_StaysProcessing()
    {
        var billing = AddBilling(BillingStatus.Processing);
        var body = SessionEvent("checkout.session.completed", paymentStatus: "unpaid");

        await CreateController(body, Sign(body)).Receive(CancellationToken.None);

        Assert.Equal(BillingStatus.Processing, await StatusOf(billing.Id));
    }

    [Fact]
    public async Task Receive_SuccessAfterEarlierCardFailureOnSameSession_MarksPaid()
    {
        var billing = AddBilling(BillingStatus.Failed);
        var body = SessionEvent("checkout.session.completed");

        await CreateController(body, Sign(body)).Receive(CancellationToken.None);

        Assert.Equal(BillingStatus.Paid, await StatusOf(billing.Id));
    }

    [Fact]
    public async Task Receive_ReplayedCompletedEvent_IsAcknowledgedWithoutSideEffects()
    {
        var billing = AddBilling(BillingStatus.Processing);
        var body = SessionEvent("checkout.session.completed");
        await CreateController(body, Sign(body)).Receive(CancellationToken.None);
        var paidAt = (await Reload(billing.Id)).PaidAtUtc;

        var result = await CreateController(body, Sign(body)).Receive(CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var stored = await Reload(billing.Id);
        Assert.Equal(BillingStatus.Paid, stored.Status);
        Assert.Equal(paidAt, stored.PaidAtUtc);
    }

    [Fact]
    public async Task Receive_FailureAfterPaid_DoesNotLeavePaid()
    {
        var billing = AddBilling(BillingStatus.Paid);
        var body = PaymentIntentEvent("payment_intent.payment_failed", billing.Id, "requires_payment_method");

        await CreateController(body, Sign(body)).Receive(CancellationToken.None);

        Assert.Equal(BillingStatus.Paid, await StatusOf(billing.Id));
    }

    [Fact]
    public async Task Receive_EventForStaleSession_IsIgnored()
    {
        var billing = AddBilling(BillingStatus.Processing);
        var body = SessionEvent("checkout.session.completed", sessionId: "cs_test_replaced_earlier");

        var result = await CreateController(body, Sign(body)).Receive(CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(BillingStatus.Processing, await StatusOf(billing.Id));
    }

    [Fact]
    public async Task Receive_UnhandledEventType_Returns200WithoutChanges()
    {
        var billing = AddBilling(BillingStatus.Processing);
        var body = SessionEvent("customer.created");

        var result = await CreateController(body, Sign(body)).Receive(CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(BillingStatus.Processing, await StatusOf(billing.Id));
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private StripeWebhooksController CreateController(string body, string? signatureHeader, IBillingService? billingService = null)
    {
        var httpContext = new DefaultHttpContext();
        var bytes = Encoding.UTF8.GetBytes(body);
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.ContentType = "application/json";
        httpContext.Request.ContentLength = bytes.Length;
        httpContext.Request.Body = new MemoryStream(bytes);
        if (signatureHeader is not null)
        {
            httpContext.Request.Headers[StripeWebhooksController.SignatureHeader] = signatureHeader;
        }

        return new StripeWebhooksController(_gateway, billingService ?? _billing)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
    }

    /// <summary>Stripe's v1 scheme is the same HMAC construction ETMS uses for outbound deliveries.</summary>
    private static string Sign(string body, string secret = WebhookSecret, DateTimeOffset? timestamp = null)
    {
        var t = (timestamp ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        return $"t={t},v1={WebhookSigner.ComputeSignature(secret, t, body)}";
    }

    private TaskBilling AddBilling(BillingStatus status)
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);
        var billing = new TaskBilling
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            AmountCents = 15000,
            Currency = "usd",
            Status = status,
            StripeSessionId = SessionId,
            PaidAtUtc = status == BillingStatus.Paid ? DateTime.UtcNow.AddDays(-1) : null,
            CreatedAtUtc = DateTime.UtcNow
        };
        _ctx.Db.TaskBillings.Add(billing);
        _ctx.Db.SaveChanges();
        _ctx.Db.ChangeTracker.Clear();
        return billing;
    }

    private async Task<TaskBilling> Reload(Guid billingId) =>
        await _ctx.Db.TaskBillings.AsNoTracking().SingleAsync(b => b.Id == billingId);

    private async Task<BillingStatus> StatusOf(Guid billingId) => (await Reload(billingId)).Status;

    private static string SessionEvent(string type, string sessionId = SessionId, string paymentStatus = "paid", string? paymentIntent = "pi_test_123") =>
        JsonSerializer.Serialize(new
        {
            id = $"evt_{Guid.NewGuid():N}",
            @object = "event",
            api_version = "2024-06-20",
            created = 1_700_000_000,
            livemode = false,
            pending_webhooks = 1,
            request = new { id = (string?)null, idempotency_key = (string?)null },
            type,
            data = new
            {
                @object = new
                {
                    id = sessionId,
                    @object = "checkout.session",
                    payment_status = paymentStatus,
                    payment_intent = paymentIntent,
                    status = "complete"
                }
            }
        });

    private static string PaymentIntentEvent(string type, Guid billingId, string status) =>
        JsonSerializer.Serialize(new
        {
            id = $"evt_{Guid.NewGuid():N}",
            @object = "event",
            api_version = "2024-06-20",
            created = 1_700_000_000,
            livemode = false,
            pending_webhooks = 1,
            request = new { id = (string?)null, idempotency_key = (string?)null },
            type,
            data = new
            {
                @object = new
                {
                    id = "pi_test_intent",
                    @object = "payment_intent",
                    status,
                    metadata = new Dictionary<string, string> { ["TaskId"] = Guid.NewGuid().ToString(), ["BillingId"] = billingId.ToString() }
                }
            }
        });
}
