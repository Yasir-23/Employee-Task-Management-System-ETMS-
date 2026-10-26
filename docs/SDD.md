# System Design Document (SDD)
## Project: Employee Task Management System (ETMS)
**Document Version:** 1.1.0 (adds Milestones 6–9)  
**Target Runtime:** .NET 8 (ASP.NET Core Web API)  
**Database:** PostgreSQL 16 (via Entity Framework Core 8)  
**Cache:** Redis 7 (via `Microsoft.Extensions.Caching.StackExchangeRedis`)  
**Integrations:** Stripe (via `Stripe.net`), QuestPDF, `Microsoft.Extensions.Http.Resilience` (Polly v8)  

---

### 1. Architectural Topology & Layering

The application implements a classic Layered Architecture. Dependencies flow strictly in one direction:

- Presentation Layer: Controllers with [Authorize] RBAC, Global Exception Handling, and DTOs.
- Business Service Layer: TaskService (enforcing BR-01 through BR-05), AuthService, EmployeeService, NotificationService, and BackgroundService.
- Data & Persistence Layer: AppDbContext, Auto-Audit SaveChangesInterceptor, and EF Core Migrations.
- Database: PostgreSQL 16.

Milestones 6–9 add an **Infrastructure Adapter** tier. Business services depend on its interfaces; the implementations wrap external systems. Controllers never call adapters directly, with one exception: the inbound Stripe webhook controller asks the gateway to verify the signature (§10.1).

```text
 Presentation    TasksController  AttachmentsController  ReportsController  BillingController
                 StripeWebhooksController  WebhookSubscriptionsController
                        |
 Business        TaskService  AttachmentService  ReportService (+ CachedReportService decorator)
                 BillingService  WebhookSubscriptionService  IWebhookDispatcher
                        |
 Infrastructure  IStorageService --> LocalFileStorageService --> /app/storage (Docker volume)
 Adapters        IPdfReportGenerator --> QuestPdfReportGenerator
                 IPaymentGatewayService --> StripePaymentGatewayService --> HttpClient "Stripe"
                 IWebhookSender --> HttpWebhookSender --> HttpClient "OutboundWebhooks"
                 IDistributedCache --> Redis
                        |
 Background      TaskNotificationWorker   WebhookDeliveryWorker
                        |
 Persistence     AppDbContext (PostgreSQL 16)
```

The two named `HttpClient`s carry the Polly v8 resilience pipelines described in §12.

---

### 2. Database Schema & Entity Relationship Model

```text
  +------------------+         +------------------+
  |   Departments    |         |   RefreshTokens  |
  +------------------+         +------------------+
  | Id (PK)          |         | Id (PK)          |
  | Name             |         | TokenHash        |
  | CreatedAtUtc     |         | UserId (FK)      |
  +--------+---------+         | ExpiresAtUtc     |
           |                   | IsRevoked        |
           | 1                 +--------+---------+
           |                            |
           | N                          | N
  +--------+---------+                  |
  |      Users       |<-----------------+ 1
  +------------------+
  | Id (PK)          |
  | FullName         |
  | Email (UQ)       |
  | PasswordHash     |
  | Role (Enum)      |
  | DepartmentId(FK) |
  | ManagerId (FK)---+ (Self-Referencing 1:N)
  | CreatedAtUtc     |
  +---+----------+---+
      | 1        | 1
      |          |
      | N        | N
  +---+----------+---+         +------------------+
  |      Tasks       | 1     N |     Comments     |
  +------------------+<--------+------------------+
  | Id (PK)          |         | Id (PK)          |
  | Title            |         | TaskId (FK)      |
  | Description      |         | UserId (FK)      |
  | Priority (Enum)  |         | Content          |
  | Status (Enum)    |         | CreatedAtUtc     |
  | DueDateUtc       |         +------------------+
  | CreatedBy(FK)    |
  | AssigneeId (FK)  |         +------------------+
  | ReminderSent     | 1     N |    AuditLogs     |
  | OverdueAlertSent |<--------+------------------+
  | CreatedAtUtc     |         | Id (PK)          |
  | UpdatedAtUtc     |         | TaskId (FK)      |
  +------------------+         | ChangedBy(FK)    |
                               | PropertyName     |
                               | OldValue         |
                               | NewValue         |
                               | TimestampUtc     |
                               +------------------+
```

#### 2.1 Milestone 6–9 Entities

`Tasks` and `Users` refer to the tables in the diagram above.

```text
  +------------------------+  N              1  +------------------+
  |    TaskAttachments     |------------------->|      Users       |
  +------------------------+  UploadedByUserId  +------------------+
  | Id (PK)                |                    | Id (PK)          |
  | TaskId (FK)            |                    +------------------+
  | FileName               |
  | StoragePath (UQ)       |
  | ContentType            |
  | FileSizeBytes          |
  | UploadedByUserId (FK)  |
  | UploadedAtUtc          |
  +-----------+------------+
              | N
              |
              | 1
  +-----------+------------+  1              1  +------------------------+
  |         Tasks          |<-------------------+      TaskBillings      |
  +------------------------+                    +------------------------+
  | Id (PK)                |                    | Id (PK)                |
  | ...                    |                    | TaskId (FK, UQ)        |
  +------------------------+                    | StripePaymentIntentId  |
                                                | StripeSessionId (UQ)   |
                                                | AmountCents            |
                                                | Currency               |
                                                | BillingStatus (Enum)   |
                                                | PaidAtUtc              |
                                                +------------------------+

  +------------------------+  1              N  +------------------------+
  |  WebhookSubscriptions  |<-------------------+  WebhookDeliveryLogs   |
  +------------------------+                    +------------------------+
  | Id (PK)                |                    | Id (PK)                |
  | Url                    |                    | SubscriptionId (FK)    |
  | SecretKey (encrypted)  |                    | EventType              |
  | EventType              |                    | Payload (jsonb)        |
  | IsActive               |                    | StatusCode             |
  | CreatedAtUtc           |                    | IsSuccess              |
  +------------------------+                    | AttemptCount           |
                                                | TimestampUtc           |
                                                +------------------------+
```

**Relationships:**

| Child | Parent | Cardinality | On Delete | Notes |
| :--- | :--- | :--- | :--- | :--- |
| `TaskAttachments.TaskId` | `Tasks.Id` | N : 1 | CASCADE | When the database row is removed, `AttachmentService` deletes the stored file. |
| `TaskAttachments.UploadedByUserId` | `Users.Id` | N : 1 | RESTRICT | Keeps the record of who uploaded each file. |
| `TaskBillings.TaskId` | `Tasks.Id` | 1 : 0..1 | RESTRICT | Financial records must never be cascade-deleted. A unique index on `TaskId` enforces one billing per task. |
| `WebhookDeliveryLogs.SubscriptionId` | `WebhookSubscriptions.Id` | N : 1 | RESTRICT | Subscriptions are soft-deleted (`IsActive = false`), so delivery history is kept. |

The webhook tables intentionally have no foreign key to `Tasks` or `Users`. The task ID is carried inside `Payload`, so delivery history is not tied to the lifetime of task rows.

3. Data Dictionary
3.1 Departments Table
Id: UUID / Guid, PRIMARY KEY

Name: VARCHAR(100), NOT NULL, UNIQUE

CreatedAtUtc: TIMESTAMP WITH TIME ZONE, NOT NULL

3.2 Users Table
Id: UUID / Guid, PRIMARY KEY

FullName: VARCHAR(150), NOT NULL

Email: VARCHAR(256), NOT NULL, UNIQUE

PasswordHash: TEXT, NOT NULL

Role: INT (Enum: 0 = Admin, 1 = Manager, 2 = Employee), NOT NULL

DepartmentId: UUID, Foreign Key -> Departments(Id), NULLABLE for SuperAdmin

ManagerId: UUID, Foreign Key -> Users(Id), NULLABLE (Self-referencing manager hierarchy)

CreatedAtUtc: TIMESTAMP WITH TIME ZONE, NOT NULL

3.3 RefreshTokens Table
Id: UUID / Guid, PRIMARY KEY

UserId: UUID, Foreign Key -> Users(Id), ON DELETE CASCADE

TokenHash: VARCHAR(256), NOT NULL, INDEXED

ExpiresAtUtc: TIMESTAMP WITH TIME ZONE, NOT NULL

IsRevoked: BOOLEAN, NOT NULL, DEFAULT FALSE

3.4 Tasks Table
Id: UUID / Guid, PRIMARY KEY

Title: VARCHAR(200), NOT NULL

Description: TEXT, NOT NULL

Priority: INT (Enum: 0 = Low, 1 = Medium, 2 = High), NOT NULL

Status: INT (Enum: 0 = Pending, 1 = InProgress, 2 = InReview, 3 = Completed), NOT NULL

DueDateUtc: TIMESTAMP WITH TIME ZONE, NOT NULL

CreatedByUserId: UUID, Foreign Key -> Users(Id), NOT NULL

AssigneeId: UUID, Foreign Key -> Users(Id), NULLABLE

ReminderSent: BOOLEAN, NOT NULL, DEFAULT FALSE

OverdueAlertSent: BOOLEAN, NOT NULL, DEFAULT FALSE

CreatedAtUtc: TIMESTAMP WITH TIME ZONE, NOT NULL

UpdatedAtUtc: TIMESTAMP WITH TIME ZONE, NOT NULL

3.5 Comments Table
Id: UUID / Guid, PRIMARY KEY

TaskId: UUID, Foreign Key -> Tasks(Id), ON DELETE CASCADE

UserId: UUID, Foreign Key -> Users(Id), NOT NULL

Content: TEXT, NOT NULL

CreatedAtUtc: TIMESTAMP WITH TIME ZONE, NOT NULL

3.6 AuditLogs Table
Id: BIGINT, PRIMARY KEY, IDENTITY

TaskId: UUID, Foreign Key -> Tasks(Id), ON DELETE CASCADE

ChangedByUserId: UUID, Foreign Key -> Users(Id), NOT NULL

PropertyName: VARCHAR(100), NOT NULL (e.g., Status, AssigneeId, DueDateUtc)

OldValue: TEXT, NULLABLE

NewValue: TEXT, NULLABLE

TimestampUtc: TIMESTAMP WITH TIME ZONE, NOT NULL

#### 3.7 TaskAttachments Table (Milestone 6)

| Column | Type | Constraints / Notes |
| :--- | :--- | :--- |
| `Id` | UUID | PRIMARY KEY |
| `TaskId` | UUID | FK → `Tasks(Id)`, ON DELETE CASCADE, NOT NULL |
| `FileName` | VARCHAR(255) | NOT NULL. Original client filename, sanitized (path segments and control characters stripped). Used only for display and `Content-Disposition`. |
| `StoragePath` | VARCHAR(500) | NOT NULL, UNIQUE. Key relative to the storage root: `attachments/{TaskId}/{Id}{ext}`. |
| `ContentType` | VARCHAR(127) | NOT NULL. One of the BR-06 allow-listed MIME types. |
| `FileSizeBytes` | BIGINT | NOT NULL, CHECK (`FileSizeBytes BETWEEN 1 AND 10485760`) |
| `UploadedByUserId` | UUID | FK → `Users(Id)`, ON DELETE RESTRICT, NOT NULL |
| `UploadedAtUtc` | TIMESTAMP WITH TIME ZONE | NOT NULL |

#### 3.8 TaskBillings Table (Milestone 7)

| Column | Type | Constraints / Notes |
| :--- | :--- | :--- |
| `Id` | UUID | PRIMARY KEY. Sent to Stripe as `client_reference_id` and in metadata. |
| `TaskId` | UUID | FK → `Tasks(Id)`, ON DELETE RESTRICT, NOT NULL, UNIQUE |
| `StripePaymentIntentId` | VARCHAR(255) | NULLABLE. Set from the webhook when payment completes. |
| `StripeSessionId` | VARCHAR(255) | NULLABLE, UNIQUE (where not null). The most recent Checkout Session. |
| `AmountCents` | BIGINT | NOT NULL, CHECK (`AmountCents BETWEEN 50 AND 99999999`) |
| `Currency` | CHAR(3) | NOT NULL, DEFAULT `'usd'`. Lowercase ISO 4217. |
| `BillingStatus` | INT | NOT NULL. Enum: 0 = Unpaid, 1 = Processing, 2 = Paid, 3 = Failed |
| `PaidAtUtc` | TIMESTAMP WITH TIME ZONE | NULLABLE. Set only on the transition to `Paid`. |
| *(concurrency)* | `xmin` | PostgreSQL system column mapped as an EF Core row-version shadow property. No physical column. Serializes checkout and webhook updates (FR-PAY-04). |

#### 3.9 WebhookSubscriptions Table (Milestone 8)

| Column | Type | Constraints / Notes |
| :--- | :--- | :--- |
| `Id` | UUID | PRIMARY KEY |
| `Url` | VARCHAR(2048) | NOT NULL. Absolute `https` URL that passes the SSRF checks in §10.2. |
| `SecretKey` | TEXT | NOT NULL. 32 random bytes, protected with ASP.NET Core Data Protection (purpose `ETMS.Webhooks.Secret`). Never returned after creation. |
| `EventType` | VARCHAR(64) | NOT NULL. One of `task.created`, `task.reassigned`, `task.status_changed`, `task.completed`, or `*` |
| `IsActive` | BOOLEAN | NOT NULL, DEFAULT TRUE |
| `CreatedAtUtc` | TIMESTAMP WITH TIME ZONE | NOT NULL |

#### 3.10 WebhookDeliveryLogs Table (Milestone 8)

There is one row per (event, subscription) delivery. The row is created in the task transaction (outbox) and updated after each attempt.

| Column | Type | Constraints / Notes |
| :--- | :--- | :--- |
| `Id` | UUID | PRIMARY KEY. Sent to receivers as `X-ETMS-Delivery`. |
| `SubscriptionId` | UUID | FK → `WebhookSubscriptions(Id)`, ON DELETE RESTRICT, NOT NULL |
| `EventType` | VARCHAR(64) | NOT NULL. The concrete event type (never `*`). |
| `Payload` | JSONB | NOT NULL. The full event envelope, frozen at enqueue time. |
| `StatusCode` | INT | NULLABLE. HTTP status of the last attempt. NULL if no attempt yet or if there was a network error or timeout. |
| `IsSuccess` | BOOLEAN | NOT NULL, DEFAULT FALSE |
| `AttemptCount` | INT | NOT NULL, DEFAULT 0. `0` = not yet attempted; `6` with `IsSuccess = false` = dead-lettered. |
| `NextRetryUtc` | TIMESTAMP WITH TIME ZONE | NULLABLE. When the worker may next attempt the row: enqueue time initially, then last attempt + backoff (§10.3). NULL once delivered or dead-lettered. |
| `TimestampUtc` | TIMESTAMP WITH TIME ZONE | NOT NULL. Enqueue time while `AttemptCount = 0`, otherwise the time of the last attempt. |

4. Database Indexes
IX_Users_Email: Unique index on Users(Email)

IX_Users_ManagerId: Index on Users(ManagerId)

IX_Tasks_Assignee_Status: Composite index on Tasks(AssigneeId, Status) for rapid 5-task rule validation

IX_Tasks_DueDate_Status: Composite index on Tasks(DueDateUtc, Status) for background worker queries

IX_AuditLogs_TaskId: Index on AuditLogs(TaskId)

IX_TaskAttachments_TaskId: Index on TaskAttachments(TaskId)

UX_TaskAttachments_StoragePath: Unique index on TaskAttachments(StoragePath)

UX_TaskBillings_TaskId: Unique index on TaskBillings(TaskId)

UX_TaskBillings_StripeSessionId: Unique partial index on TaskBillings(StripeSessionId) WHERE StripeSessionId IS NOT NULL (webhook lookup)

IX_WebhookSubscriptions_EventType_IsActive: Composite index on WebhookSubscriptions(EventType, IsActive) for fan-out at enqueue time

IX_WebhookDeliveryLogs_Pending: Partial index on WebhookDeliveryLogs(NextRetryUtc) WHERE IsSuccess = FALSE AND AttemptCount < 6 (worker polling)

IX_WebhookDeliveryLogs_SubscriptionId: Index on WebhookDeliveryLogs(SubscriptionId)

5. Automated Audit Logging via EF Core Interceptor
State changes are captured automatically using an EF Core SaveChangesInterceptor:

Interceptor overrides SavingChangesAsync.

Inspects ChangeTracker.Entries<Task>() where state is Modified.

For tracked properties (Status, AssigneeId, DueDateUtc), if modified, it creates a new AuditLog record with OriginalValue and CurrentValue.

Adds the audit records to the context so they commit in the same atomic database transaction.

6. Background Worker Engine (TaskNotificationWorker)
A background service (BackgroundService) runs periodically (every 1 hour in production):

Creates an IServiceScope to resolve AppDbContext and IEmailService.

24-Hour Reminder Check: Queries active tasks where DueDateUtc is between now + 23h and now + 24h and ReminderSent == false. Dispatches email and marks ReminderSent = true.

Overdue Escalation Check: Queries active tasks where DueDateUtc < now and OverdueAlertSent == false. Dispatches alert email to the task creator (Manager) and marks OverdueAlertSent = true.

---

### 7. Storage Subsystem (Milestone 6)

#### 7.1 Contract

```csharp
public interface IStorageService
{
    /// Writes the stream to the given server-generated key and returns the bytes written.
    Task<long> SaveAsync(string storagePath, Stream content, CancellationToken cancellationToken = default);

    /// Opens a read-only, seekable stream (FileOptions.Asynchronous | SequentialScan). Throws NotFoundException if missing.
    Task<Stream> OpenReadAsync(string storagePath, CancellationToken cancellationToken = default);

    Task DeleteAsync(string storagePath, CancellationToken cancellationToken = default);
}
```

`LocalFileStorageService` is the only provider in this phase. The interface is deliberately provider-neutral, so an S3 or Azure Blob implementation can be swapped in later without touching `AttachmentService`.

#### 7.2 Local Provider & Docker Volume

- Root directory comes from `Storage:RootPath` (default `/app/storage` in the container, `./storage` locally).
- In `docker-compose.yml`, the named volume `etms_attachments` is mounted at `/app/storage` so files survive container rebuilds. The directory must be writable by the non-root `$APP_UID` user set in the runtime stage of the `Dockerfile`.
- Path-traversal guard: every key is resolved with `Path.GetFullPath(Path.Combine(root, key))` and rejected unless the result starts with the normalized root.
- Files are written to `{key}.tmp` and then atomically renamed to `{key}`, so readers never see a partial file.

#### 7.3 Upload Pipeline (`AttachmentService.UploadAsync`)

```text
 multipart request
   | [RequestSizeLimit(10 MB + 64 KB)] + FormOptions.MultipartBodyLengthLimit   -> 413 (BR-06)
   v
 1. Authorize: caller is assignee, creator, or Admin of the task                  -> 403 / 404
 2. Validate: length in 1..10,485,760; extension in allow-list;
    declared Content-Type matches extension                                       -> 400 / 413 / 415
 3. Sniff signature bytes from the stream (no full buffering):
      PDF  25 50 44 46 ("%PDF")      PNG  89 50 4E 47 0D 0A 1A 0A
      JPEG FF D8 FF                  DOCX 50 4B 03 04 (ZIP container)             -> 415 on mismatch
 4. Id = Guid.NewGuid(); StoragePath = attachments/{TaskId}/{Id}{ext}
 5. IStorageService.SaveAsync (streamed copy)
 6. Insert TaskAttachment + SaveChangesAsync
      on failure -> compensating IStorageService.DeleteAsync, then rethrow
 7. 201 Created + AttachmentDto
```

#### 7.4 Download Pipeline

The controller loads the `TaskAttachment` and checks it with `WHERE Id = @attachmentId AND TaskId = @taskId`. It applies the same visibility scope as `GET /api/tasks/{id}`, then returns `File(stream, ContentType, FileName, enableRangeProcessing: true)`. ASP.NET Core streams the file in chunks and handles `Range` / `206 Partial Content`. The response always includes `X-Content-Type-Options: nosniff` and `Content-Disposition: attachment`, so browsers never render uploaded content inline.

---

### 8. PDF Engine (Milestone 6)

#### 8.1 Components

| Component | Responsibility |
| :--- | :--- |
| `IPdfReportGenerator` / `QuestPdfReportGenerator` | `byte[] GenerateDepartmentPerformance(DepartmentPerformanceReportModel model)` |
| `DepartmentPerformanceDocument : IDocument` | QuestPDF template. Pure layout with no I/O or DB access. |
| `DepartmentPerformanceReportModel` | `From`, `To`, `GeneratedAtUtc`, `GeneratedBy`, `IReadOnlyList<DepartmentPerformanceDto> Rows`, plus computed totals. |

At startup, `Program.cs` sets `QuestPDF.Settings.License = LicenseType.Community`. The Community license is free only for organizations below QuestPDF's revenue threshold, so eligibility must be confirmed before production.

#### 8.2 Document Template Structure

```text
 Page (A4 portrait, 1.5 cm margins, default font 10 pt)
 +-- Header
 |     "Department Performance Report"            (title, 18 pt, semibold)
 |     Window: {From:yyyy-MM-dd} - {To:yyyy-MM-dd}
 |     Generated {GeneratedAtUtc:u} by {GeneratedBy}
 +-- Content
 |     Table
 |       Columns: Department | Total | Completed | Completion % | Avg Turnaround (h)
 |       Header row repeated on each page; zebra-striped body rows
 |       Totals row (sum of Total/Completed, weighted completion %)
 |     Empty state: "No tasks were created in the selected window."
 +-- Footer
       "Page {current} of {total}"  (centered)
```

#### 8.3 Response Pipeline

```text
 GET /api/reports/department-performance/pdf?from&to       [Authorize(Roles = "Admin")]
   -> IReportService.GetDepartmentPerformanceAsync(from, to)   (cached, §12.2; same rules as JSON endpoint)
   -> build DepartmentPerformanceReportModel
   -> QuestPdfReportGenerator.GenerateDepartmentPerformance(model)   (CPU-bound, synchronous)
   -> File(bytes, "application/pdf", "department-performance_{from}_{to}.pdf")
```

QuestPDF writes synchronously, and Kestrel disallows synchronous I/O on the response body. The document is therefore rendered into memory and returned as a file result. That is fine here: the report has one row per department and stays in the tens of kilobytes. Glyph rendering must be checked inside the Linux runtime image during Milestone 6; if fonts are missing, install a font package in the runtime stage.

---

### 9. Billing Subsystem (Milestone 7)

#### 9.1 Contract

```csharp
public interface IPaymentGatewayService
{
    Task<CheckoutSessionResult> CreateCheckoutSessionAsync(
        CreateCheckoutSessionRequest request, string idempotencyKey, CancellationToken cancellationToken = default);

    Task<CheckoutSessionResult> GetCheckoutSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    /// Verifies the Stripe-Signature header against the raw body and maps the event.
    /// Throws WebhookSignatureException (missing/malformed header -> 400, mismatch/stale -> 401).
    PaymentWebhookEvent ParseWebhookEvent(string rawBody, string? signatureHeader);
}

public record CreateCheckoutSessionRequest(Guid TaskId, Guid TaskBillingId, string ProductName, long AmountCents, string Currency);
public record CheckoutSessionResult(string SessionId, string Url, string Status, string? PaymentIntentId, DateTime ExpiresAtUtc);
public record PaymentWebhookEvent(string EventId, string Type, string? SessionId, string? PaymentIntentId, string? PaymentStatus, Guid? TaskBillingId);
```

Stripe SDK types never leave `StripePaymentGatewayService`. `BillingService` works only with the records above, which keeps it unit-testable with Moq.

#### 9.2 Stripe.net Integration

- `IStripeClient` is registered as a singleton: `new StripeClient(apiKey, httpClient: new SystemNetHttpClient(factory.CreateClient("Stripe"), maxNetworkRetries: 2))`. Stripe.net's built-in retries reuse the idempotency key, so the Polly pipeline on the `"Stripe"` client applies only timeouts and the circuit breaker, not retries. This avoids multiplying retries.
- `SessionService.CreateAsync` is called with `Mode = "payment"`, a single `PriceData` line item, `ClientReferenceId = TaskBillingId`, `Metadata = { taskId, taskBillingId }`, `PaymentIntentData.Metadata` (same values), `SuccessUrl` / `CancelUrl` from config, and `RequestOptions { IdempotencyKey = "checkout:{TaskBillingId}:{Idempotency-Key}" }`.
- Sandbox: `Stripe:SecretKey` is a `sk_test_...` key in every non-production environment. Startup refuses to boot if `ASPNETCORE_ENVIRONMENT != Production` and the key starts with `sk_live_`. Local webhook testing uses `stripe listen --forward-to localhost:5000/api/webhooks/stripe`.

#### 9.3 BillingService State Machine

```text
                 checkout created                 checkout.session.completed (paid)
   +--------+  ------------------->  +------------+  / async_payment_succeeded   +------+
   | Unpaid |                        | Processing |  ------------------------->  | Paid |  (terminal)
   +--------+  <-------------------  +------------+                              +------+
                checkout.session.expired   |   ^
                                           |   |  new checkout created
            async_payment_failed /         v   |
            payment_intent.payment_failed +--------+
                                          | Failed |
                                          +--------+
```

Each webhook-driven transition runs as follows:

1. Load `TaskBilling` by `StripeSessionId`. Events for unknown or stale sessions are ignored.
2. Check whether the transition is allowed from the current status. If not, treat the event as a replay and do nothing.
3. Apply the change and call `SaveChangesAsync`. The `xmin` concurrency token detects a race. On `DbUpdateConcurrencyException`, reload and re-evaluate once.

---

### 10. Webhook Subsystem (Milestone 8)

#### 10.1 Inbound: Stripe Handler

```text
 POST /api/webhooks/stripe     [AllowAnonymous]  [DisableRequestSizeLimit -> capped at 256 KB]
   1. Read the raw body:  Request.EnableBuffering(); using var reader = new StreamReader(Request.Body, UTF8);
                          var raw = await reader.ReadToEndAsync(ct);
      (the action takes no [FromBody] parameter, so model binding never consumes or reformats the body)
   2. IPaymentGatewayService.ParseWebhookEvent(raw, Request.Headers["Stripe-Signature"])
        -> EventUtility.ConstructEvent(raw, header, Stripe:WebhookSecret, tolerance: 300,
                                       throwOnApiVersionMismatch: false)
        missing/malformed header  -> 400   (BR-08; nothing else executed)
        signature/timestamp fail  -> 401   (BR-08; nothing else executed)
   3. BillingService.HandleWebhookEventAsync(evt)   -> state machine (§9.3)
   4. 200 OK  (also for ignored and unhandled event types)
      unexpected exception -> 500, so Stripe retries with its own backoff
```

The endpoint is excluded from JWT authentication. `Stripe:WebhookSecret` (`whsec_...`) is a different secret from the API key.

#### 10.2 Outbound: Subscription Registry & Dispatcher

```csharp
public interface IWebhookDispatcher
{
    /// Adds one WebhookDeliveryLog per matching active subscription to the current DbContext.
    /// Does NOT call SaveChanges: rows commit atomically with the caller's task mutation (outbox).
    Task EnqueueAsync(string eventType, object data, CancellationToken cancellationToken = default);
}
```

- `TaskService` calls `EnqueueAsync` before `SaveChangesAsync`:
  - create emits `task.created`;
  - reassign emits `task.reassigned`;
  - a status change emits `task.status_changed`, plus `task.completed` when the new status is `Completed`.
- Envelope (stored in `Payload`): `{ "id": "<event guid>", "type": "...", "occurredAtUtc": "...", "data": { ... } }`. The event `id` is shared by every delivery of the same event.
- SSRF protection:
  - At registration, the URL must be `https`, must not be an IP literal in loopback, link-local, or private ranges, and must not be `localhost`.
  - At delivery, the `"OutboundWebhooks"` handler's `SocketsHttpHandler.ConnectCallback` rejects resolved private addresses. This defends against DNS rebinding.
  - Redirects are disabled (`AllowAutoRedirect = false`).

#### 10.3 WebhookDeliveryWorker (Background Outbox Relay)

```text
 every 10 s (BackgroundService, scoped AppDbContext per cycle):
   1. Claim up to 50 due rows:
        SELECT ... FROM "WebhookDeliveryLogs"
        WHERE "IsSuccess" = FALSE AND "AttemptCount" < 6
          AND "TimestampUtc" + backoff("AttemptCount") <= now()
        ORDER BY "TimestampUtc" LIMIT 50
        FOR UPDATE SKIP LOCKED              -- safe with multiple API replicas
      backoff(0)=0, (1)=30s, (2)=2m, (3)=10m, (4)=1h, (5)=6h
   2. Skip rows whose subscription is now inactive (no attempt consumed).
   3. For each row: unprotect secret; t = unix now;
        sig = hex(HMACSHA256(secret, $"{t}.{Payload}"))
        POST Payload  with  X-ETMS-Event, X-ETMS-Delivery: {Id}, X-ETMS-Signature: t={t},v1={sig}
   4. Update: AttemptCount++, StatusCode, IsSuccess = 2xx, TimestampUtc = now
        BrokenCircuitException -> leave the row unchanged (no attempt consumed), retry next cycle
        AttemptCount reaches 6 without success -> log warning (dead letter)
   5. SaveChangesAsync, commit
```

Delivery is at-least-once. Receivers verify the signature, reject timestamps older than 5 minutes, and deduplicate on `X-ETMS-Delivery`.

---

### 11. Configuration & Deployment Additions

| Key | Example | Notes |
| :--- | :--- | :--- |
| `Storage:RootPath` | `/app/storage` | Mounted named volume `etms_attachments` |
| `Storage:MaxFileSizeBytes` | `10485760` | BR-06 |
| `Stripe:SecretKey` | `sk_test_...` | Secret; env `Stripe__SecretKey` |
| `Stripe:WebhookSecret` | `whsec_...` | Secret; env `Stripe__WebhookSecret` |
| `Stripe:SuccessUrl` / `Stripe:CancelUrl` | `https://app.example.com/billing/{CHECKOUT_SESSION_ID}` | Client-facing return URLs |
| `Stripe:DefaultCurrency` | `usd` | FR-PAY-01 |
| `Webhooks:MaxAttempts` | `6` | FR-HOOK-04 |
| `Webhooks:PollIntervalSeconds` | `10` | §10.3 |
| `ConnectionStrings:Redis` | `redis:6379` | §12.2 |
| `Cache:AnalyticsTtlSeconds` | `300` | FR-RESIL-02 |

`docker-compose.yml` (Milestone 9) adds:

- a `redis` service (`redis:7-alpine`, healthcheck `redis-cli ping`), with the `api` service depending on it (`service_healthy`);
- the `etms_attachments` volume mounted at `/app/storage`;
- a Data Protection key-ring volume, so encrypted webhook secrets stay readable across container restarts.

| NuGet Package | Purpose |
| :--- | :--- |
| `QuestPDF` | PDF generation |
| `Stripe.net` | Stripe API + webhook signature verification |
| `Microsoft.Extensions.Http.Resilience` (8.x) | Polly v8 pipelines for `HttpClient` |
| `Microsoft.Extensions.Caching.StackExchangeRedis` (8.x) | `IDistributedCache` on Redis |

---

### 12. Resilience & Cache Layer (Milestone 9)

#### 12.1 HTTP Resilience Pipelines (Polly v8)

```csharp
// Outbound webhooks: full standard pipeline, one circuit per receiver host.
services.AddHttpClient("OutboundWebhooks", c => c.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectCallback = SsrfGuard.ConnectAsync           // §10.2
    })
    .AddStandardResilienceHandler(o =>
    {
        o.Retry.MaxRetryAttempts = 2;                      // inner retries; worker backoff is the outer loop
        o.Retry.BackoffType = DelayBackoffType.Exponential;
        o.Retry.UseJitter = true;
        o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
        o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
        o.CircuitBreaker.FailureRatio = 0.5;
        o.CircuitBreaker.MinimumThroughput = 10;
        o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
        o.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);
    })
    .SelectPipelineByAuthority();

// Stripe: Stripe.net already retries with idempotency keys -> timeouts + circuit breaker only.
services.AddHttpClient("Stripe")
    .AddResilienceHandler("stripe", b => b
        .AddTimeout(TimeSpan.FromSeconds(60))              // total
        .AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
        {
            FailureRatio = 0.5, MinimumThroughput = 10,
            SamplingDuration = TimeSpan.FromSeconds(30), BreakDuration = TimeSpan.FromSeconds(30)
        })
        .AddTimeout(TimeSpan.FromSeconds(20)));            // per attempt
```

| Failure | Outcome |
| :--- | :--- |
| Stripe circuit open (`BrokenCircuitException`) | Checkout returns `503` + `Retry-After: 30`; billing status unchanged |
| Stripe API error / timeout after retries | Checkout returns `502 Bad Gateway`; billing status unchanged |
| Webhook receiver circuit open | Delivery deferred, attempt not consumed (§10.3) |

#### 12.2 Distributed Cache (Redis)

- Registration: `services.AddStackExchangeRedisCache(o => { o.Configuration = cfg.GetConnectionString("Redis"); o.InstanceName = "etms:"; })`. Integration tests replace it with `AddDistributedMemoryCache()`.
- `CachedReportService` decorates `IReportService` (cache-aside). Key format:
  `reports:{generation}:{endpoint}:{scope}:{paramsHash}`, where `scope` = `all` (Admin) or `mgr:{userId}`.
- `generation` is read from key `reports:gen`, which has no expiry. After any committed task change (create, reassign, or status update), `TaskService` writes a new GUID to that key. Older keys are then never read again and expire through their TTL.
- Entries are JSON-serialized DTOs with `AbsoluteExpirationRelativeToNow = Cache:AnalyticsTtlSeconds`.
- Fail-open: every cache call is wrapped in `try/catch (RedisException or TimeoutException)` with a 250 ms `syncTimeout`. On error, the service logs a warning and reads from PostgreSQL.

---

### 13. Testing Strategy Additions

- **Unit:**
  - BR-06 validator (size, extension, MIME, and signature-byte mismatch cases);
  - every allowed and disallowed transition of the billing state machine;
  - BR-07 (not billable, already `Paid`);
  - webhook replay and stale-session handling;
  - HMAC signing helper against known vectors;
  - cache key scoping and generation bumping.
- **Integration (`WebApplicationFactory`):**
  - multipart upload and download round-trip, including `Range` requests;
  - PDF endpoint returns `%PDF` bytes with `application/pdf`;
  - Stripe webhook with a locally computed valid signature returns 200, and tampered body, missing header, or stale timestamp return 400/401 with no DB change (BR-08);
  - `IPaymentGatewayService` mocked for checkout;
  - outbox rows are committed atomically with task changes.

---