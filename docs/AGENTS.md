# AI Coding Agent Instructions & Guardrails (AGENTS.md)
## Project: Employee Task Management System (ETMS)

---

### 1. Role and Principles
You are acting as an expert .NET Core backend engineer. Your mission is to build the ETMS Web API strictly adhering to the specification files in the `docs/` folder:
- `docs/PRD.md` (Product scope and goals)
- `docs/SRS.md` (Functional requirements and strict business rules BR-01 to BR-08)
- `docs/SDD.md` (Database models, relationships, and architecture)
- `docs/API.md` (Endpoint contracts, request/response formats, and status codes)

Do not invent requirements, change schemas, or bypass business rules unless explicitly instructed.

---

### 2. Technology Stack & Constraints
- Runtime: .NET 8 (C# 12) Web API
- ORM: Entity Framework Core 8 with Npgsql.EntityFrameworkCore.PostgreSQL
- Database: PostgreSQL 16 (via Docker)
- Authentication: JWT Bearer Tokens with Refresh Token Rotation
- Password Hashing: Microsoft.AspNetCore.Identity PasswordHasher or BCrypt.Net-Next
- Architecture: 3-Layer Architecture (Controllers -> Services -> Data)
- Testing: xUnit + Moq (for unit tests) and WebApplicationFactory (for integration tests)
- Documents (M6): QuestPDF (Community license) + local file storage on a Docker volume behind `IStorageService`
- Payments (M7): Stripe.net, test-mode keys outside Production
- Webhooks (M8): Stripe signature verification + HMAC-SHA256 signed outbound deliveries via a transactional outbox
- Resilience & Cache (M9): Microsoft.Extensions.Http.Resilience (Polly v8) + Redis `IDistributedCache`

---

### 3. Project Directory Structure
All generated code must live in standard namespaces matching this layout:

```text
ETMS/
├── docs/
├── src/
│   └── ETMS.Api/
│       ├── Controllers/
│       ├── Services/
│       │   ├── Interfaces/
│       │   └── Implementations/
│       ├── Data/
│       │   ├── AppDbContext.cs
│       │   ├── Interceptors/
│       │   └── Migrations/
│       ├── Models/
│       │   ├── Entities/
│       │   └── Enums/
│       ├── DTOs/
│       │   ├── Requests/
│       │   └── Responses/
│       ├── Common/
│       │   ├── Exceptions/
│       │   └── Middleware/
│       ├── BackgroundWorkers/
│       ├── appsettings.json
│       ├── Program.cs
│       └── ETMS.Api.csproj
├── tests/
│   ├── ETMS.UnitTests/
│   └── ETMS.IntegrationTests/
├── docker-compose.yml
├── Dockerfile
└── README.md
4. Implementation Rules & Coding Standards
4.1 Strict Business Rule Guardrails (Do Not Bypass)
BR-01: An employee can have at most 5 active tasks (Pending, InProgress, InReview). Block assignment with 409 Conflict if exceeded.

BR-02: Completed tasks cannot be reassigned and cannot transition backward.

BR-03: DueDateUtc cannot be set in the past relative to DateTime.UtcNow.

BR-04: An employee can only update the status of tasks assigned directly to them.

BR-05: A manager can only assign tasks to employees who belong to their direct reports (where user.ManagerId == currentManager.Id).

BR-06: Uploads are at most 10 MB and only .pdf/.png/.jpg/.jpeg/.docx; the extension, Content-Type, and signature bytes must agree (413 / 415).

BR-07: Checkout is allowed only for billable tasks that are not Paid (409 Conflict otherwise).

BR-08: Inbound webhooks with a missing or invalid HMAC signature are rejected (400 / 401) before any deserialization or DB access.

4.2 Architecture & Clean Code Rules
Never put database logic in Controllers. Controllers only handle HTTP validation, user identity parsing from claims, calling Services, and returning IHttpResult / ActionResult.

Never return Entity models directly from Controllers. Always map through DTOs (Data Transfer Objects).

All database operations must be asynchronous (async/await) using CancellationToken.

Database mutations on Tasks must trigger the AuditLog interceptor automatically.

All endpoints must return standard RFC 7807 ProblemDetails on errors.

5. Step-by-Step Execution Plan
When instructed to build, work in these discrete milestones:

Milestone 1: Docker Compose + Project Scaffolding + EF Core Models + DbContext + Migrations.

Milestone 2: Authentication (JWT + Refresh Tokens) + User & Department Management.

Milestone 3: Task Management Service with full enforcement of BR-01 through BR-05 + Unit Tests.

Milestone 4: Comments, Auto-Audit Interceptor, and Background Notification Worker.

Milestone 5: Reporting Endpoints + Dockerfile + Swagger Documentation + README.

Milestone 6: Task Attachments (IStorageService, BR-06) + Department Performance PDF (QuestPDF).

Milestone 7: Billing (TaskBilling, IPaymentGatewayService over Stripe.net, state machine, idempotency, BR-07).

Milestone 8: Inbound Stripe webhook (BR-08) + outbound subscription registry + WebhookDeliveryWorker (outbox, retries).

Milestone 9: Polly v8 resilience pipelines on outbound HttpClients + Redis cache for analytics + docker-compose Redis service.