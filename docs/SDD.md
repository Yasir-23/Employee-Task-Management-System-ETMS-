# System Design Document (SDD)
## Project: Employee Task Management System (ETMS)
**Document Version:** 1.0.0  
**Target Runtime:** .NET 8 (ASP.NET Core Web API)  
**Database:** PostgreSQL 16 (via Entity Framework Core 8)  

---

### 1. Architectural Topology & Layering

The application implements a classic Layered Architecture. Dependencies flow strictly in one direction:

- Presentation Layer: Controllers with [Authorize] RBAC, Global Exception Handling, and DTOs.
- Business Service Layer: TaskService (enforcing BR-01 through BR-05), AuthService, EmployeeService, NotificationService, and BackgroundService.
- Data & Persistence Layer: AppDbContext, Auto-Audit SaveChangesInterceptor, and EF Core Migrations.
- Database: PostgreSQL 16.

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

4. Database Indexes
IX_Users_Email: Unique index on Users(Email)

IX_Users_ManagerId: Index on Users(ManagerId)

IX_Tasks_Assignee_Status: Composite index on Tasks(AssigneeId, Status) for rapid 5-task rule validation

IX_Tasks_DueDate_Status: Composite index on Tasks(DueDateUtc, Status) for background worker queries

IX_AuditLogs_TaskId: Index on AuditLogs(TaskId)

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