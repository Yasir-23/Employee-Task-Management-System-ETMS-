# REST API Specification & Endpoint Contracts
## Project: Employee Task Management System (ETMS)
**Base URL:** `/api`  
**Authentication Scheme:** `Bearer <JWT_TOKEN>` via `Authorization` header  
**Standard Response Format:** `application/json`  
**Error Standard:** `RFC 7807 ProblemDetails` (`application/problem+json`)  

---

### 1. Authentication Endpoints (`/api/auth`)

#### 1.1 User Login
* **Method:** `POST /api/auth/login`
* **Access:** Anonymous / Public
* **Request Body:**
```json
{
  "email": "manager.sarah@company.com",
  "password": "SecurePassword123!"
}
```
Success Response (200 OK):JSON{
  "accessToken": "eyJhbGciOi...",
  "refreshToken": "d8f411b2...",
  "expiresAt": "2026-10-23T12:15:00Z",
  "user": {
    "id": "c1f7a0b5-7798-4c6e-8263-ff39c366e6b4",
    "fullName": "Sarah Jenkins",
    "email": "manager.sarah@company.com",
    "role": "Manager"
  }
}
Error Responses: 400 Bad Request (Validation), 401 Unauthorized (Invalid credentials).1.2 Refresh Access TokenMethod: POST /api/auth/refreshAccess: Anonymous / PublicRequest Body:JSON{
  "refreshToken": "d8f411b2..."
}
Success Response (200 OK): Returns new accessToken and rotated refreshToken pair.Error Responses: 401 Unauthorized (Expired, revoked, or non-existent token).2. Organizational Endpoints (/api/departments & /api/employees)2.1 Create DepartmentMethod: POST /api/departmentsAccess: Admin onlyRequest Body:JSON{
  "name": "Software Engineering"
}
Success Response (201 Created): Returns created Department object.2.2 List DepartmentsMethod: GET /api/departmentsAccess: Authenticated (Admin, Manager, Employee)Success Response (200 OK): Returns array of Department objects.2.3 Create Employee / UserMethod: POST /api/employeesAccess: Admin onlyRequest Body:JSON{
  "fullName": "Alex Mercer",
  "email": "alex.mercer@company.com",
  "password": "InitialPassword123!",
  "role": "Employee",
  "departmentId": "a90df214-9982-4217-bc73-d58ef8a1cb01",
  "managerId": "c1f7a0b5-7798-4c6e-8263-ff39c366e6b4"
}
Success Response (201 Created): Returns user representation (omitting password hash).Error Responses: 400 Bad Request (Email already exists or invalid manager assignment).2.4 List Employees (Paginated & Filtered)Method: GET /api/employees?pageNumber=1&pageSize=20&search=alex&departmentId={id}&role=EmployeeAccess: Admin, ManagerSuccess Response (200 OK):JSON{
  "items": [
    {
      "id": "e45ba012-7889-4d10-8022-de9081ffaa12",
      "fullName": "Alex Mercer",
      "email": "alex.mercer@company.com",
      "role": "Employee",
      "department": { "id": "...", "name": "Software Engineering" },
      "manager": { "id": "...", "fullName": "Sarah Jenkins" },
      "activeTasksCount": 3
    }
  ],
  "pageNumber": 1,
  "pageSize": 20,
  "totalCount": 1,
  "totalPages": 1
}
3. Task Management Endpoints (/api/tasks)3.1 Create and Assign TaskMethod: POST /api/tasksAccess: Manager onlyRequest Body:JSON{
  "title": "Build Payment Gateway Adapter",
  "description": "Implement Stripe checkout flow with webhook handlers.",
  "priority": "High",
  "dueDate": "2026-10-30T18:00:00Z",
  "assigneeId": "e45ba012-7889-4d10-8022-de9081ffaa12"
}
Validation & Rule Enforcements:BR-03: dueDate must be in the future. Returns 400 Bad Request if in the past.BR-05: assigneeId must be a direct report of the authenticated Manager. Returns 403 Forbidden if not.BR-01: Assignee must have $\le 4$ active tasks. Returns 409 Conflict if currently at 5.Success Response (201 Created): Returns Task object.3.2 List Tasks (Role-Scoped & Paginated)Method: GET /api/tasks?status=InProgress&priority=High&pageNumber=1&pageSize=20Access: AuthenticatedAdmin: Can view all tasks across the company.Manager: Can view tasks created by them or assigned to their direct reports.Employee: Can view only tasks assigned to them (AssigneeId == CurrentUser).Success Response (200 OK): Paginated array of task summaries.3.3 Get Task Details (with Comments and Audit History)Method: GET /api/tasks/{id}Access: Authenticated (Subject to ownership/management scope)Success Response (200 OK): Returns detailed Task entity including comments and audit log history.3.4 Update Task Status (State Machine Transition)Method: PATCH /api/tasks/{id}/statusAccess: Employee (Assignee only)Request Body:JSON{
  "newStatus": "InProgress"
}
Validation & Rule Enforcements:BR-04: Authenticated user must equal AssigneeId. Returns 403 Forbidden otherwise.BR-02: If current status is Completed, returns 409 Conflict (terminal immutability).State Machine: Transition must follow Pending $\to$ InProgress $\to$ InReview $\to$ Completed. Jumping steps or reversing returns 400 Bad Request.Success Response (200 OK): Returns updated task state.3.5 Reassign TaskMethod: PUT /api/tasks/{id}/reassignAccess: Manager (Original creator/line manager)Request Body:JSON{
  "newAssigneeId": "f9801a23-4122-491a-a001-c88f11ba45d0"
}
Validation & Rule Enforcements:BR-02: Returns 409 Conflict if task is already Completed.BR-05: Returns 403 Forbidden if new assignee is not in manager's team.BR-01: Returns 409 Conflict if new assignee already has 5 active tasks.Success Response (200 OK): Returns updated task representation.3.6 Add Comment to TaskMethod: POST /api/tasks/{id}/commentsAccess: Authenticated (Assignee, Creator, or Admin)Request Body:JSON{
  "content": "Staging webhook tested successfully. Ready for code review."
}
Success Response (201 Created): Returns created Comment record.4. Analytics & Reporting Endpoints (/api/reports)4.1 System & Team OverviewMethod: GET /api/reports/summaryAccess: Admin, ManagerSuccess Response (200 OK):JSON{
  "totalTasks": 142,
  "pendingTasks": 28,
  "inProgressTasks": 45,
  "inReviewTasks": 12,
  "completedTasks": 50,
  "overdueTasks": 7
}
4.2 Workload DistributionMethod: GET /api/reports/workloadAccess: Admin, ManagerSuccess Response (200 OK):JSON[
  {
    "employeeId": "e45ba012-7889-4d10-8022-de9081ffaa12",
    "employeeName": "Alex Mercer",
    "departmentName": "Software Engineering",
    "activeTasksCount": 3,
    "completedTasksCount": 18
  }
]
4.3 Department PerformanceMethod: GET /api/reports/department-performance?from=2026-10-01&to=2026-10-23Access: Admin onlySuccess Response (200 OK): Array of metrics grouped by department (Completion Rate %, Average Turnaround Hours).

---

> **Milestones 6–9.** The sections below add document, billing, and webhook contracts. All errors use `application/problem+json`. Example:
>
> ```json
> {
>   "type": "https://tools.ietf.org/html/rfc9110#section-15.5.16",
>   "title": "Unsupported Media Type",
>   "status": 415,
>   "detail": "BR-06: '.exe' is not an allowed attachment type. Allowed: .pdf, .png, .jpg, .jpeg, .docx.",
>   "instance": "/api/tasks/7d1c.../attachments"
> }
> ```
>
> **Analytics caching (FR-RESIL-02).** Responses from §4 and §6 may be served from Redis for up to 5 minutes and are invalidated on any task change. The contracts are unchanged.

---

### 5. Task Attachment Endpoints (`/api/tasks/{id}/attachments`)

#### 5.1 Upload Attachment
* **Method:** `POST /api/tasks/{id}/attachments`
* **Access:** Authenticated. Must be the task's assignee, the task's creator, or `Admin`.
* **Content-Type:** `multipart/form-data`
* **Path Parameters:** `id` (UUID): task ID.
* **Form Fields:**

| Field | Type | Required | Rules |
| :--- | :--- | :---: | :--- |
| `file` | binary | Yes | Exactly one file. 1 byte – 10 MB. `.pdf`, `.png`, `.jpg`, `.jpeg`, `.docx`. Extension, `Content-Type`, and signature bytes must agree (BR-06). |

* **Example Request:**
```http
POST /api/tasks/7d1c2e4a-1b7f-4f39-9c55-2a0e5d1f6b21/attachments
Authorization: Bearer eyJhbGciOi...
Content-Type: multipart/form-data; boundary=----etms

------etms
Content-Disposition: form-data; name="file"; filename="stripe-flow.pdf"
Content-Type: application/pdf

%PDF-1.7 ...
------etms--
```
* **Success Response (201 Created):** `Location: /api/tasks/{id}/attachments/{attachmentId}`
```json
{
  "id": "3b9f0c6e-5a27-4c8e-9d0b-7f4e2a1c9e55",
  "taskId": "7d1c2e4a-1b7f-4f39-9c55-2a0e5d1f6b21",
  "fileName": "stripe-flow.pdf",
  "contentType": "application/pdf",
  "fileSizeBytes": 248310,
  "uploadedBy": { "id": "e45ba012-7889-4d10-8022-de9081ffaa12", "fullName": "Alex Mercer" },
  "uploadedAtUtc": "2026-10-24T09:41:12Z",
  "downloadUrl": "/api/tasks/7d1c2e4a-1b7f-4f39-9c55-2a0e5d1f6b21/attachments/3b9f0c6e-5a27-4c8e-9d0b-7f4e2a1c9e55"
}
```
* **Error Responses:**

| Status | Condition |
| :--- | :--- |
| `400 Bad Request` | `file` field missing, more than one file, empty file, or invalid filename |
| `401 Unauthorized` | Missing/invalid JWT |
| `403 Forbidden` | Caller is not the assignee, the creator, or `Admin` |
| `404 Not Found` | Task does not exist |
| `413 Payload Too Large` | File exceeds 10 MB (BR-06) |
| `415 Unsupported Media Type` | Extension/MIME not allowed, or the declared type does not match the file signature (BR-06) |

> `GET /api/tasks/{id}` (§3.3) additionally returns an `attachments` array of the objects shown above.

#### 5.2 Download Attachment
* **Method:** `GET /api/tasks/{id}/attachments/{attachmentId}`
* **Access:** Authenticated, within the task's visibility scope (same rules as §3.2 / §3.3).
* **Path Parameters:** `id` (UUID): task ID. `attachmentId` (UUID): attachment ID; must belong to `id`.
* **Request Headers (optional):** `Range: bytes=0-1048575`
* **Success Response (200 OK, or 206 Partial Content for range requests):** binary file stream.
```http
HTTP/1.1 200 OK
Content-Type: application/pdf
Content-Length: 248310
Content-Disposition: attachment; filename="stripe-flow.pdf"; filename*=UTF-8''stripe-flow.pdf
X-Content-Type-Options: nosniff
Accept-Ranges: bytes
```
* **Error Responses:** `401 Unauthorized`; `403 Forbidden` (outside the task's visibility scope); `404 Not Found` (task or attachment not found, attachment belongs to a different task, or the stored file is missing); `416 Range Not Satisfiable` (invalid range).

---

### 6. PDF Reporting Endpoints (`/api/reports`)

#### 6.1 Export Department Performance as PDF
* **Method:** `GET /api/reports/department-performance/pdf?from=2026-10-01&to=2026-10-23`
* **Access:** `Admin` only
* **Query Parameters:**

| Name | Type | Required | Rules |
| :--- | :--- | :---: | :--- |
| `from` | date/date-time (UTC) | No | Defaults to 30 days before `to` |
| `to` | date/date-time (UTC) | No | Defaults to now; a date without a time includes the whole day; must be ≥ `from` |

* **Success Response (200 OK):** binary PDF (QuestPDF template, SDD §8.2).
```http
HTTP/1.1 200 OK
Content-Type: application/pdf
Content-Disposition: attachment; filename="department-performance_2026-10-01_2026-10-23.pdf"
```
* **Error Responses:** `400 Bad Request` (unparseable dates or `from` > `to`); `401 Unauthorized`; `403 Forbidden` (not `Admin`).

---

### 7. Billing Endpoints (`/api/tasks/{id}/billing`)

`billingStatus` values: `Unpaid`, `Processing`, `Paid`, `Failed` (state machine: SRS FR-PAY-03).

#### 7.1 Configure Billable Task
Defines or updates the billable amount (FR-PAY-01). A task must have a billing record before checkout can start.
* **Method:** `PUT /api/tasks/{id}/billing`
* **Access:** `Admin`, or the `Manager` who created the task
* **Request Body:**
```json
{
  "amountCents": 125000,
  "currency": "usd"
}
```

| Field | Type | Required | Rules |
| :--- | :--- | :---: | :--- |
| `amountCents` | integer | Yes | 50 – 99,999,999 |
| `currency` | string | No | 3-letter ISO 4217, lowercase; defaults to `usd` |

* **Success Response (201 Created on first setup, 200 OK on update):** a `TaskBillingDto` (see §7.3).
* **Error Responses:** `400 Bad Request` (validation); `401`; `403 Forbidden` (not `Admin` or the creating `Manager`); `404 Not Found` (task); `409 Conflict` (billing is `Processing` or `Paid`; the amount is locked).

#### 7.2 Create Stripe Checkout Session
* **Method:** `POST /api/tasks/{id}/billing/checkout`
* **Access:** `Admin`, or the `Manager` who created the task
* **Request Headers:** `Idempotency-Key: <uuid>` (**required**, FR-PAY-04)
* **Request Body:** none
* **Success Response (201 Created):** a new session was created and billing moved to `Processing`.
* **Success Response (200 OK):** billing was already `Processing` with an open session; the existing session is returned.
```json
{
  "taskId": "7d1c2e4a-1b7f-4f39-9c55-2a0e5d1f6b21",
  "taskBillingId": "b2c4e6a8-0f1e-4d3c-8b5a-6e7f8091a2b3",
  "stripeSessionId": "cs_test_a1B2c3D4e5F6g7H8",
  "checkoutUrl": "https://checkout.stripe.com/c/pay/cs_test_a1B2c3D4e5F6g7H8",
  "expiresAtUtc": "2026-10-25T09:41:12Z",
  "billingStatus": "Processing"
}
```
* **Error Responses:**

| Status | Condition |
| :--- | :--- |
| `400 Bad Request` | `Idempotency-Key` header missing or not a UUID |
| `401 Unauthorized` | Missing/invalid JWT |
| `403 Forbidden` | Caller is not `Admin` or the creating `Manager` |
| `404 Not Found` | Task does not exist |
| `409 Conflict` | BR-07: task is not billable, or billing is already `Paid`; or a concurrent checkout won the race |
| `502 Bad Gateway` | Stripe returned an error or timed out after retries |
| `503 Service Unavailable` | Stripe circuit breaker is open (`Retry-After` header set) |

#### 7.3 Get Billing Status
* **Method:** `GET /api/tasks/{id}/billing/status`
* **Access:** `Admin`, or the `Manager` who created the task
* **Success Response (200 OK):**
```json
{
  "taskId": "7d1c2e4a-1b7f-4f39-9c55-2a0e5d1f6b21",
  "taskBillingId": "b2c4e6a8-0f1e-4d3c-8b5a-6e7f8091a2b3",
  "amountCents": 125000,
  "currency": "usd",
  "billingStatus": "Paid",
  "stripeSessionId": "cs_test_a1B2c3D4e5F6g7H8",
  "stripePaymentIntentId": "pi_3QabCdEfGhIjKlMn",
  "paidAtUtc": "2026-10-24T09:52:40Z"
}
```
* **Error Responses:** `401`; `403 Forbidden`; `404 Not Found` (task does not exist, or the task is not billable).

---

### 8. Webhook Endpoints (`/api/webhooks`)

#### 8.1 Inbound Stripe Event Handler
* **Method:** `POST /api/webhooks/stripe`
* **Access:** Anonymous. Authenticated by HMAC signature only (BR-08).
* **Request Headers:** `Stripe-Signature: t=1792834332,v1=5257a869e7ec...` (required); `Content-Type: application/json`
* **Request Body:** raw Stripe `Event` JSON, read byte-for-byte. Max 256 KB.
```json
{
  "id": "evt_1QabCdEfGhIjKlMn",
  "type": "checkout.session.completed",
  "data": {
    "object": {
      "id": "cs_test_a1B2c3D4e5F6g7H8",
      "object": "checkout.session",
      "payment_status": "paid",
      "payment_intent": "pi_3QabCdEfGhIjKlMn",
      "client_reference_id": "b2c4e6a8-0f1e-4d3c-8b5a-6e7f8091a2b3",
      "metadata": { "taskId": "7d1c2e4a-...", "taskBillingId": "b2c4e6a8-..." }
    }
  }
}
```
* **Handled Event Types:**

| Stripe Event | Billing Transition |
| :--- | :--- |
| `checkout.session.completed` (`payment_status = paid`) | `Processing` → `Paid` |
| `checkout.session.async_payment_succeeded` | `Processing` → `Paid` |
| `checkout.session.async_payment_failed` | `Processing` → `Failed` |
| `payment_intent.payment_failed` | `Processing` → `Failed` |
| `checkout.session.expired` | `Processing` → `Unpaid` |
| `payment_intent.succeeded` | `Processing` / `Failed` → `Paid` (correlated through the `BillingId` metadata set at checkout) |
| *any other type* | ignored, `200 OK` |

* **Success Response (200 OK):** also returned for replays, stale sessions, and ignored types.
```json
{ "received": true }
```
* **Note:** a success event (`checkout.session.completed` with `payment_status = paid`, `checkout.session.async_payment_succeeded`, or `payment_intent.succeeded`) for the billing's current session also moves `Failed` → `Paid`, because the payer may retry after a declined card (SRS FR-PAY-03).
* **Error Responses:**

| Status | Condition |
| :--- | :--- |
| `400 Bad Request` | `Stripe-Signature` header missing or malformed; body is not valid JSON after verification (BR-08) |
| `401 Unauthorized` | Signature mismatch, or timestamp outside the 300-second tolerance (BR-08) |
| `413 Payload Too Large` | Body exceeds 256 KB |
| `500 Internal Server Error` | Unexpected processing failure; Stripe retries delivery |

#### 8.2 Register Outbound Webhook Subscription
* **Method:** `POST /api/webhooks/subscriptions`
* **Access:** `Admin` only
* **Request Body:**
```json
{
  "url": "https://hooks.partner.example.com/etms",
  "eventType": "task.status_changed"
}
```

| Field | Type | Required | Rules |
| :--- | :--- | :---: | :--- |
| `url` | string | Yes | Absolute `https` URL, ≤ 2048 chars; not localhost, a loopback, link-local, or private IP |
| `eventType` | string | Yes | `task.created`, `task.reassigned`, `task.status_changed`, `task.completed`, `task.overdue`, or `*` |

* **Success Response (201 Created):** `Location: /api/webhooks/subscriptions/{id}`. **`secretKey` is returned only in this response.**
```json
{
  "id": "5e8a1f3c-9b2d-4c7e-a6f0-1d3b5c7e9f02",
  "url": "https://hooks.partner.example.com/etms",
  "eventType": "task.status_changed",
  "isActive": true,
  "secretKey": "whsec_etms_8Kq2vN5xR1tY7mZ3pL9cW4hB6dF0sJ7f4a",
  "createdAtUtc": "2026-10-24T10:05:00Z"
}
```
* **Error Responses:** `400 Bad Request` (invalid URL, scheme, blocked host, or unknown `eventType`); `401`; `403 Forbidden` (not `Admin`); `409 Conflict` (an active subscription with the same `url` + `eventType` already exists).

#### 8.3 List Outbound Webhook Subscriptions
* **Method:** `GET /api/webhooks/subscriptions?isActive=true&pageNumber=1&pageSize=20`
* **Access:** `Admin` only
* **Query Parameters:** `isActive` (bool, optional); `pageNumber` (default 1); `pageSize` (default 20, max 100).
* **Success Response (200 OK):**
```json
{
  "items": [
    {
      "id": "5e8a1f3c-9b2d-4c7e-a6f0-1d3b5c7e9f02",
      "url": "https://hooks.partner.example.com/etms",
      "eventType": "task.status_changed",
      "isActive": true,
      "secretKeyHint": "••••7f4a",
      "createdAtUtc": "2026-10-24T10:05:00Z",
      "lastDelivery": { "isSuccess": true, "statusCode": 200, "attemptCount": 1, "timestampUtc": "2026-10-24T10:12:31Z" }
    }
  ],
  "pageNumber": 1,
  "pageSize": 20,
  "totalCount": 1,
  "totalPages": 1
}
```
* **Error Responses:** `400 Bad Request` (`pageSize` > 100); `401`; `403 Forbidden`.

#### 8.4 Remove Outbound Webhook Subscription
* **Method:** `DELETE /api/webhooks/subscriptions/{id}`
* **Access:** `Admin` only
* **Behavior:** Soft delete (`IsActive = false`). Pending deliveries are not attempted. Delivery history is kept. Idempotent.
* **Success Response:** `204 No Content` (also when the subscription is already inactive)
* **Error Responses:** `401`; `403 Forbidden`; `404 Not Found` (unknown ID).

#### 8.5 Outbound Delivery Contract (what receivers get)
* **Method:** `POST {subscription.url}`
* **Headers:**

| Header | Example | Purpose |
| :--- | :--- | :--- |
| `Content-Type` | `application/json` | |
| `X-ETMS-Event` | `task.status_changed` | Event type |
| `X-ETMS-Delivery` | `3f2b8c1e-6d4a-4e0b-9a7c-5b1d2e3f4a50` | Unique delivery ID (UUID); use it to deduplicate (at-least-once delivery) |
| `X-ETMS-Signature` | `t=1792836751,v1=9f2c...` | Format `t={timestamp},v1={hash}`: `timestamp` is Unix seconds at send time; `hash = hex(HMAC-SHA256(secretKey, "{timestamp}.{rawBody}"))`, lowercase |

* **Body (envelope):**
```json
{
  "id": "c0a8012e-7f3d-4b6a-9e1c-2d4f6a8b0c13",
  "type": "task.status_changed",
  "occurredAtUtc": "2026-10-24T10:12:30Z",
  "data": {
    "taskId": "7d1c2e4a-1b7f-4f39-9c55-2a0e5d1f6b21",
    "oldStatus": "InProgress",
    "newStatus": "InReview",
    "changedByUserId": "e45ba012-7889-4d10-8022-de9081ffaa12"
  }
}
```
* **`data` by event type:**

| Event | `data` fields |
| :--- | :--- |
| `task.created` | `taskId`, `title`, `priority`, `status`, `dueDateUtc`, `assigneeId`, `createdByUserId` |
| `task.reassigned` | `taskId`, `previousAssigneeId`, `newAssigneeId`, `changedByUserId` |
| `task.status_changed` | `taskId`, `oldStatus`, `newStatus`, `changedByUserId` |
| `task.completed` | `taskId`, `assigneeId`, `completedAtUtc` (sent in addition to `task.status_changed`) |
| `task.overdue` | `taskId`, `assigneeId`, `createdByUserId`, `status`, `dueDateUtc` (sent once, when the overdue alert is raised) |

* **Receiver expectations:**
  - Verify the signature with a constant-time comparison.
  - Reject `t` values older than 5 minutes.
  - Respond with `2xx` within 10 seconds.
* **Retries:** any non-`2xx` response, timeout, or network error is retried after 30 s, 2 min, 10 min, 1 h, and 6 h (6 attempts in total). After that the delivery is dead-lettered. Redirects are not followed.

---