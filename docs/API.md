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