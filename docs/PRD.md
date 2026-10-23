# Product Requirements Document (PRD)
## Project Name: Employee Task Management System (ETMS)
**Document Version:** 1.0.0  
**Status:** Approved  
**Author:** Tech Lead & Principal Software Engineering Team  

---

### 1. Executive Summary & Problem Statement
The organization currently operates across multiple departments with an employee base ranging from 50 to 200 staff members. Task assignment, execution tracking, and accountability are currently managed informally through fragmented WhatsApp group chats and shared Microsoft Excel spreadsheets. 

This informal process has resulted in:
- High task abandonment and missed project deadlines.
- Complete lack of accountability (no single source of truth for task ownership).
- Unbalanced workload distribution (some staff handle 10+ concurrent items while others are idle).
- Zero visibility for department heads and executive management regarding organizational throughput.

**The Solution:** Build a centralized, secure, multi-tenant-ready backend platform called the **Employee Task Management System (ETMS)**. ETMS establishes structured role boundaries, automates workload limits, enforces a strict task transition lifecycle, and provides real-time reporting.

---

### 2. Business Objectives & Success Metrics
- **Zero Forgotten Tasks:** Eliminate manual tracking via WhatsApp/Excel by migrating 100% of internal task assignments to the centralized API.
- **Fair Workload Balance:** Mathematically prevent employee burnout by enforcing a hard ceiling of 5 active concurrent tasks per staff member.
- **Auditability:** Provide complete legal and operational traceability by automatically logging every state change, assignment alteration, and timestamp.
- **Proactive Notification:** Reduce overdue task occurrences by delivering automated advance email warnings 24 hours prior to deadline expirations.

---

### 3. User Personas & Roles

| Persona | Role | Primary Goals & Responsibilities |
| :--- | :--- | :--- |
| **System Administrator** | `Admin` | Overall system governance: onboarding/offboarding employees, creating departments, assigning line managers, and maintaining system health. |
| **Team Lead / Manager** | `Manager` | Operational execution: creating work items, assigning tasks to direct reports, monitoring team bandwidth, and handling escalations. |
| **Individual Contributor** | `Employee` | Day-to-day delivery: viewing active work queues, updating progress status, posting execution commentary, and completing work items. |

---

### 4. High-Level Feature Scope

            ┌──────────────────────────────────────┐
            │          ETMS Backend Engine         │
            └──────────────────┬───────────────────┘
                               │
  ┌────────────────────────────┼───────────────────────────┐
  ▼                            ▼                           ▼
┌──────────────┐          ┌───────────────────┐       ┌─────────────────┐
│ Identity &   │          │  Core Task Engine │       │  Intelligence & │
│ Governance   │          │                   │       │   Automation    │
├──────────────┤          ├───────────────────┤       ├─────────────────┤
│ • JWT + Rot. │          │ • Lifecycle FSM   │       │ • Workload Cap  │
│ • RBAC Guard │          │ • Team Isolation  │       │ • Audit Engine  │
│ • Department │          │ • Real-time Notes │       │ • Deadline Cron │
│   Hierarchy  │          │ • Audit Trail     │       │ • Org Analytics │
└──────────────┘          └───────────────────┘       └─────────────────┘


#### 4.1 Identity & Organization Management
- **Role-Based Access Control (RBAC):** Strict isolation between `Admin`, `Manager`, and `Employee` permissions.
- **Organizational Tree:** Deterministic hierarchy linking each employee to exactly one department and one direct reporting manager.

#### 4.2 Task Lifecycle & State Machine
- **Structured Fields:** Title, Description, Priority (Low, Medium, High), Due Date, Lifecycle Status, Creator, and Assignee.
- **Linear State Machine:** Strict sequential movement: `Pending` $\to$ `In Progress` $\to$ `In Review` $\to$ `Completed`. Reopening or reassigning completed tasks is forbidden.

#### 4.3 Automated Governance (Business Guardrails)
- **Hard Workload Cap:** Rejection of assignment if an employee currently holds $\ge 5$ active (non-completed) tasks.
- **Managerial Scoping:** Managers are strictly blocked from assigning tasks to individuals outside their direct reporting tree.
- **Ownership Integrity:** Employees can update status only on items explicitly assigned to them.

#### 4.4 Background Engine & Notifications
- **Event-Driven Dispatch:** Instant asynchronous email delivery upon task creation/assignment.
- **Automated Deadline Watcher:** Background daemon that wakes periodically to dispatch 24-hour advance reminders and escalate overdue states to managers.

#### 4.5 Executive Analytics
- Aggregated department throughput, average completion latencies, overdue counts, and live team utilization metrics.

---

### 5. Out of Scope for Phase 1
- Native mobile clients (Android/iOS).
- File attachments (S3/Blob storage uploads on task comments).
- Multi-language localization (English only for V1).
- Direct peer-to-peer real-time chat messaging.