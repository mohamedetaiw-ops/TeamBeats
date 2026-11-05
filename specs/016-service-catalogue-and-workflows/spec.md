# Feature Specification: Self Services, Service Catalogue and Workflows

| Field | Value |
|---|---|
| Spec ID | 016-service-catalogue-and-workflows |
| Area | workflows |
| Status | Proposed |
| Version | 0.1 |
| Owner | Mohamed |
| Approver | Mohamed |
| Target release | R1 (catalogue, linked apps, request forms, single-step approval, task inbox), R2 (multi-step workflows, escalation, integrations, service templates pack) |
| Last updated | 2026-11-05 |
| Requirement IDs | WF-001, WF-002, WF-003, WF-004 to WF-012 (new) |
| Use cases | UC-08 |
| Depends on | 002 (hierarchy), 003 (directory sync for prefill and manager), 004 (authorization), 005 (entitlements), 006 (audit), 007 (localization), 010 (profile and reporting line), 015 (notifications), 017 (connectors) |

## 1. Problem and outcome

**Problem:** Employees use an intranet mostly to get things done: ask for leave, a business card, a laptop repair, VPN access, a training seat, or file an expense. In the current intranet these services are spread across a hamburger menu, a 14 icon ribbon and home widgets, mix new pages with legacy pages that look different, and send people to separate systems with no warning. Each service was built by hand, so adding one means a development project.

**Outcome:** One service catalogue lists every service a person may use, whether it runs inside TeamBeats or in another system. Most services are configured, not coded: a form, prefilled from the employee's profile, plus an approval route. Every request follows the same pattern (New request, My requests, Team requests, Details and Tracking), and every approval lands in one task inbox with a badge in the header. Services that need their own business logic (leave balances, attendance, payslips) plug into the same catalogue and inbox.

## 2. Scope

**In scope:**
- Service catalogue: categories, search, favourites, availability by audience (tenant, org unit, group, membership type).
- Service types: (a) internal form service; (b) linked app, which opens another system with a clear "opens in another system" cue and SSO hint; (c) module service, provided by a domain module such as leave (spec 022) that registers itself in the catalogue.
- Form builder for tenant admins and service owners: field types, prefill from profile, conditional fields, attachments, policy notes shown in the form, validation rules.
- Request lifecycle: draft, submitted, in approval, returned for changes, approved, rejected, cancelled, completed. My requests and Team requests lists with filters, group by and export.
- Approval routes: line manager, manager of manager, org unit role (for example HR of my subsidiary), named group or queue, sequential and parallel steps, conditions on form values.
- Task inbox: one list of everything waiting for the person, with a header badge, from forms and from module services.
- Delegation while away ("Manage Replacement"): a person hands their approvals to a delegate for a date range.
- Escalation and reminders when a task waits too long.
- Fulfilment step: the request goes to a team queue (for example Logistics) that marks it done, or to a connector (spec 017) that creates the item in another system.
- A starter pack of service templates a tenant can switch on and edit (section 12).

**Out of scope:**
- Leave, attendance, compensation days and team calendars: spec 022, which registers module services here.
- Payslip and other payroll data: read from the HR or payroll system through spec 019.
- A full IT service management tool. IT tickets can be a form service, or a linked app to the customer's helpdesk.
- Native mobile apps. Mobile use is through the responsive web and PWA (EXP-009).

## 3. Actors and permissions

| Actor | What they can do | Permission / scope |
|---|---|---|
| Employee | Browse catalogue, submit and track own requests, act on own tasks, set a delegate | Catalogue audience rules; own requests only |
| On-behalf requester | Submit for another person when the service allows it (for example training on behalf of a team member) | `service.request_on_behalf` at the target person's scope |
| Line manager | See Team requests for direct and indirect reports, approve or return | Reporting line from spec 010 |
| Fulfilment agent | Work a service queue, complete or reject | Member of the service's queue group |
| Service owner | Design forms and routes for services in their area | `service.manage` on the service category or org scope |
| Tenant administrator | Manage catalogue, categories, templates, linked apps, global settings | Tenant |

## 4. User scenarios

### User Story 1: Find any service in one place (Priority: P1, R1)

Sara wants a business card. She opens Services, types "card" and sees "Business cards" under Logistics. Next to it, "Help Desk" carries an "opens in another system" icon.

**Acceptance scenarios:**
1. **Given** a catalogue with internal, linked and module services, **When** an employee opens it, **Then** they see only services whose audience includes them, grouped by category, with search, favourites and recently used.
2. **Given** a linked app, **When** the employee opens it, **Then** it opens in a new tab with a visible external cue, and the card says whether sign-in is single sign-on.
3. **Given** the global search (spec 012), **When** the employee types a service name or synonym, in Arabic or English, **Then** the service appears in the results.
4. **Given** a service not available to contractors, **When** a contractor searches for it, **Then** it does not appear and the direct URL returns "not available to you".

### User Story 2: Submit a request with a prefilled form (Priority: P1, R1)

**Acceptance scenarios:**
1. **Given** a form with profile prefill, **When** the employee opens New request, **Then** name, job title, department, manager, company, email and extension are filled from their profile and are read-only where the source is the directory or HR.
2. **Given** a form with a policy note (for example "Resignation needs 60 days notice"), **When** it is shown, **Then** the note appears next to the related field in the user's language.
3. **Given** a validation rule (for example last working day at least 60 days from today), **When** the employee submits an earlier date, **Then** submission is blocked with a localized message.
4. **Given** attachments allowed up to the service's limit, **When** a file is uploaded, **Then** it is malware scanned, stored under the tenant's object path, and classified with the request.
5. **Given** a submitted request, **Then** the employee sees it under My requests with status, current step, current assignee and a Tracking timeline.

### User Story 3: Approve from one task inbox (Priority: P1, R1)

Omar manages eight people. His header shows a badge with 3. The inbox lists one business card request, one training request and one leave request from spec 022.

**Acceptance scenarios:**
1. **Given** tasks from form services and module services, **When** Omar opens the inbox, **Then** all appear in one list with type, requester, age and due date, filterable by type.
2. **Given** a task, **When** Omar approves, rejects (reason required) or returns it for changes, **Then** the request moves on, the requester is notified (spec 015) and the action is recorded in Tracking and audit.
3. **Given** a task assigned to Omar, **When** another user calls the approve API for it, **Then** it is denied, even if they can see the request.
4. **Given** Team requests, **When** Omar opens it, **Then** he sees requests of his direct and indirect reports only, based on the reporting line, not on org unit membership.

### User Story 4: Delegate while away (Priority: P1, R1)

**Acceptance scenarios:**
1. **Given** Omar sets Layla as delegate from 1 to 14 August, **When** a task would be assigned to Omar in that window, **Then** it goes to Layla, marked "on behalf of Omar", and Omar can still see it.
2. **Given** a delegate who is also the requester, **When** the task would reach them, **Then** it skips to the next approver; nobody approves their own request.
3. **Given** the delegation ends, **Then** open tasks return to Omar unless already acted on.

### User Story 5: Multi-step workflow with conditions (Priority: P2, R2)

Training request: employee registers, training admin checks the seat, manager approves, and requests above a cost limit also need the department head.

**Acceptance scenarios:**
1. **Given** sequential and parallel steps with conditions on form values, **When** the request runs, **Then** each step is assigned by its rule, and steps whose condition is false are skipped and shown as skipped in Tracking.
2. **Given** a step with a 3 working day due time, **When** it is overdue, **Then** the assignee gets a reminder, and after the escalation time the task also goes to the escalation target.
3. **Given** a route changed by the service owner, **When** older requests are still open, **Then** they finish on the route version they started with.

### User Story 6: Fulfil and hand off to another system (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** an approved business card request, **When** it reaches the Logistics queue, **Then** any queue member can pick it, complete it or reject it with a reason.
2. **Given** a service whose fulfilment step is a connector (for example create a purchase request in Oracle Fusion), **When** the step runs, **Then** it is sent through the connector framework with retry and idempotency, and the external reference is shown on the request.
3. **Given** the external system rejects the item, **Then** the request shows "fulfilment failed", the queue is alerted, and the step can be retried.

### User Story 7: Build a new service without code (Priority: P1, R1 basic, R2 full)

**Acceptance scenarios:**
1. **Given** a service owner, **When** they create a service from a template or from scratch, **Then** they set name and description in Arabic and English, category, audience, form, route and fulfilment queue, preview it, and publish it.
2. **Given** a published service, **When** the owner edits the form, **Then** a new form version is created; submitted requests keep the version they were filled with.
3. **Given** a service owner scoped to "Acme Gulf", **When** they publish a service, **Then** its audience cannot reach beyond Acme Gulf.

### Edge cases
- Requester leaves the company (disabled by directory sync): open requests are cancelled with reason "requester left", except services marked "continue after exit" (for example resignation, final settlement).
- Approver leaves or has no manager: the task goes to the step's fallback (org unit admin or tenant admin queue) and the service owner is alerted.
- Manager changes during approval: an unclaimed manager step re-resolves to the new manager.
- Duplicate submission (double click, retry): idempotency key on submit returns the same request.
- Anonymous services (Speak Up style): the requester identity is not stored on the request and is not visible to reviewers or admins; see spec 023.

## 5. Functional requirements

- **FR-001**: The system MUST provide one service catalogue per tenant listing internal, linked and module services, filtered by audience. *(WF-004)*
- **FR-002**: Linked apps MUST show an external cue and SSO status, and MUST open only http(s) URLs registered by an admin. *(WF-005)*
- **FR-003**: Tenant admins and service owners MUST be able to build forms with field types text, number, money with currency, date, date range, choice, multi choice, person, org unit, lookup list, attachment and policy note, plus required, conditional and validation rules. *(WF-001)*
- **FR-004**: Forms MUST support prefill from profile fields; prefilled fields owned by the directory or HR MUST be read-only. *(WF-006)*
- **FR-005**: Every service MUST share the request lifecycle, My requests, Team requests, Details and Tracking views. *(WF-007)*
- **FR-006**: Workflows MUST support assignment by line manager, manager chain level, org unit role, group or queue; sequential and parallel steps; conditions; approve, reject, return and reassign; due times, reminders and escalation; and full history. *(WF-002)*
- **FR-007**: The system MUST provide one task inbox for all tasks, including those from module services, with an unread count in the header. *(WF-008)*
- **FR-008**: Users MUST be able to set a delegate for a date range. Nobody may approve their own request, directly or as delegate. *(WF-009)*
- **FR-009**: Form and route changes MUST be versioned; open requests finish on their starting version. *(WF-010)*
- **FR-010**: Module services MUST register through a catalogue contract (service ID, names, audience, launch route, task source) and their tasks MUST appear in the same inbox. *(WF-011)*
- **FR-011**: Workflow steps, reminders, escalations and connector hand-offs MUST run as durable background jobs with retry and idempotency. *(WF-003)*
- **FR-012**: Request data MUST support reporting (counts, cycle time, rejection reasons, structured reason codes such as resignation reasons) through spec 021 without exporting personal fields by default. *(WF-012)*

## 6. Business and validation rules
- Team requests use the reporting line (spec 010), filled from the directory `manager` attribute or HR. Org unit membership alone never makes someone an approver.
- A request's classification defaults to the service's classification (default Internal; services such as resignation or medical default to Confidential).
- Cancel is allowed by the requester until the first approval, then only through the service owner, unless the service says otherwise.
- Working days and holidays come from the tenant calendar for the requester's org unit (shared with spec 022).

## 7. Tenant and organization scope
- Services, forms, routes, requests, tasks, attachments and queues are tenant-owned with RLS.
- Cross-tenant denial: open a request, act on a task, list a queue, read an attachment.
- Org boundary: a service scoped to Acme Gulf is invisible and unusable in Acme Africa; a Team requests list never shows people outside the manager's reporting tree.

## 8. Data, ownership and classification

| Entity | System of record | Classification | Retention | Personal data? |
|---|---|---|---|---|
| Service, form, route | Product | Internal | Life of tenant, versions kept | No |
| Request and its form data | Product, or external system after hand-off | Per service (Internal default) | Per service, default 3 years after closure | Yes |
| Task and Tracking history | Product | Internal | Same as request | Yes |
| Attachment | Product object storage | Same as request | Same as request | Possibly |

## 9. Package entitlements

| Capability key | Behavior |
|---|---|
| `workflow.catalogue` | All packages (catalogue, linked apps, form services, single-step approval, task inbox, delegation) |
| `workflow.multi_step` | Workplace Plus and above (parallel steps, conditions, escalation) |
| `workflow.connector_fulfilment` | Workplace Plus and above |
| `workflow.max_services` | Quota per package (commercial data) |

## 10. UX, localization and accessibility
- One Services page replaces the menu, ribbon and widget routes of the old intranet. Home (spec 008) shows a personal "My services" shortcut strip drawn from favourites, and a task badge.
- All service names, descriptions, field labels, options and policy notes are stored per locale (Arabic and English in R1).
- Forms and grids are fully keyboard operable, labels are always visible (no icon-only actions), contrast meets WCAG 2.2 AA, layouts mirror in RTL.
- Grids have date filters, group by, column choice, row count and export, consistent across every service.

## 11. AI behavior
- R1: none required.
- Later (spec 014 and 020): the assistant can find the right service from a question ("how do I get a VPN?"), prefill a draft request for the user to review, and summarize a request for an approver. It never submits or approves without the user's explicit action.

## 12. Integrations, events and starter templates

Publishes `RequestSubmitted`, `RequestStepAssigned`, `RequestApproved`, `RequestRejected`, `RequestCompleted`, `TaskCreated`, `TaskCompleted`.

Starter templates (R2, tenant can switch on and edit). Taken from the current intranet review of 2026-11-05:

| Template | Notes |
|---|---|
| Business cards | Prefilled name, title, email, extension; include mobile; number of boxes |
| Company transportation | Pickup and drop-off from a tenant lookup list of areas |
| Device maintenance | Brand, part number, problem, accessories, serial number |
| Purchase request | Optional hand-off to ERP through a connector |
| Expense claim | Expense type, provider, period, subsidiary, department, country, currency, unit price, quantity, payment method |
| Travel request | Business trip with dates, destination, advance |
| Training request | Registration, training admin, manager approval; on behalf of option |
| VPN or system access | IT queue fulfilment |
| Resignation | 60 day notice rule, structured reason list for exit analytics, continues after exit |
| HR appointment | Pick a slot with HR |
| Parking waiting list | Queue position shown to the requester |
| Referral (candidate or business) | New and My referrals |
| Speak Up | Anonymous option, type (suggestion, complaint, problem, observation), attachment. Detailed in spec 023 |

## 13. Audit

| Event | Type | Key fields |
|---|---|---|
| service.created / published / changed / retired | audit | service, version, actor |
| request.submitted / cancelled | audit | request, service, requester or "anonymous" |
| task.approved / rejected / returned / reassigned / delegated | audit | task, actor, on behalf of |
| request.handoff_succeeded / failed | operational and audit | request, connector, external reference |

## 14. Security and abuse cases
- Approving through a crafted API call for a task not assigned to you: denied (US3 scenario 3).
- Delegation used to approve own request: blocked (FR-008).
- Linked app URL set to `javascript:` or an unregistered host: rejected (FR-002).
- Form field used to inject script into an approver's view: all values are encoded on output; rich text is not a field type.
- Anonymous service leaking identity through attachments or metadata: file metadata is stripped and no requester ID is stored.

## 15. Non-functional requirements
- Catalogue and task inbox load p95 under 1 second for a user with 500 open tasks.
- A step assignment is visible in the assignee's inbox within 10 seconds of the previous action.
- Design-to scale: 200 services and 5,000 requests per day per tenant.

## 16. Success criteria
- **SC-001**: Every service of the current intranet is reachable from the TeamBeats catalogue, either inside TeamBeats or as a linked app.
- **SC-002**: A service owner can publish a new two-step form service in under 30 minutes without developer help.
- **SC-003**: A manager handles all pending approvals from one inbox.

## 17. Open questions
1. Release split: should the catalogue, linked apps, simple form services and task inbox come forward into R1 (as written above) so the pilot can replace the current intranet's menu, or stay in R2 as the roadmap had them? Default as written: R1.
2. Retention for request data: default 3 years after closure, configurable per service.

## 18. Traceability

| Requirement ID | Covered by |
|---|---|
| WF-001 | FR-003, US7 |
| WF-002 | FR-006, US3, US5 |
| WF-003 | FR-011, US6 |
| WF-004 to WF-012 (new) | FR-001, FR-002, FR-004, FR-005, FR-007 to FR-010, FR-012 |
| UC-08 | US2, US3 |
