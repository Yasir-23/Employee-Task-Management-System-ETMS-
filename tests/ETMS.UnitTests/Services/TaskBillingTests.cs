using System.Security.Claims;
using ETMS.Api.Common.Exceptions;
using ETMS.Api.Common.Settings;
using ETMS.Api.Controllers;
using ETMS.Api.DTOs.Requests;
using ETMS.Api.DTOs.Responses;
using ETMS.Api.Models.Entities;
using ETMS.Api.Models.Enums;
using ETMS.Api.Services.Billing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ETMS.UnitTests.Services;

public sealed class TaskBillingTests : IDisposable
{
    private const string SuccessUrl = "https://app.example.com/billing/success?session_id={CHECKOUT_SESSION_ID}";
    private const string CancelUrl = "https://app.example.com/billing/cancelled";

    private readonly TaskServiceTestContext _ctx = new();
    private readonly Mock<IPaymentGatewayService> _gateway = new(MockBehavior.Strict);
    private readonly BillingService _service;

    public TaskBillingTests()
    {
        _service = new BillingService(
            _ctx.Db,
            _gateway.Object,
            Options.Create(new StripeSettings
            {
                ApiKey = "sk_test_unit",
                PublishableKey = "pk_test_unit",
                WebhookSecret = "whsec_unit",
                SuccessUrl = SuccessUrl,
                CancelUrl = CancelUrl
            }),
            NullLogger<BillingService>.Instance);
    }

    public void Dispose() => _ctx.Dispose();

    // ---------------------------------------------------------------------
    // PUT billing: validation (FR-PAY-01)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task SetBilling_ByCreatingManager_CreatesUnpaidBillingWithDefaultCurrency()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id, creatorId: _ctx.Manager.Id);

        var result = await _service.SetTaskBillingAsync(_ctx.Manager.Id, nameof(Role.Manager), task.Id,
            new SetBillingRequest { AmountCents = 15000 });

        Assert.True(result.Created);
        Assert.Equal(15000, result.Billing.AmountCents);
        Assert.Equal("usd", result.Billing.Currency);
        Assert.Equal(BillingStatus.Unpaid, result.Billing.Status);
        var stored = Assert.Single(_ctx.Db.TaskBillings);
        Assert.Equal(task.Id, stored.TaskId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-15000)]
    [InlineData(49)] // below Stripe's 50-cent minimum
    [InlineData(100_000_000)]
    public async Task SetBilling_AmountOutOfRange_Returns400(long amountCents)
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);

        var ex = await Assert.ThrowsAsync<BadRequestException>(() => _service.SetTaskBillingAsync(
            _ctx.Manager.Id, nameof(Role.Manager), task.Id, new SetBillingRequest { AmountCents = amountCents }));

        Assert.Equal(StatusCodes.Status400BadRequest, ex.StatusCode);
        Assert.Empty(_ctx.Db.TaskBillings);
    }

    [Fact]
    public async Task SetBilling_MissingAmount_Returns400()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);

        await Assert.ThrowsAsync<BadRequestException>(() => _service.SetTaskBillingAsync(
            _ctx.Manager.Id, nameof(Role.Manager), task.Id, new SetBillingRequest()));
    }

    [Theory]
    [InlineData("us")]
    [InlineData("usdd")]
    [InlineData("u$d")]
    [InlineData("")]
    public async Task SetBilling_InvalidCurrency_Returns400(string currency)
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);

        await Assert.ThrowsAsync<BadRequestException>(() => _service.SetTaskBillingAsync(
            _ctx.Manager.Id, nameof(Role.Manager), task.Id, new SetBillingRequest { AmountCents = 15000, Currency = currency }));
    }

    [Fact]
    public async Task SetBilling_UppercaseCurrency_IsNormalized()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);

        var result = await _service.SetTaskBillingAsync(_ctx.Manager.Id, nameof(Role.Manager), task.Id,
            new SetBillingRequest { AmountCents = 15000, Currency = " EUR " });

        Assert.Equal("eur", result.Billing.Currency);
    }

    [Theory]
    [InlineData(BillingStatus.Unpaid)]
    [InlineData(BillingStatus.Failed)]
    public async Task SetBilling_ExistingUnpaidOrFailed_UpdatesAmountAndKeepsStatus(BillingStatus status)
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);
        AddBilling(task, status);

        var result = await _service.SetTaskBillingAsync(_ctx.Manager.Id, nameof(Role.Manager), task.Id,
            new SetBillingRequest { AmountCents = 20000 });

        Assert.False(result.Created);
        Assert.Equal(20000, result.Billing.AmountCents);
        Assert.Equal(status, result.Billing.Status);
        Assert.NotNull(result.Billing.UpdatedAtUtc);
    }

    [Theory]
    [InlineData(BillingStatus.Processing)]
    [InlineData(BillingStatus.Paid)]
    public async Task SetBilling_WhenProcessingOrPaid_Returns409(BillingStatus status)
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);
        AddBilling(task, status);

        var ex = await Assert.ThrowsAsync<ConflictException>(() => _service.SetTaskBillingAsync(
            _ctx.Manager.Id, nameof(Role.Manager), task.Id, new SetBillingRequest { AmountCents = 20000 }));

        Assert.Equal(StatusCodes.Status409Conflict, ex.StatusCode);
    }

    // ---------------------------------------------------------------------
    // PUT billing: access control
    // ---------------------------------------------------------------------

    [Fact]
    public async Task SetBilling_ByEmployee_Returns403EvenForOwnTask()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);

        var ex = await Assert.ThrowsAsync<ForbiddenException>(() => _service.SetTaskBillingAsync(
            _ctx.Employee.Id, nameof(Role.Employee), task.Id, new SetBillingRequest { AmountCents = 15000 }));

        Assert.Equal(StatusCodes.Status403Forbidden, ex.StatusCode);
        Assert.Empty(_ctx.Db.TaskBillings);
    }

    [Fact]
    public async Task SetBilling_ByUnrelatedManager_Returns403()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id, creatorId: _ctx.Manager.Id);

        await Assert.ThrowsAsync<ForbiddenException>(() => _service.SetTaskBillingAsync(
            _ctx.OtherManager.Id, nameof(Role.Manager), task.Id, new SetBillingRequest { AmountCents = 15000 }));
    }

    [Fact]
    public async Task SetBilling_ByManagerOfAssignee_Succeeds()
    {
        // Created by another manager but assigned to one of Manager's direct reports.
        var task = _ctx.AddTask(_ctx.Employee.Id, creatorId: _ctx.OtherManager.Id);

        var result = await _service.SetTaskBillingAsync(_ctx.Manager.Id, nameof(Role.Manager), task.Id,
            new SetBillingRequest { AmountCents = 15000 });

        Assert.True(result.Created);
    }

    [Fact]
    public async Task SetBilling_ByAdmin_OnAnyTask_Succeeds()
    {
        var task = _ctx.AddTask(_ctx.ForeignEmployee.Id, creatorId: _ctx.OtherManager.Id);

        var result = await _service.SetTaskBillingAsync(Guid.NewGuid(), nameof(Role.Admin), task.Id,
            new SetBillingRequest { AmountCents = 15000 });

        Assert.True(result.Created);
    }

    [Fact]
    public async Task SetBilling_UnknownTask_Returns404()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => _service.SetTaskBillingAsync(
            _ctx.Manager.Id, nameof(Role.Manager), Guid.NewGuid(), new SetBillingRequest { AmountCents = 15000 }));
    }

    // ---------------------------------------------------------------------
    // Checkout: BR-07
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Checkout_WhenAlreadyPaid_Returns409AndNeverCallsStripe()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);
        AddBilling(task, BillingStatus.Paid);

        var ex = await Assert.ThrowsAsync<ConflictException>(() => _service.CreateCheckoutSessionAsync(
            _ctx.Manager.Id, nameof(Role.Manager), task.Id, null, NewKey()));

        Assert.Equal(StatusCodes.Status409Conflict, ex.StatusCode);
        Assert.Contains("BR-07", ex.Message);
        _gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Checkout_TaskNotBillable_Returns409()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);

        await Assert.ThrowsAsync<ConflictException>(() => _service.CreateCheckoutSessionAsync(
            _ctx.Manager.Id, nameof(Role.Manager), task.Id, null, NewKey()));

        _gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(BillingStatus.Unpaid)]
    [InlineData(BillingStatus.Failed)]
    public async Task Checkout_UnpaidOrFailed_CreatesSessionAndMovesToProcessing(BillingStatus status)
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);
        var billing = AddBilling(task, status);
        SetupCreate("cs_test_new", "https://checkout.stripe.com/c/pay/cs_test_new");

        var result = await _service.CreateCheckoutSessionAsync(_ctx.Manager.Id, nameof(Role.Manager), task.Id, null, NewKey());

        Assert.True(result.IsNewSession);
        Assert.Equal(new CheckoutSessionResponseDto("https://checkout.stripe.com/c/pay/cs_test_new", "cs_test_new"), result.Session);
        var stored = ReloadBilling(billing.Id);
        Assert.Equal(BillingStatus.Processing, stored.Status);
        Assert.Equal("cs_test_new", stored.StripeSessionId);
        _gateway.Verify(g => g.CreateCheckoutSessionAsync(
            It.Is<TaskBilling>(b => b.Id == billing.Id), It.Is<TaskItem>(t => t.Id == task.Id),
            SuccessUrl, CancelUrl, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Checkout_ByEmployee_Returns403()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);
        AddBilling(task, BillingStatus.Unpaid);

        await Assert.ThrowsAsync<ForbiddenException>(() => _service.CreateCheckoutSessionAsync(
            _ctx.Employee.Id, nameof(Role.Employee), task.Id, null, NewKey()));

        _gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Checkout_CustomReturnUrls_AreForwardedToGateway()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);
        AddBilling(task, BillingStatus.Unpaid);
        SetupCreate("cs_test_custom", "https://checkout.stripe.com/c/pay/cs_test_custom");

        await _service.CreateCheckoutSessionAsync(_ctx.Manager.Id, nameof(Role.Manager), task.Id,
            new CreateCheckoutRequest { SuccessUrl = "https://portal.example.com/paid", CancelUrl = "https://portal.example.com/back" }, NewKey());

        _gateway.Verify(g => g.CreateCheckoutSessionAsync(It.IsAny<TaskBilling>(), It.IsAny<TaskItem>(),
            "https://portal.example.com/paid", "https://portal.example.com/back", It.IsAny<string>(), It.IsAny<CancellationToken>()));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("/relative/path")]
    [InlineData("ftp://example.com/file")]
    public async Task Checkout_InvalidReturnUrl_Returns400(string url)
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);
        AddBilling(task, BillingStatus.Unpaid);

        await Assert.ThrowsAsync<BadRequestException>(() => _service.CreateCheckoutSessionAsync(
            _ctx.Manager.Id, nameof(Role.Manager), task.Id, new CreateCheckoutRequest { SuccessUrl = url }, NewKey()));

        _gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Checkout_GatewayFailure_LeavesBillingUnchanged()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);
        var billing = AddBilling(task, BillingStatus.Unpaid);
        _gateway.Setup(g => g.CreateCheckoutSessionAsync(It.IsAny<TaskBilling>(), It.IsAny<TaskItem>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BadGatewayException("Stripe is down."));

        var ex = await Assert.ThrowsAsync<BadGatewayException>(() => _service.CreateCheckoutSessionAsync(
            _ctx.Manager.Id, nameof(Role.Manager), task.Id, null, NewKey()));

        Assert.Equal(StatusCodes.Status502BadGateway, ex.StatusCode);
        var stored = ReloadBilling(billing.Id);
        Assert.Equal(BillingStatus.Unpaid, stored.Status);
        Assert.Null(stored.StripeSessionId);
    }

    // ---------------------------------------------------------------------
    // Checkout: idempotency (FR-PAY-04)
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-uuid")]
    public async Task Checkout_MissingOrInvalidIdempotencyKey_Returns400(string? key)
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);
        AddBilling(task, BillingStatus.Unpaid);

        var ex = await Assert.ThrowsAsync<BadRequestException>(() => _service.CreateCheckoutSessionAsync(
            _ctx.Manager.Id, nameof(Role.Manager), task.Id, null, key));

        Assert.Contains("Idempotency-Key", ex.Message);
        _gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Checkout_ForwardsBillingScopedIdempotencyKeyToStripe()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);
        var billing = AddBilling(task, BillingStatus.Unpaid);
        var clientKey = Guid.NewGuid();
        SetupCreate("cs_test_key", "https://checkout.stripe.com/c/pay/cs_test_key");

        // Braces and upper case are accepted and normalized, so equivalent client keys map to one Stripe key.
        await _service.CreateCheckoutSessionAsync(_ctx.Manager.Id, nameof(Role.Manager), task.Id, null,
            clientKey.ToString("B").ToUpperInvariant());

        _gateway.Verify(g => g.CreateCheckoutSessionAsync(It.IsAny<TaskBilling>(), It.IsAny<TaskItem>(), It.IsAny<string>(),
            It.IsAny<string>(), $"checkout:{billing.Id}:{clientKey:D}", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Checkout_RetryWhileSessionOpen_ReturnsSameSessionWithoutCreatingAnother()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);
        AddBilling(task, BillingStatus.Unpaid);
        var key = NewKey();
        SetupCreate("cs_test_once", "https://checkout.stripe.com/c/pay/cs_test_once");
        _gateway.Setup(g => g.GetCheckoutSessionAsync("cs_test_once", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CheckoutSessionResult("cs_test_once", "https://checkout.stripe.com/c/pay/cs_test_once", CheckoutSessionState.Open, DateTime.UtcNow.AddHours(23)));

        var first = await _service.CreateCheckoutSessionAsync(_ctx.Manager.Id, nameof(Role.Manager), task.Id, null, key);
        var retry = await _service.CreateCheckoutSessionAsync(_ctx.Manager.Id, nameof(Role.Manager), task.Id, null, key);

        Assert.True(first.IsNewSession);
        Assert.False(retry.IsNewSession);
        Assert.Equal(first.Session, retry.Session);
        _gateway.Verify(g => g.CreateCheckoutSessionAsync(It.IsAny<TaskBilling>(), It.IsAny<TaskItem>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Checkout_ProcessingWithExpiredSession_StartsNewSession()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);
        var billing = AddBilling(task, BillingStatus.Processing, sessionId: "cs_test_expired");
        _gateway.Setup(g => g.GetCheckoutSessionAsync("cs_test_expired", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CheckoutSessionResult("cs_test_expired", null, CheckoutSessionState.Expired, DateTime.UtcNow.AddHours(-1)));
        SetupCreate("cs_test_fresh", "https://checkout.stripe.com/c/pay/cs_test_fresh");

        var result = await _service.CreateCheckoutSessionAsync(_ctx.Manager.Id, nameof(Role.Manager), task.Id, null, NewKey());

        Assert.True(result.IsNewSession);
        Assert.Equal("cs_test_fresh", result.Session.SessionId);
        Assert.Equal("cs_test_fresh", ReloadBilling(billing.Id).StripeSessionId);
    }

    [Fact]
    public async Task Checkout_ProcessingWithCompletedSession_Returns409()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);
        AddBilling(task, BillingStatus.Processing, sessionId: "cs_test_done");
        _gateway.Setup(g => g.GetCheckoutSessionAsync("cs_test_done", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CheckoutSessionResult("cs_test_done", null, CheckoutSessionState.Complete, null));

        await Assert.ThrowsAsync<ConflictException>(() => _service.CreateCheckoutSessionAsync(
            _ctx.Manager.Id, nameof(Role.Manager), task.Id, null, NewKey()));
    }

    [Fact]
    public async Task Checkout_StripeReplaysStaleSessionForReusedKey_Returns409WithoutStoringIt()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);
        var billing = AddBilling(task, BillingStatus.Unpaid);
        _gateway.Setup(g => g.CreateCheckoutSessionAsync(It.IsAny<TaskBilling>(), It.IsAny<TaskItem>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CheckoutSessionResult("cs_test_old", null, CheckoutSessionState.Expired, null));

        await Assert.ThrowsAsync<ConflictException>(() => _service.CreateCheckoutSessionAsync(
            _ctx.Manager.Id, nameof(Role.Manager), task.Id, null, NewKey()));

        Assert.Null(ReloadBilling(billing.Id).StripeSessionId);
    }

    // ---------------------------------------------------------------------
    // GET billing
    // ---------------------------------------------------------------------

    [Fact]
    public async Task GetBilling_ByAssignee_ReturnsStatusAmountAndPaymentTime()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);
        var paidAt = DateTime.UtcNow.AddMinutes(-5);
        AddBilling(task, BillingStatus.Paid, paidAtUtc: paidAt);

        var result = await _service.GetBillingStatusAsync(_ctx.Employee.Id, nameof(Role.Employee), task.Id);

        Assert.Equal(BillingStatus.Paid, result.Status);
        Assert.Equal(15000, result.AmountCents);
        Assert.Equal(paidAt, result.PaidAtUtc);
    }

    [Fact]
    public async Task GetBilling_OutsideTaskScope_Returns404()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);
        AddBilling(task, BillingStatus.Unpaid);

        await Assert.ThrowsAsync<NotFoundException>(() => _service.GetBillingStatusAsync(
            _ctx.OtherManager.Id, nameof(Role.Manager), task.Id));
    }

    [Fact]
    public async Task GetBilling_TaskNotBillable_Returns404()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);

        await Assert.ThrowsAsync<NotFoundException>(() => _service.GetBillingStatusAsync(
            _ctx.Manager.Id, nameof(Role.Manager), task.Id));
    }

    // ---------------------------------------------------------------------
    // Controller status codes and header binding
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(true, StatusCodes.Status201Created)]
    [InlineData(false, StatusCodes.Status200OK)]
    public async Task Controller_Checkout_Returns201ForNewSessionAnd200ForReuse(bool isNew, int expectedStatus)
    {
        var taskId = Guid.NewGuid();
        var managerId = Guid.NewGuid();
        var session = new CheckoutSessionResponseDto("https://checkout.stripe.com/c/pay/cs_x", "cs_x");
        var billingService = new Mock<IBillingService>();
        billingService.Setup(s => s.CreateCheckoutSessionAsync(managerId, nameof(Role.Manager), taskId, null, "key-123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CheckoutResult(session, isNew));
        var controller = CreateController(billingService.Object, managerId, Role.Manager);

        var response = await controller.CreateCheckout(taskId, "key-123", null, CancellationToken.None);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(response.Result);
        Assert.Equal(expectedStatus, objectResult.StatusCode);
        Assert.Same(session, objectResult.Value);
    }

    [Fact]
    public async Task Controller_SetBilling_Returns201WhenCreated()
    {
        var taskId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var dto = new TaskBillingResponseDto { TaskId = taskId, AmountCents = 15000, Currency = "usd" };
        var billingService = new Mock<IBillingService>();
        billingService.Setup(s => s.SetTaskBillingAsync(adminId, nameof(Role.Admin), taskId, It.IsAny<SetBillingRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BillingUpsertResult(dto, Created: true));
        var controller = CreateController(billingService.Object, adminId, Role.Admin);

        var response = await controller.SetBilling(taskId, new SetBillingRequest { AmountCents = 15000 }, CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(response.Result);
        Assert.Equal(nameof(TaskBillingController.GetBilling), created.ActionName);
        Assert.Same(dto, created.Value);
    }

    private TaskBilling AddBilling(TaskItem task, BillingStatus status, string? sessionId = null, DateTime? paidAtUtc = null)
    {
        var billing = new TaskBilling
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            AmountCents = 15000,
            Currency = "usd",
            Status = status,
            StripeSessionId = sessionId,
            PaidAtUtc = paidAtUtc,
            CreatedAtUtc = DateTime.UtcNow
        };
        _ctx.Db.TaskBillings.Add(billing);
        _ctx.Db.SaveChanges();
        _ctx.Db.ChangeTracker.Clear(); // the service must load state from the store, as it would per request
        return billing;
    }

    private TaskBilling ReloadBilling(Guid billingId) =>
        _ctx.Db.TaskBillings.AsNoTracking().Single(b => b.Id == billingId);

    private void SetupCreate(string sessionId, string url) =>
        _gateway.Setup(g => g.CreateCheckoutSessionAsync(It.IsAny<TaskBilling>(), It.IsAny<TaskItem>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CheckoutSessionResult(sessionId, url, CheckoutSessionState.Open, DateTime.UtcNow.AddHours(24)));

    private static string NewKey() => Guid.NewGuid().ToString();

    private static TaskBillingController CreateController(IBillingService service, Guid userId, Role role) =>
        new(service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                        new Claim(ClaimTypes.Role, role.ToString())
                    ], "Test"))
                }
            }
        };
}
