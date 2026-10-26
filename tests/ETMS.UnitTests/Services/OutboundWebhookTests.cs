using System.Net;
using System.Text.Json;
using ETMS.Api.Common.Exceptions;
using ETMS.Api.Common.Settings;
using ETMS.Api.DTOs.Requests;
using ETMS.Api.Models.Entities;
using ETMS.Api.Models.Enums;
using ETMS.Api.Services.Implementations;
using ETMS.Api.Services.Interfaces;
using ETMS.Api.Services.Webhooks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ETMS.UnitTests.Services;

/// <summary>FR-HOOK-02/03/04: subscription registry, transactional outbox, signing, and retrying delivery.</summary>
public sealed class OutboundWebhookTests : IDisposable
{
    private const string ReceiverUrl = "https://hooks.partner.example.com/etms";

    private readonly TaskServiceTestContext _ctx = new();
    private readonly IDataProtectionProvider _dataProtection = new EphemeralDataProtectionProvider();
    private readonly RecordingHandler _receiver = new();
    private readonly WebhookSubscriptionService _subscriptions;
    private readonly WebhookDeliveryService _delivery;

    public OutboundWebhookTests()
    {
        _subscriptions = new WebhookSubscriptionService(_ctx.Db, _dataProtection, NullLogger<WebhookSubscriptionService>.Instance);

        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(WebhookDeliveryService.HttpClientName))
            .Returns(() => new HttpClient(_receiver, disposeHandler: false));
        _delivery = new WebhookDeliveryService(_ctx.Db, httpClientFactory.Object, _dataProtection,
            Options.Create(new WebhookSettings()), NullLogger<WebhookDeliveryService>.Instance);
    }

    public void Dispose() => _ctx.Dispose();

    // ---------------------------------------------------------------------
    // Subscription registry (FR-HOOK-02)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Create_ReturnsSecretOnceAndStoresItEncrypted()
    {
        var created = await _subscriptions.CreateAsync(Request(WebhookEventTypes.TaskCompleted));

        Assert.True(created.IsActive);
        Assert.StartsWith(WebhookSubscriptionService.SecretPrefix, created.SecretKey);
        Assert.True(created.SecretKey.Length >= WebhookSubscriptionService.SecretPrefix.Length + 43); // 32 random bytes

        var stored = await _ctx.Db.WebhookSubscriptions.AsNoTracking().SingleAsync();
        Assert.NotEqual(created.SecretKey, stored.SecretKey);
        Assert.DoesNotContain(created.SecretKey, stored.SecretKey);
        Assert.Equal(created.SecretKey,
            _dataProtection.CreateProtector(WebhookSubscriptionService.SecretProtectionPurpose).Unprotect(stored.SecretKey));
    }

    [Fact]
    public async Task Create_GeneratesDistinctSecretsPerSubscription()
    {
        var first = await _subscriptions.CreateAsync(Request(WebhookEventTypes.TaskCompleted));
        var second = await _subscriptions.CreateAsync(Request(WebhookEventTypes.TaskCreated));

        Assert.NotEqual(first.SecretKey, second.SecretKey);
    }

    [Theory]
    [InlineData("task.deleted")]
    [InlineData("TASK.COMPLETED")]
    [InlineData("")]
    public async Task Create_UnknownEventType_Returns400(string eventType)
    {
        await Assert.ThrowsAsync<BadRequestException>(() => _subscriptions.CreateAsync(Request(eventType)));
        Assert.Empty(_ctx.Db.WebhookSubscriptions);
    }

    [Theory]
    [InlineData("http://hooks.partner.example.com/etms")]   // not https
    [InlineData("ftp://hooks.partner.example.com/etms")]
    [InlineData("/relative/path")]
    [InlineData("https://localhost/hook")]
    [InlineData("https://api.localhost/hook")]
    [InlineData("https://127.0.0.1/hook")]
    [InlineData("https://10.0.0.5/hook")]
    [InlineData("https://172.16.4.2/hook")]
    [InlineData("https://192.168.1.10/hook")]
    [InlineData("https://169.254.169.254/latest/meta-data")] // cloud metadata endpoint
    [InlineData("https://[::1]/hook")]
    [InlineData("https://[fd00::1]/hook")]
    [InlineData("https://user:pass@hooks.partner.example.com/etms")]
    public async Task Create_DisallowedUrl_Returns400(string url)
    {
        var ex = await Assert.ThrowsAsync<BadRequestException>(() =>
            _subscriptions.CreateAsync(new CreateWebhookSubscriptionRequest { Url = url, EventType = WebhookEventTypes.TaskCompleted }));

        Assert.Equal(StatusCodes.Status400BadRequest, ex.StatusCode);
    }

    [Fact]
    public async Task Create_DuplicateActiveUrlAndEventType_Returns409()
    {
        await _subscriptions.CreateAsync(Request(WebhookEventTypes.TaskCompleted));

        await Assert.ThrowsAsync<ConflictException>(() => _subscriptions.CreateAsync(Request(WebhookEventTypes.TaskCompleted)));
    }

    [Fact]
    public async Task Create_SameUrlAfterDeactivation_IsAllowed()
    {
        var first = await _subscriptions.CreateAsync(Request(WebhookEventTypes.TaskCompleted));
        await _subscriptions.DeactivateAsync(first.Id);

        var second = await _subscriptions.CreateAsync(Request(WebhookEventTypes.TaskCompleted));

        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task List_MasksSecretAndReportsLastDelivery()
    {
        var created = await _subscriptions.CreateAsync(Request(WebhookEventTypes.TaskCompleted));
        AddDelivery(created.Id, attemptCount: 1, isSuccess: true, statusCode: 200, timestampUtc: DateTime.UtcNow.AddMinutes(-5));
        AddDelivery(created.Id, attemptCount: 2, isSuccess: false, statusCode: 503, timestampUtc: DateTime.UtcNow.AddMinutes(-1));
        AddDelivery(created.Id, attemptCount: 0, isSuccess: false, statusCode: null, timestampUtc: DateTime.UtcNow); // not attempted yet

        var page = await _subscriptions.GetSubscriptionsAsync(new WebhookSubscriptionFilterParams());

        var item = Assert.Single(page.Items);
        Assert.Equal("••••" + created.SecretKey[^4..], item.SecretKeyHint);
        Assert.DoesNotContain(created.SecretKey, JsonSerializer.Serialize(page));
        Assert.NotNull(item.LastDelivery);
        Assert.Equal(503, item.LastDelivery!.StatusCode);
        Assert.Equal(2, item.LastDelivery.AttemptCount);
        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public async Task List_FiltersByIsActive()
    {
        var active = await _subscriptions.CreateAsync(Request(WebhookEventTypes.TaskCompleted));
        var removed = await _subscriptions.CreateAsync(Request(WebhookEventTypes.TaskCreated));
        await _subscriptions.DeactivateAsync(removed.Id);

        var activeOnly = await _subscriptions.GetSubscriptionsAsync(new WebhookSubscriptionFilterParams { IsActive = true });
        var all = await _subscriptions.GetSubscriptionsAsync(new WebhookSubscriptionFilterParams());

        Assert.Equal(active.Id, Assert.Single(activeOnly.Items).Id);
        Assert.Equal(2, all.TotalCount);
    }

    [Fact]
    public async Task Deactivate_SoftDeletesAndIsIdempotent()
    {
        var created = await _subscriptions.CreateAsync(Request(WebhookEventTypes.TaskCompleted));

        await _subscriptions.DeactivateAsync(created.Id);
        await _subscriptions.DeactivateAsync(created.Id);

        var stored = await _ctx.Db.WebhookSubscriptions.AsNoTracking().SingleAsync();
        Assert.False(stored.IsActive);
        Assert.NotNull(stored.UpdatedAtUtc);
    }

    [Fact]
    public async Task Deactivate_UnknownId_Returns404()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => _subscriptions.DeactivateAsync(Guid.NewGuid()));
    }

    // ---------------------------------------------------------------------
    // Dispatcher / transactional outbox (FR-HOOK-03)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Enqueue_FansOutToMatchingAndWildcardActiveSubscriptions()
    {
        var exact = AddSubscription(WebhookEventTypes.TaskCompleted);
        var wildcard = AddSubscription(WebhookEventTypes.Wildcard);
        AddSubscription(WebhookEventTypes.TaskCreated);
        AddSubscription(WebhookEventTypes.TaskCompleted, isActive: false);

        await _ctx.Webhooks.EnqueueAsync(WebhookEventTypes.TaskCompleted, new { taskId = Guid.NewGuid() });
        await _ctx.Db.SaveChangesAsync();

        var rows = await _ctx.Db.WebhookDeliveryLogs.AsNoTracking().ToListAsync();
        Assert.Equal(new[] { exact.Id, wildcard.Id }.OrderBy(id => id), rows.Select(r => r.SubscriptionId).OrderBy(id => id));
        Assert.All(rows, r =>
        {
            Assert.Equal(WebhookEventTypes.TaskCompleted, r.EventType);
            Assert.Equal(0, r.AttemptCount);
            Assert.False(r.IsSuccess);
            Assert.NotNull(r.NextRetryUtc);
        });
        Assert.Equal(rows[0].Payload, rows[1].Payload); // one event id shared by all deliveries
    }

    [Fact]
    public async Task Enqueue_DoesNotSaveOnItsOwn()
    {
        AddSubscription(WebhookEventTypes.Wildcard);

        await _ctx.Webhooks.EnqueueAsync(WebhookEventTypes.TaskCreated, new { taskId = Guid.NewGuid() });

        Assert.Single(_ctx.Db.ChangeTracker.Entries<WebhookDeliveryLog>(), e => e.State == EntityState.Added);
        Assert.Equal(0, await _ctx.Db.WebhookDeliveryLogs.CountAsync());
    }

    [Fact]
    public async Task CompletingTask_EnqueuesStatusChangedAndCompletedEnvelopes()
    {
        AddSubscription(WebhookEventTypes.Wildcard);
        var task = _ctx.AddTask(_ctx.Employee.Id, TaskLifecycleStatus.InReview);

        await _ctx.Service.UpdateTaskStatusAsync(_ctx.Employee.Id, task.Id,
            new UpdateTaskStatusRequest { NewStatus = TaskLifecycleStatus.Completed });

        var rows = await _ctx.Db.WebhookDeliveryLogs.AsNoTracking().ToListAsync();
        Assert.Equal(
            new[] { WebhookEventTypes.TaskCompleted, WebhookEventTypes.TaskStatusChanged },
            rows.Select(r => r.EventType).OrderBy(t => t));

        using var statusChanged = JsonDocument.Parse(rows.Single(r => r.EventType == WebhookEventTypes.TaskStatusChanged).Payload);
        var root = statusChanged.RootElement;
        Assert.True(Guid.TryParse(root.GetProperty("id").GetString(), out _));
        Assert.Equal(WebhookEventTypes.TaskStatusChanged, root.GetProperty("type").GetString());
        Assert.True(root.TryGetProperty("occurredAtUtc", out _));
        var data = root.GetProperty("data");
        Assert.Equal(task.Id, data.GetProperty("taskId").GetGuid());
        Assert.Equal("InReview", data.GetProperty("oldStatus").GetString());
        Assert.Equal("Completed", data.GetProperty("newStatus").GetString());
        Assert.Equal(_ctx.Employee.Id, data.GetProperty("changedByUserId").GetGuid());

        using var completed = JsonDocument.Parse(rows.Single(r => r.EventType == WebhookEventTypes.TaskCompleted).Payload);
        Assert.Equal(_ctx.Employee.Id, completed.RootElement.GetProperty("data").GetProperty("assigneeId").GetGuid());
    }

    [Fact]
    public async Task CreatingAndReassigningTask_EnqueueTheirEvents()
    {
        AddSubscription(WebhookEventTypes.Wildcard);

        var created = await _ctx.Service.CreateTaskAsync(_ctx.Manager.Id, TaskServiceTestContext.CreateRequest(_ctx.Employee.Id));
        await _ctx.Service.ReassignTaskAsync(_ctx.Manager.Id, created.Id, new ReassignTaskRequest { NewAssigneeId = _ctx.Teammate.Id });

        var rows = await _ctx.Db.WebhookDeliveryLogs.AsNoTracking().ToListAsync();
        Assert.Contains(rows, r => r.EventType == WebhookEventTypes.TaskCreated);
        var reassigned = rows.Single(r => r.EventType == WebhookEventTypes.TaskReassigned);
        using var doc = JsonDocument.Parse(reassigned.Payload);
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal(_ctx.Employee.Id, data.GetProperty("previousAssigneeId").GetGuid());
        Assert.Equal(_ctx.Teammate.Id, data.GetProperty("newAssigneeId").GetGuid());
    }

    [Fact]
    public async Task RejectedTaskChange_EnqueuesNothing()
    {
        AddSubscription(WebhookEventTypes.Wildcard);
        var task = _ctx.AddTask(_ctx.Employee.Id, TaskLifecycleStatus.Pending);

        // FR-TASK-02: skipping a lifecycle step is rejected.
        await Assert.ThrowsAsync<BadRequestException>(() => _ctx.Service.UpdateTaskStatusAsync(_ctx.Employee.Id, task.Id,
            new UpdateTaskStatusRequest { NewStatus = TaskLifecycleStatus.Completed }));

        Assert.Equal(0, await _ctx.Db.WebhookDeliveryLogs.CountAsync());
    }

    [Fact]
    public async Task OverdueAlert_EnqueuesTaskOverdueOnce()
    {
        AddSubscription(WebhookEventTypes.TaskOverdue);
        var task = _ctx.AddTask(_ctx.Employee.Id, TaskLifecycleStatus.InProgress);
        task.DueDateUtc = DateTime.UtcNow.AddHours(-2);
        _ctx.Db.SaveChanges();
        var notifications = new NotificationService(_ctx.Db, new Mock<IEmailService>().Object, _ctx.Webhooks);

        await notifications.SendOverdueAlertsAsync();
        await notifications.SendOverdueAlertsAsync();

        var row = Assert.Single(_ctx.Db.WebhookDeliveryLogs.AsNoTracking());
        Assert.Equal(WebhookEventTypes.TaskOverdue, row.EventType);
        Assert.Contains(task.Id.ToString(), row.Payload);
    }

    // ---------------------------------------------------------------------
    // Signing (FR-HOOK-04)
    // ---------------------------------------------------------------------

    [Fact]
    public void ComputeSignature_MatchesKnownHmacSha256Vector()
    {
        // printf '%s' '1700000000.{"type":"task.completed"}' | openssl dgst -sha256 -hmac 'whsec_etms_known_vector'
        var signature = WebhookSigner.ComputeSignature("whsec_etms_known_vector", 1_700_000_000, """{"type":"task.completed"}""");

        Assert.Equal("c65f4159e6d46a446d0d70b89cbefb7b7bfd7adf541ddd30abba89869b281ce1", signature);
    }

    [Fact]
    public void BuildHeader_UsesTimestampAndV1Scheme()
    {
        var header = WebhookSigner.BuildHeader("secret", 1_700_000_000, "{}");

        Assert.Equal($"t=1700000000,v1={WebhookSigner.ComputeSignature("secret", 1_700_000_000, "{}")}", header);
    }

    // ---------------------------------------------------------------------
    // Retry policy (FR-HOOK-04)
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 120)]
    [InlineData(3, 600)]
    [InlineData(4, 3600)]
    [InlineData(5, 21600)]
    public void FailedAttempt_SchedulesExponentialBackoff(int attemptsSoFar, int expectedDelaySeconds)
    {
        var now = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
        var delivery = new WebhookDeliveryLog { AttemptCount = attemptsSoFar - 1 };

        var deadLettered = WebhookRetryPolicy.RecordAttempt(delivery, 500, now, maxAttempts: 6);

        Assert.False(deadLettered);
        Assert.Equal(attemptsSoFar, delivery.AttemptCount);
        Assert.Equal(500, delivery.StatusCode);
        Assert.False(delivery.IsSuccess);
        Assert.Equal(now, delivery.TimestampUtc);
        Assert.Equal(now.AddSeconds(expectedDelaySeconds), delivery.NextRetryUtc);
    }

    [Fact]
    public void SixthFailedAttempt_DeadLetters()
    {
        var delivery = new WebhookDeliveryLog { AttemptCount = 5 };

        var deadLettered = WebhookRetryPolicy.RecordAttempt(delivery, null, DateTime.UtcNow, maxAttempts: 6);

        Assert.True(deadLettered);
        Assert.Equal(6, delivery.AttemptCount);
        Assert.False(delivery.IsSuccess);
        Assert.Null(delivery.StatusCode);
        Assert.Null(delivery.NextRetryUtc);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(202)]
    [InlineData(204)]
    public void SuccessfulAttempt_ClearsSchedule(int statusCode)
    {
        var delivery = new WebhookDeliveryLog { AttemptCount = 2 };

        var deadLettered = WebhookRetryPolicy.RecordAttempt(delivery, statusCode, DateTime.UtcNow, maxAttempts: 6);

        Assert.False(deadLettered);
        Assert.True(delivery.IsSuccess);
        Assert.Equal(3, delivery.AttemptCount);
        Assert.Null(delivery.NextRetryUtc);
    }

    // ---------------------------------------------------------------------
    // Delivery worker (FR-HOOK-04)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Deliver_PostsSignedPayloadAndMarksSuccess()
    {
        var created = await _subscriptions.CreateAsync(Request(WebhookEventTypes.TaskCompleted));
        var delivery = AddDelivery(created.Id);

        var attempted = await _delivery.DeliverDueAsync();

        Assert.Equal(1, attempted);
        var (request, body) = Assert.Single(_receiver.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(ReceiverUrl, request.RequestUri!.ToString());
        Assert.Equal(delivery.Payload, body);
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal(WebhookEventTypes.TaskCompleted, request.Headers.GetValues(WebhookSigner.EventHeader).Single());
        Assert.Equal(delivery.Id.ToString(), request.Headers.GetValues(WebhookSigner.DeliveryHeader).Single());

        // A receiver holding the secret can verify the signature.
        var signature = request.Headers.GetValues(WebhookSigner.SignatureHeader).Single();
        var timestamp = long.Parse(signature.Split(',')[0]["t=".Length..]);
        Assert.InRange(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - timestamp, 0, 60);
        Assert.Equal(WebhookSigner.BuildHeader(created.SecretKey, timestamp, body), signature);

        var stored = await _ctx.Db.WebhookDeliveryLogs.AsNoTracking().SingleAsync();
        Assert.True(stored.IsSuccess);
        Assert.Equal(200, stored.StatusCode);
        Assert.Equal(1, stored.AttemptCount);
        Assert.Null(stored.NextRetryUtc);
    }

    [Fact]
    public async Task Deliver_Non2xx_RecordsStatusAndSchedulesRetryIn30Seconds()
    {
        var created = await _subscriptions.CreateAsync(Request(WebhookEventTypes.TaskCompleted));
        AddDelivery(created.Id);
        _receiver.Respond = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        var before = DateTime.UtcNow;

        await _delivery.DeliverDueAsync();

        var stored = await _ctx.Db.WebhookDeliveryLogs.AsNoTracking().SingleAsync();
        Assert.False(stored.IsSuccess);
        Assert.Equal(503, stored.StatusCode);
        Assert.Equal(1, stored.AttemptCount);
        Assert.InRange(stored.NextRetryUtc!.Value, before.AddSeconds(30), DateTime.UtcNow.AddSeconds(30));
    }

    [Fact]
    public async Task Deliver_RedirectIsNotFollowedAndCountsAsFailure()
    {
        var created = await _subscriptions.CreateAsync(Request(WebhookEventTypes.TaskCompleted));
        AddDelivery(created.Id);
        _receiver.Respond = _ => new HttpResponseMessage(HttpStatusCode.Found);

        await _delivery.DeliverDueAsync();

        var stored = await _ctx.Db.WebhookDeliveryLogs.AsNoTracking().SingleAsync();
        Assert.False(stored.IsSuccess);
        Assert.Equal(302, stored.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Deliver_NetworkErrorOrTimeout_RecordsNullStatusAndRetries(bool timeout)
    {
        var created = await _subscriptions.CreateAsync(Request(WebhookEventTypes.TaskCompleted));
        AddDelivery(created.Id);
        _receiver.Respond = _ => timeout
            ? throw new TaskCanceledException("The request timed out.")
            : throw new HttpRequestException("Connection refused.");

        await _delivery.DeliverDueAsync();

        var stored = await _ctx.Db.WebhookDeliveryLogs.AsNoTracking().SingleAsync();
        Assert.False(stored.IsSuccess);
        Assert.Null(stored.StatusCode);
        Assert.Equal(1, stored.AttemptCount);
        Assert.NotNull(stored.NextRetryUtc);
    }

    [Fact]
    public async Task Deliver_FinalFailedAttempt_DeadLettersRow()
    {
        var created = await _subscriptions.CreateAsync(Request(WebhookEventTypes.TaskCompleted));
        AddDelivery(created.Id, attemptCount: 5);
        _receiver.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        await _delivery.DeliverDueAsync();
        var secondPass = await _delivery.DeliverDueAsync();

        var stored = await _ctx.Db.WebhookDeliveryLogs.AsNoTracking().SingleAsync();
        Assert.Equal(6, stored.AttemptCount);
        Assert.False(stored.IsSuccess);
        Assert.Null(stored.NextRetryUtc);
        Assert.Equal(0, secondPass);
        Assert.Single(_receiver.Requests);
    }

    [Fact]
    public async Task Deliver_SkipsRowsNotYetDueAndInactiveSubscriptions()
    {
        var created = await _subscriptions.CreateAsync(Request(WebhookEventTypes.TaskCompleted));
        AddDelivery(created.Id, attemptCount: 1, nextRetryUtc: DateTime.UtcNow.AddMinutes(1));
        var removed = await _subscriptions.CreateAsync(Request(WebhookEventTypes.TaskCreated));
        AddDelivery(removed.Id);
        await _subscriptions.DeactivateAsync(removed.Id);

        var attempted = await _delivery.DeliverDueAsync();

        Assert.Equal(0, attempted);
        Assert.Empty(_receiver.Requests);
        var inactiveRow = await _ctx.Db.WebhookDeliveryLogs.AsNoTracking().SingleAsync(d => d.SubscriptionId == removed.Id);
        Assert.Equal(0, inactiveRow.AttemptCount); // no attempt consumed
    }

    // ---------------------------------------------------------------------
    // SSRF guard (SDD §10.2)
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("93.184.216.34", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("192.168.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("::ffff:10.0.0.1", false)] // IPv4-mapped private address
    public void IsPublicAddress_ClassifiesRanges(string address, bool expected)
    {
        Assert.Equal(expected, SsrfGuard.IsPublicAddress(IPAddress.Parse(address)));
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static CreateWebhookSubscriptionRequest Request(string eventType) => new() { Url = ReceiverUrl, EventType = eventType };

    private WebhookSubscription AddSubscription(string eventType, bool isActive = true)
    {
        var subscription = new WebhookSubscription
        {
            Id = Guid.NewGuid(),
            Url = $"https://hooks.example.com/{Guid.NewGuid():N}",
            SecretKey = _dataProtection.CreateProtector(WebhookSubscriptionService.SecretProtectionPurpose).Protect("whsec_etms_test"),
            EventType = eventType,
            IsActive = isActive,
            CreatedAtUtc = DateTime.UtcNow
        };
        _ctx.Db.WebhookSubscriptions.Add(subscription);
        _ctx.Db.SaveChanges();
        return subscription;
    }

    private WebhookDeliveryLog AddDelivery(
        Guid subscriptionId,
        int attemptCount = 0,
        bool isSuccess = false,
        int? statusCode = null,
        DateTime? timestampUtc = null,
        DateTime? nextRetryUtc = null)
    {
        var now = DateTime.UtcNow;
        var delivery = new WebhookDeliveryLog
        {
            Id = Guid.NewGuid(),
            SubscriptionId = subscriptionId,
            EventType = WebhookEventTypes.TaskCompleted,
            Payload = $$$"""{"id":"{{{Guid.NewGuid()}}}","type":"task.completed","data":{"taskId":"{{{Guid.NewGuid()}}}"}}""",
            AttemptCount = attemptCount,
            IsSuccess = isSuccess,
            StatusCode = statusCode,
            TimestampUtc = timestampUtc ?? now,
            NextRetryUtc = nextRetryUtc ?? now.AddSeconds(-1)
        };
        _ctx.Db.WebhookDeliveryLogs.Add(delivery);
        _ctx.Db.SaveChanges();
        return delivery;
    }

    /// <summary>Stands in for the receiver; records every request with its body.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request, await request.Content!.ReadAsStringAsync(cancellationToken)));
            return Respond(request);
        }
    }
}
