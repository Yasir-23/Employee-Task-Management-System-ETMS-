# Software Requirements Specification (SRS)
## Project: Employee Task Management System (ETMS)
**Standard:** IEEE 830 / ISO-IEC 25010 Compliant Specification  
**Version:** 1.0.0  

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

---

### 4. Strict Business Rules (BR)

Any API request violating these rules must be rejected immediately with an HTTP 400/409/422 status code, accompanied by a structured `RFC 7807 ProblemDetails` error response.

* **BR-01 (Active Task Limit):** An employee cannot possess more than **5 active tasks** simultaneously. A task is defined as "active" if its status is `Pending`, `InProgress`, or `InReview`. An assignment attempt exceeding 5 must return `409 Conflict`.
* **BR-02 (Terminal Status Immutability):** A task marked as `Completed` is immutable regarding assignment and status. It **cannot be reassigned**, and its status cannot be changed back to any earlier state.
* **BR-03 (Due Date Feasibility):** A task’s `DueDate` cannot be set in the past relative to UTC server time at the moment of creation or modification.
* **BR-04 (Self-Service Boundary):** An employee can only update the status of tasks where `AssigneeId == CurrentAuthenticatedUserId`. Attempting to modify other employees' tasks must return `403 Forbidden`.
* **BR-05 (Managerial Domain Boundary):** A `Manager` can only assign or reassign tasks to employees who have `ManagerId == CurrentAuthenticatedUserId`. Assigning to an employee outside their team must return `403 Forbidden`.

---

### 5. Non-Functional Requirements (NFR)

#### 5.1 Security
- All incoming HTTP traffic must be rejected or redirected to HTTPS.
- Passwords must be hashed using industry-standard key derivation functions (minimum 100,000 PBKDF2 iterations or Argon2id).
- Endpoints must validate model schemas before controller execution; invalid payloads must return structured `400 Bad Request` payloads.
- Role checks must execute via declarative authorization attributes (`[Authorize(Roles = "...")]`).

#### 5.2 Performance & Scalability
- Read endpoints returning lists must mandate pagination parameters with a hard upper limit of `PageSize = 100`.
- Database access must use compiled, parameterized queries via Entity Framework Core to eliminate SQL injection.
- Average API response time must remain under **200 milliseconds** for standard CRUD operations at 200 concurrent requests, and under **2 seconds** for complex analytical aggregations.

#### 5.3 Reliability & Fault Tolerance
- All unexpected exceptions must be intercepted by a centralized global exception middleware returning sanitized `500 Internal Server Error` responses with zero stack trace leakage in production.
- Database writes involving multi-table operations (e.g., updating a task and writing an audit log) must execute within an atomic transaction.

#### 5.4 Maintainability & Extensibility
- Architecture must follow standard layered separation: `Controllers` $\to$ `Services` (Business Logic) $\to$ `Data/Repositories` (EF Core).
- All business rule logic must be covered by automated unit tests with minimum 80% line coverage.