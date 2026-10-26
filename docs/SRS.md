# Software Requirements Specification (SRS)
## Project: Employee Task Management System (ETMS)
**Standard:** IEEE 830 / ISO-IEC 25010 Compliant Specification  
**Version:** 1.1.0  

| Version | Scope |
| :--- | :--- |
| 1.0.0 | Milestones 1–5: Auth, Org, Tasks, Notifications, Analytics (BR-01 to BR-05). |
| 1.1.0 | Milestones 6–9: Documents & PDF, Payments & Billing, Webhooks, Resilience & Caching (BR-06 to BR-08). |

---

### 1. Introduction
This Software Requirements Specification defines the complete behavioral, operational, and non-functional requirements for the ETMS Web API backend. 

---

### 2. System Actors & Role-Based Access Matrix

| Functional Capability | `Admin` | `Manager` | `Employee` |
| :--- | :---: | :---: | :---: |
| Authenticate & Refresh Session Tokens | Yes | Yes | Yes |
| Create, Update, Delete Departments | **Yes** | No | No |
| Create, Update, Deactivate Employees | **Yes** | No | No |
| Assign/Reassign Line Manager to Employee | **Yes** | No | No |
| Create Tasks for Direct Reports | No | **Yes** | No |
| Reassign Tasks (Active Only, Direct Reports) | No | **Yes** | No |
| Update Status of Assigned Task | No | No | **Yes** |
| Post Discussion Comments to Task | Yes | Yes | **Yes** |
| View System-Wide Performance Dashboard | **Yes** | No | No |
| View Team Workload Analytics | No | **Yes** | No |
| View Self Assigned Task Queue | No | No | **Yes** |
| Upload Task Attachments (Assignee, Creator, or Admin) | Yes | Yes | Yes |
| Download Task Attachments (within task visibility scope) | Yes | Yes | Yes |
| Export Department Performance PDF | **Yes** | No | No |
| Configure Task Billing & Start Stripe Checkout | Yes | **Yes** (tasks they created) | No |
| View Task Billing Status | Yes | **Yes** (tasks they created) | No |
| Manage Outbound Webhook Subscriptions | **Yes** | No | No |

---

### 3. Functional Requirements (FR)

#### Module 1: Authentication & Token Management (AUTH)
- **FR-AUTH-01:** System must validate credentials (`Email`, `Password`) using secure salted hashing (PBKDF2/Argon2/BCrypt via ASP.NET Identity Core).
- **FR-AUTH-02:** Upon successful authentication, system issues a short-lived JSON Web Token (JWT) with a maximum lifetime of 15 minutes.
- **FR-AUTH-03:** System issues a cryptographically random, persistent Refresh Token with a 7-day lifetime.
- **FR-AUTH-04:** Token rotation is mandatory: requesting a new access token via `/api/auth/refresh` immediately invalidates the consumed refresh token and issues a replacement pair.
- **FR-AUTH-05:** Logging out or revoking credentials immediately revokes active refresh tokens.

#### Module 2: Organizational Structure & Staff Management (ORG)
- **FR-ORG-01:** System allows `Admin` to manage departments (`Id`, `Name`, `CreatedAt`).
- **FR-ORG-02:** System allows `Admin` to create user profiles containing `FullName`, `Email`, `Role`, `DepartmentId`, and `ManagerId`.
- **FR-ORG-03:** Every user with role `Employee` must have a valid `ManagerId` pointing to an existing `Manager` record.
- **FR-ORG-04:** Employee list queries must support indexed search by name/email, filtering by department/role, and deterministic pagination (`PageNumber`, `PageSize`).

#### Module 3: Task Lifecycle Management (TASK)
- **FR-TASK-01:** A Task entity comprises: `Id`, `Title`, `Description`, `Priority` (`Low`, `Medium`, `High`), `Status` (`Pending`, `InProgress`, `InReview`, `Completed`), `DueDate`, `CreatedByUserId`, `AssigneeId`, `CreatedAt`, `UpdatedAt`.
- **FR-TASK-02:** Task status transitions must follow a strict, non-reversible directional pipeline:
  $$\text{Pending} \longrightarrow \text{InProgress} \longrightarrow \text{InReview} \longrightarrow \text{Completed}$$
- **FR-TASK-03:** Any user authorized on a task may append a `Comment` (`Id`, `TaskId`, `UserId`, `CommentText`, `CreatedAt`).
- **FR-TASK-04:** The system must record an immutable `AuditLog` entry on every task mutation capturing: `TaskId`, `ChangedByUserId`, `PropertyName`, `OldValue`, `NewValue`, and `TimestampUtc`.

#### Module 4: Notification & Background Scheduling (NOTIF)
- **FR-NOTIF-01:** System dispatches an asynchronous email notification to the assignee upon initial task creation or reassignment.
- **FR-NOTIF-02:** System executes a background daemon (`BackgroundService`) configured to run on a scheduled interval (e.g., hourly).
- **FR-NOTIF-03:** The daemon queries for active tasks whose `DueDate` falls within $[T+23\text{h}, T+24\text{h}]$ and dispatches a 24-hour reminder email to the assignee (flagged to prevent duplicate dispatches).
- **FR-NOTIF-04:** The daemon queries for active tasks where `DueDate` $< \text{CurrentUtcTime}$ and `Status` $\neq \text{Completed}$, transitions their internal evaluation flag to overdue, and dispatches an escalation notification to the assigning manager.

#### Module 5: Analytics & Aggregations (DASH)
- **FR-DASH-01:** System provides an endpoint returning global and scoped totals: `TotalTasks`, `PendingTasks`, `InProgressTasks`, `InReviewTasks`, `CompletedTasks`, and `OverdueTasks`.
- **FR-DASH-02:** System calculates employee workload distributions (number of currently active tasks per employee).
- **FR-DASH-03:** System aggregates completion velocity per department within a requested date window (`FromDate`, `ToDate`).

#### Module 6: Document Processing & PDF Reporting (DOC)
- **FR-DOC-01 (Task Attachments):** The task assignee, the task creator, or an `Admin` may upload a file to a task as `multipart/form-data`. The system writes the file through `IStorageService` under a key it generates itself (the client filename is never part of the storage path) and records a `TaskAttachment` (`Id`, `TaskId`, `FileName`, `StoragePath`, `ContentType`, `FileSizeBytes`, `UploadedByUserId`, `UploadedAtUtc`). Uploads must satisfy **BR-06**.
- **FR-DOC-02 (Secure Download Streaming):** Any user who can view the task (same scope as `GET /api/tasks/{id}`) may download its attachments. The file is streamed from storage, not loaded fully into memory, with `Content-Disposition: attachment` (sanitized filename), the stored `ContentType`, `X-Content-Type-Options: nosniff`, and HTTP range support. An attachment that does not belong to the task in the route returns `404 Not Found`.
- **FR-DOC-03 (Department Performance PDF):** `Admin` may export the department performance report (same dataset and date-window rules as FR-DASH-03) as a PDF rendered on the server with QuestPDF. The document has a header (report title, date window, generation timestamp, requesting user), one table row per department (`TotalTasks`, `CompletedTasks`, `CompletionRate %`, `AverageTurnaroundHours`), a totals row, and a `Page X of Y` footer. It is returned as an `application/pdf` attachment.

#### Module 7: Payments & Billing (PAY)
- **FR-PAY-01 (Billable Task Setup):** An `Admin`, or the `Manager` who created the task, may mark a task as billable by setting `AmountCents` (integer minor units, 50–99,999,999) and `Currency` (ISO 4217, lowercase, default `usd`). This creates the task's single `TaskBilling` record with `BillingStatus = Unpaid`. The amount and currency can be changed only while the status is `Unpaid` or `Failed`.
- **FR-PAY-02 (Checkout Session Creation):** An authorized user may start checkout for a billable task. The system creates a Stripe Checkout Session (`mode = payment`) through `IPaymentGatewayService` with one line item (task title, amount, currency), metadata `{ taskId, taskBillingId }`, and success/cancel URLs from configuration. It stores `StripeSessionId`, sets `BillingStatus = Processing`, and returns the hosted checkout URL. Non-production environments must use Stripe test-mode (sandbox) keys.
- **FR-PAY-03 (Payment State Machine):** `BillingStatus` must follow these transitions only:

  | From | To | Trigger |
  | :--- | :--- | :--- |
  | `Unpaid` | `Processing` | Checkout session created (FR-PAY-02) |
  | `Failed` | `Processing` | New checkout session created after a failed payment |
  | `Processing` | `Paid` | Verified `checkout.session.completed` with `payment_status = paid`, `checkout.session.async_payment_succeeded`, or `payment_intent.succeeded`. Sets `PaidAtUtc` and `StripePaymentIntentId`. |
  | `Failed` | `Paid` | Verified success event for the billing's **current** session (`TaskBilling.StripeSessionId`). A declined card moves the billing to `Failed`, but the payer can retry on the same Checkout page; the later success must still be recorded because Stripe has taken the payment. |
  | `Processing` | `Failed` | Verified `checkout.session.async_payment_failed` or `payment_intent.payment_failed` |
  | `Processing` | `Unpaid` | Verified `checkout.session.expired` |

  `Paid` is terminal. Transitions out of `Processing`, and `Failed` → `Paid`, happen **only** through verified Stripe webhook events (FR-HOOK-01), never through client calls.
- **FR-PAY-04 (Idempotency):**
  - The checkout endpoint requires an `Idempotency-Key` request header (UUID). The system sends `checkout:{TaskBillingId}:{Idempotency-Key}` to Stripe as the request idempotency key, so a retried call with the same key never creates a second session.
  - If the billing is already `Processing` and its Stripe session is still open, the system returns that session instead of creating a new one. If Stripe reports the session as expired, the system first moves the billing to `Unpaid`, then creates a new session.
  - Concurrent checkout requests for the same `TaskBilling` are serialized by an optimistic concurrency token. The losing request receives `409 Conflict`.
  - Webhook handling is idempotent: a transition is applied only when the current status allows it, a replayed event that was already applied is acknowledged with `200 OK` and has no side effects, and events whose session ID does not match `TaskBilling.StripeSessionId` (stale sessions) are ignored.

#### Module 8: Inbound & Outbound Webhook Engine (HOOK)
- **FR-HOOK-01 (Inbound Stripe Webhooks):** `POST /api/webhooks/stripe` accepts anonymous requests. It reads the raw request body byte-for-byte and verifies the `Stripe-Signature` header (HMAC-SHA256, `v1` scheme, 300-second timestamp tolerance) against the configured endpoint signing secret **before** deserializing the event or touching the database (**BR-08**). Verified events are routed to the billing state machine (FR-PAY-03). Event types the system does not handle are acknowledged with `200 OK` and ignored.
- **FR-HOOK-02 (Outbound Subscription Registry):** `Admin` may register, list, and remove outbound webhook subscriptions (`Url`, `EventType`). The URL must be an absolute `https` URL that does not point to a loopback, link-local, or private address. The system generates a 32-byte random signing secret, returns it **once** in the creation response, and stores it encrypted at rest. Lists show only a masked hint of the secret. Removing a subscription sets `IsActive = false` and keeps its delivery history.
- **FR-HOOK-03 (Event Emission):** For the task lifecycle events `task.created`, `task.reassigned`, `task.status_changed`, and `task.completed`, the system creates one `WebhookDeliveryLog` entry for every active subscription whose `EventType` matches the event or is `*`. These entries are written in the same database transaction as the task change (transactional outbox). The payload is a JSON envelope `{ id, type, occurredAtUtc, data }`, saved at enqueue time so every retry sends the same body.
- **FR-HOOK-04 (Retryable Dispatch):** A background worker sends pending deliveries as signed `POST` requests (`X-ETMS-Signature: t={unix},v1={hex HMAC-SHA256(secret, "{t}.{body}")}`, plus `X-ETMS-Event` and `X-ETMS-Delivery`). A `2xx` response counts as success. Any other status, a timeout, or a network error is a failed attempt, retried with exponential backoff (30 s, 2 min, 10 min, 1 h, 6 h) up to **6 total attempts**. After that the delivery is dead-lettered (`IsSuccess = false`, `AttemptCount = 6`) and a warning is logged. Every attempt updates `StatusCode`, `IsSuccess`, `AttemptCount`, and `TimestampUtc`. Delivery is at-least-once, so receivers deduplicate on `X-ETMS-Delivery`.

#### Module 9: Enterprise Resilience & Distributed Caching (RESIL)
- **FR-RESIL-01 (HTTP Client Resilience):** Every outbound HTTP client (Stripe API, outbound webhooks) is registered through `IHttpClientFactory` with a `Microsoft.Extensions.Http.Resilience` (Polly v8) pipeline that includes:
  - retry with exponential backoff and jitter on transient failures (`HttpRequestException`, `408`, `429` honoring `Retry-After`, `5xx`);
  - a per-attempt timeout and a total request timeout;
  - a circuit breaker that opens at a ≥ 50% failure ratio over a 30-second window (minimum 10 requests) and stays open for 30 seconds.

  Non-idempotent requests are retried only when they carry an idempotency key. The webhook client keeps a separate circuit per destination host, so one failing receiver does not block the others. While a circuit is open, calls fail fast: checkout returns `503 Service Unavailable` with `Retry-After`, and webhook deliveries are postponed without using up an attempt.
- **FR-RESIL-02 (Distributed Analytics Cache):** Results of `GET /api/reports/summary`, `/workload`, and `/department-performance` (including the PDF dataset) are cached through `IDistributedCache` backed by Redis, using cache-aside:
  - Keys include the endpoint, the caller's data scope (system-wide or manager ID), the query parameters, and a cache-generation token.
  - Entries expire after an absolute 5 minutes. Any committed task change bumps the generation token, which invalidates all cached analytics.
  - Redis is **fail-open**: if it is unavailable or a cache operation errors, the request falls through to PostgreSQL and a warning is logged. The request itself never fails.

---

### 4. Strict Business Rules (BR)

Any API request violating these rules must be rejected immediately with an HTTP 400/409/422 status code (or the rule-specific code stated below), accompanied by a structured `RFC 7807 ProblemDetails` error response.

* **BR-01 (Active Task Limit):** An employee cannot possess more than **5 active tasks** simultaneously. A task is defined as "active" if its status is `Pending`, `InProgress`, or `InReview`. An assignment attempt exceeding 5 must return `409 Conflict`.
* **BR-02 (Terminal Status Immutability):** A task marked as `Completed` is immutable regarding assignment and status. It **cannot be reassigned**, and its status cannot be changed back to any earlier state.
* **BR-03 (Due Date Feasibility):** A task’s `DueDate` cannot be set in the past relative to UTC server time at the moment of creation or modification.
* **BR-04 (Self-Service Boundary):** An employee can only update the status of tasks where `AssigneeId == CurrentAuthenticatedUserId`. Attempting to modify other employees' tasks must return `403 Forbidden`.
* **BR-05 (Managerial Domain Boundary):** A `Manager` can only assign or reassign tasks to employees who have `ManagerId == CurrentAuthenticatedUserId`. Assigning to an employee outside their team must return `403 Forbidden`.
* **BR-06 (Upload Safety):** A file upload must not exceed **10 MB** (10,485,760 bytes) and must be one of these safe types: `.pdf` (`application/pdf`), `.png` (`image/png`), `.jpg`/`.jpeg` (`image/jpeg`), `.docx` (`application/vnd.openxmlformats-officedocument.wordprocessingml.document`). The extension, the declared `Content-Type`, and the file's leading signature bytes must all agree. Oversized files return `413 Payload Too Large`. Disallowed or mismatched types return `415 Unsupported Media Type`. Empty files return `400 Bad Request`.
* **BR-07 (Billing Eligibility):** A checkout session can be started only for a task that has a `TaskBilling` record in `Unpaid`, `Failed`, or `Processing` status (FR-PAY-04 describes the `Processing` case). A task that is not billable, or whose billing is already `Paid`, must be rejected with `409 Conflict`. A paid task can never be billed again.
* **BR-08 (Inbound Webhook Authenticity):** An inbound webhook without a valid HMAC signature must be rejected **before any processing**: no deserialization, no database access, no side effects. A missing or malformed `Stripe-Signature` header returns `400 Bad Request`. A signature mismatch or a timestamp outside the 300-second tolerance returns `401 Unauthorized`.

---

### 5. Non-Functional Requirements (NFR)

#### 5.1 Security
- All incoming HTTP traffic must be rejected or redirected to HTTPS.
- Passwords must be hashed using industry-standard key derivation functions (minimum 100,000 PBKDF2 iterations or Argon2id).
- Endpoints must validate model schemas before controller execution; invalid payloads must return structured `400 Bad Request` payloads.
- Role checks must execute via declarative authorization attributes (`[Authorize(Roles = "...")]`).
- Secrets (Stripe API key, Stripe webhook signing secret, outbound webhook secrets) must never be committed to source control or written to logs. They are supplied through environment variables or a secret store. Outbound webhook secrets are encrypted at rest with ASP.NET Core Data Protection.
- Attachment storage paths are always generated by the server and resolved under the configured storage root. Any path that resolves outside that root must be rejected (path-traversal guard).
- HMAC signatures must be compared in constant time (`CryptographicOperations.FixedTimeEquals` or the Stripe.net SDK helper).

#### 5.2 Performance & Scalability
- Read endpoints returning lists must mandate pagination parameters with a hard upper limit of `PageSize = 100`.
- Database access must use compiled, parameterized queries via Entity Framework Core to eliminate SQL injection.
- Average API response time must remain under **200 milliseconds** for standard CRUD operations at 200 concurrent requests, and under **2 seconds** for complex analytical aggregations.

#### 5.3 Reliability & Fault Tolerance
- All unexpected exceptions must be intercepted by a centralized global exception middleware returning sanitized `500 Internal Server Error` responses with zero stack trace leakage in production.
- Database writes involving multi-table operations (e.g., updating a task and writing an audit log) must execute within an atomic transaction.
- Outbound webhook events must be written in the same transaction as the task change that produced them, so a crash never loses an event that was committed and never emits an event that was not.
- Failure of an optional dependency (Redis, a webhook receiver) must not fail a core task or reporting request. Failure of Stripe affects only billing endpoints, which return `502 Bad Gateway` or `503 Service Unavailable`.

#### 5.4 Maintainability & Extensibility
- Architecture must follow standard layered separation: `Controllers` $\to$ `Services` (Business Logic) $\to$ `Data/Repositories` (EF Core).
- All business rule logic must be covered by automated unit tests with minimum 80% line coverage.