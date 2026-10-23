# Employee Task Management System (ETMS)

A .NET 8 Web API that replaces ad-hoc task tracking (chat groups and spreadsheets) with a single,
auditable system: role-based access, a strict task lifecycle, workload limits, automatic audit
history, deadline notifications and reporting.

The specifications live in [`docs/`](docs/):
[PRD](docs/PRD.md) · [SRS](docs/SRS.md) · [SDD](docs/SDD.md) · [API](docs/API.md) · [AGENTS](docs/AGENTS.md).

---

## Architecture

Three layers, with dependencies flowing one way:

```text
Controllers  ──►  Services  ──►  Data (EF Core)  ──►  PostgreSQL 16
  HTTP, [Authorize],   business rules       AppDbContext,
  DTOs, claims         BR-01 … BR-05        audit interceptor,
                                            migrations
```

| Folder (`src/ETMS.Api/`) | Contents |
| --- | --- |
| `Controllers/` | `Auth`, `Departments`, `Employees`, `Tasks`, `Reports`. They parse the request and the caller's claims, then call a service. |
| `Services/` | `AuthService`, `TokenService`, `PasswordHasherService`, `DepartmentService`, `EmployeeService`, `TaskService` (business rules), `ReportService`, `NotificationService`, `LoggingEmailService` |
| `Data/` | `AppDbContext` (Fluent API config and indexes), `Interceptors/TaskAuditSaveChangesInterceptor`, `Migrations/`, `DbInitializer` (migrate and seed) |
| `Models/` | Entities (`User`, `Department`, `RefreshToken`, `TaskItem`, `Comment`, `AuditLog`) and enums |
| `DTOs/` | Request and response contracts. Entities are never returned directly. |
| `Common/` | RFC 7807 exception middleware, typed exceptions, settings, mappings, claim helpers |
| `BackgroundWorkers/` | `TaskNotificationWorker`: hourly deadline reminders and overdue escalations |

Other cross-cutting pieces:

- **Authentication:** JWT bearer access tokens (15 min) and refresh tokens (7 days). Refresh tokens are stored as SHA-256 hashes and rotated on every refresh.
- **Passwords:** ASP.NET Core Identity `PasswordHasher` (PBKDF2-HMAC-SHA512, 100,000 iterations).
- **Errors:** every error response is `application/problem+json` (RFC 7807). Unexpected exceptions return a sanitized 500.
- **Audit:** an EF Core `SaveChangesInterceptor` writes an `AuditLog` row whenever a task's `Status`, `AssigneeId` or `DueDateUtc` changes. It runs in the same save, so in the same transaction.

---

## Prerequisites

| Tool | Needed for |
| --- | --- |
| [Docker](https://www.docker.com/) with Compose v2 | Quickstart (API and database in containers) |
| [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) | Local development and running tests |
| PostgreSQL 16 | Provided by `docker-compose.yml`; no separate install needed |

---

## Quickstart (Docker Compose)

```bash
docker compose up --build
```

| Service | URL |
| --- | --- |
| API | http://localhost:5000 |
| Swagger UI | http://localhost:5000/swagger |
| Health check | http://localhost:5000/health |
| PostgreSQL | `localhost:5432`, database `etms_db`, user `postgres`, password `postgrespassword` |

On startup the API applies EF Core migrations and seeds the accounts below.

> **Upgrading from the database-only compose file?** The database service was renamed from
> `postgres` to `db`. Run `docker compose down` once before `docker compose up --build`. This
> removes the old container but keeps the data volume.

### Local development (API on the host, database in Docker)

```bash
docker compose up -d db
dotnet run --project src/ETMS.Api      # http://localhost:5109/swagger
```

### Default seed credentials

Seeding only happens when the `Users` table is empty. The Manager and Employee demo accounts are
created only in the `Development` environment, which Compose uses.

| Role | Email | Password |
| --- | --- | --- |
| Admin | `admin@company.com` | `Admin@123456` |
| Manager | `manager.sarah@company.com` | `SecurePassword123!` |
| Employee (reports to Sarah) | `alex.mercer@company.com` | `InitialPassword123!` |

> These credentials and the JWT secret in `appsettings*.json` are for local use only. Outside
> local use, override them with environment variables, for example `JwtSettings__Secret` and
> `ConnectionStrings__DefaultConnection`.

### Trying it in Swagger

1. `POST /api/auth/login` with one of the accounts above.
2. Click **Authorize** and paste the returned `accessToken`.
3. As Sarah, call `POST /api/tasks` with Alex's id (from `GET /api/employees`) as `assigneeId`.
4. As Alex, call `PATCH /api/tasks/{id}/status` with `InProgress`, then `InReview`, then `Completed`.
5. Call `GET /api/tasks/{id}` to see the comments and the automatic audit trail.

---

## Running the tests

```bash
dotnet test ETMS.sln
```

`tests/ETMS.UnitTests` (xUnit, Moq, EF Core InMemory) covers:

- every business rule (BR-01 to BR-05) and the status state machine
- comment permissions
- audit interceptor output
- notification reminder windows and duplicate suppression
- report calculations and role boundaries

No database is needed to run them.

---

## Core business rules

Violations return RFC 7807 ProblemDetails with the status code shown.

| Rule | Description | Response |
| --- | --- | --- |
| **BR-01**: Active task limit | An employee can hold at most **5** active tasks (`Pending`, `InProgress` or `InReview`). Creating or reassigning a 6th is rejected. Assignments to the same employee are serialized with a row lock, so concurrent requests can't exceed the limit. | `409 Conflict` |
| **BR-02**: Completed is terminal | A `Completed` task can't be reassigned and its status can't change. | `409 Conflict` |
| **BR-03**: Due date feasibility | `dueDate` must be later than the current UTC time. | `400 Bad Request` |
| **BR-04**: Self-service boundary | Only the task's assignee can change its status. | `403 Forbidden` |
| **BR-05**: Managerial boundary | A manager can only assign or reassign tasks to employees whose `ManagerId` is that manager. | `403 Forbidden` |
| **Lifecycle** | Status moves one step at a time: `Pending → InProgress → InReview → Completed`. Skipping a step or moving backwards is rejected. | `400 Bad Request` |

---

## Notifications

### Instant assignment emails

`TaskService` emails the assignee as soon as a task is assigned to them:

- **New task** (`POST /api/tasks`): the assignee receives the title, priority and due date.
- **Reassignment** (`PUT /api/tasks/{id}/reassign`): only the new assignee is emailed. Reassigning a task to its current assignee sends nothing.

Emails are sent only after the database transaction commits, so a rejected assignment (for example
BR-01 or BR-05) never sends one. If delivery fails, the error is logged and the request still
succeeds, because the task change has already been saved.

### Background deadline worker

`TaskNotificationWorker` runs every `NotificationWorker:IntervalSeconds`: 3600 by default, 60 in
Development. Each cycle:

- **24-hour reminder:** emails the assignee of each active task due within 23 to 24 hours from now, then sets `ReminderSent`.
- **Overdue escalation:** emails the creating manager of each active task that is past due, then sets `OverdueAlertSent`.

Emails go through `IEmailService`. The default `LoggingEmailService` writes
`[SIMULATED EMAIL] To: … | Subject: … | Body: …` to the log. To send real email, register an
SMTP or provider implementation in `Program.cs`.

---

## API reference

All routes are under `/api` and need `Authorization: Bearer <accessToken>` unless marked public.
Enums are sent and accepted as strings, for example `"High"` or `"InProgress"`.

| Method | Route | Access | Description |
| --- | --- | --- | --- |
| POST | `/auth/login` | Public | Email and password. Returns the access token, refresh token and user. |
| POST | `/auth/refresh` | Public | Rotates the refresh token and returns a new token pair. |
| POST | `/auth/revoke` | Public (requires the refresh token) | Logout: `{ "refreshToken": "…" }` revokes that token and returns `{ "message": "Token revoked successfully." }`. Repeating the call is harmless; an unknown token returns 400. |
| POST | `/departments` | Admin | Create a department. |
| GET | `/departments` | Authenticated | List departments. |
| POST | `/employees` | Admin | Create a user. Employees require a Manager as `managerId`. |
| GET | `/employees?search=&departmentId=&role=&pageNumber=&pageSize=` | Admin, Manager | Paginated search, max `pageSize` 100. |
| POST | `/tasks` | Manager | Create and assign a task (BR-01, BR-03, BR-05). |
| GET | `/tasks?status=&priority=&pageNumber=&pageSize=` | Authenticated | Tasks visible to the caller: all for Admin; created or team tasks for Manager; own tasks for Employee. |
| GET | `/tasks/{id}` | Authenticated (in scope) | Task details with comments and audit history. |
| PATCH | `/tasks/{id}/status` | Employee (assignee) | `{ "newStatus": "InProgress" }` (BR-02, BR-04, lifecycle). |
| PUT | `/tasks/{id}/reassign` | Manager | `{ "newAssigneeId": "…" }` (BR-01, BR-02, BR-05). |
| POST | `/tasks/{id}/comments` | Assignee, creator, Admin | `{ "content": "…" }` |
| GET | `/reports/summary` | Admin, Manager | Task counts by status plus overdue; team-scoped for Managers. |
| GET | `/reports/workload` | Admin, Manager | Active and completed tasks per employee; direct reports only for Managers. |
| GET | `/reports/department-performance?from=&to=` | Admin | Per department: completion rate and average turnaround hours for tasks created in the window. Defaults to the last 30 days. |
| GET | `/health` | Public | `{ "status": "Healthy", "database": "PostgreSQL" }` |

The full request and response contracts are in [`docs/API.md`](docs/API.md) and in Swagger.

---

## Configuration

| Key | Default | Notes |
| --- | --- | --- |
| `ConnectionStrings:DefaultConnection` | `Host=localhost;Port=5432;Database=etms_db;…` | Compose overrides it with `Host=db`. |
| `JwtSettings:Secret` | development value | At least 32 bytes. **Replace outside local use.** |
| `JwtSettings:AccessTokenExpirationMinutes` | `15` | Validated: 1 to 15 (FR-AUTH-02). |
| `JwtSettings:RefreshTokenExpirationDays` | `7` | |
| `NotificationWorker:Enabled` | `true` | |
| `NotificationWorker:IntervalSeconds` | `3600` (`60` in Development) | |

Any key can be set as an environment variable by replacing `:` with `__`, for example
`JwtSettings__Secret`.
