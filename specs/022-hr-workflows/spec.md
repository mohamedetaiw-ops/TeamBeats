# Feature Specification: Leave, Attendance and HR Workflows

| Field | Value |
|---|---|
| Spec ID | 022-hr-workflows |
| Area | workflows |
| Status | Approved |
| Version | 1.0 |
| Owner | Mohamed |
| Approver | Mohamed |
| Target release | R1 (holiday and working week calendars per country and location, and the working-day contract used by specs 016 and 024), R2 (leave, Ramadan and other reduced hours periods, team availability, attendance, onboarding and offboarding journeys, employee data change, HR letters, expense claims), R3 (probation and contract milestones with HR write-back, shift rosters, AI receipt reading for all tenants) |
| Last updated | 2026-11-05 |
| Approved | 2026-11-05 by Mohamed |
| Requirement IDs | INT-007, UC-08, UC-09, LVA-001 to LVA-012 and HRW-001 to HRW-014 (new) |
| Use cases | UC-08 (service request), UC-09 (HR data) |
| Depends on | 002, 003 (`UserProvisioned`, `UserDeprovisioned`), 004, 005, 006, 007, 010 (profile and reporting line), 011 (documents), 015 (notifications), 016 (service catalogue, task inbox, delegation), 017 (connectors), 019 (HR integration), 021 (analytics), 024 (process engine), 025 (onboarding policy attestations), 026 (classification), 028 (learning assignments) |

## 1. Problem and outcome

**Problem:** HR teams run the same requests every day: leave, attendance corrections, salary certificates, data changes, expense claims, new joiners and leavers. In most customers these live in email, spreadsheets and a dated HR portal, so employees do not know their balance, managers approve leave without seeing who else is off, new joiners arrive with no laptop and no access, and leavers keep access for weeks. A plain form with an approval (spec 016) is not enough here: leave needs balances and calendars, onboarding needs a timed set of tasks across HR, IT, facilities and the manager, and expenses need policy limits and receipts.

**Outcome:** TeamBeats provides HR module services that sit in the one service catalogue (spec 016), put every approval and task in the one task inbox, and run on the process engine (spec 024). Employees see their leave balance before they ask, managers see team availability when they approve, attendance exceptions are fixed in two clicks, new joiners get a guided first 90 days, leavers lose access on their last day with every hand-over tracked, and expense claims check policy limits before they reach the manager. Where the customer's HR or payroll system is the system of record, TeamBeats reads from and writes to it through spec 019 instead of keeping its own copy.

## 2. Scope

**In scope:**
- Leave: leave types and policies (accrual, entitlement by grade or service years, carry-over, maximums, notice, documents required, gender or religion based types such as Hajj leave where the tenant enables them), live balance, half days and hours, replacement person, team calendar and overlap warning, cancellation and shortening, leave on behalf (by HR or manager).
- Calendars: public holidays per country and location, working week per location (for example Sunday to Thursday or Monday to Friday), Ramadan working hours, tenant events. These calendars are the working-day source for timers in spec 024 and due times in spec 016.
- Team availability: who is off, working remotely, travelling or on training, by team and org unit, respecting privacy (leave type hidden by default).
- Attendance: sign in and out on web and mobile with optional location check, imported clock data from time systems, working hours rules, lateness and early leave, missing punch, exception requests, compensation days, monthly attendance summary.
- Onboarding journeys: pre-boarding (before day one), day one, first week, 30, 60 and 90 day steps; tasks for HR, IT, facilities, the manager, a buddy and the new joiner; welcome page; policy attestations (spec 025) and learning assignments (planned spec 028); started by `UserProvisioned` or by an HR new hire event.
- Offboarding journeys: started by resignation approval, HR termination event or `UserDeprovisioned`; asset return, knowledge hand-over, access removal checklist, final settlement steps, exit interview with structured reasons, alumni contact consent.
- Internal transfer and promotion journeys: manager and unit change with access review.
- Employee data change: requests to change personal data (address, emergency contact, bank details, marital status, dependants) with documents, routed to HR, written back to HR through spec 019 when connected.
- HR letters and certificates: salary certificate, experience letter, NOC (no objection certificate), embassy letter; generated from tenant templates in Arabic and English with data from HR, approved where required, issued as a PDF with a verification code.
- Expense claims: claim lines with category, date, amount, currency and receipt; per diem and mileage; policy limits by grade and category; project or cost centre; receipt reading with AI where the tenant allows; route by amount; hand-off to finance or ERP through spec 017.
- Probation and contract milestones: probation review reminders and forms, contract end alerts.

**Out of scope:**
- Payroll calculation and payslips (payslip viewing is spec 019).
- Full time and attendance hardware management. TeamBeats reads clock data, it does not manage devices.
- Performance management cycles and goals. [NEEDS CLARIFICATION: in scope later?] Default: not planned; linked app.
- Recruitment and applicant tracking. Internal vacancies are content in spec 009.
- Room and desk booking (planned spec 031, and Microsoft 365 rooms through spec 018).
- The process engine itself (spec 024) and the generic catalogue, forms and task inbox (spec 016).

## 3. Actors and permissions

| Actor | What they can do | Permission / scope |
|---|---|---|
| Employee | Request leave, see own balances and attendance, request corrections, letters, data changes, submit expenses, complete own onboarding tasks | Tenant membership |
| Line manager | Approve team requests, see team calendar and attendance, run probation reviews, own the new joiner's manager tasks | Reporting line (spec 010) |
| Replacement person | Accept or decline being named as replacement | Named on the request |
| HR officer | Configure leave types and policies, calendars, journeys, letter templates; act on behalf; view HR data in scope | `hr.manage` at org scope |
| Payroll or finance officer | Process approved expenses, see claims in scope | `finance.process` at org scope |
| IT and facilities queues | Complete onboarding and offboarding tasks | Queue membership (spec 016) |
| Buddy | Get buddy tasks for a new joiner | Named on the journey |

## 4. User scenarios

### User Story 1: Request leave with live balance and team view (Priority: P1, R2)

Mona opens Leave, picks Annual leave for 12 to 16 October, sees she has 18 days and will have 13 left, sees two teammates already off on the 14th, names Karim as her replacement and submits. Her manager approves from the task inbox with the team calendar shown next to the request.

**Independent test:** configure an annual leave type with accrual; request, approve, cancel; check balance, calendar and replacement.

**Acceptance scenarios:**
1. **Given** an employee opens a leave request, **When** they choose a type and dates, **Then** they see the current balance, the days the request uses (excluding weekends and holidays of their location), and the balance after, before submitting.
2. **Given** the request exceeds the balance and the type does not allow negative balance, **Then** submission is blocked with the reason.
3. **Given** a type requires a document (for example sick leave over 2 days), **Then** submission requires an attachment.
4. **Given** others in the same team or unit are off on any of the days, **Then** the requester and the approver see an overlap warning with names (and not leave types unless permitted).
5. **Given** a replacement is named, **Then** they get a task to accept; if they decline, the requester is notified and can pick another. Delegation of approvals (spec 016) is offered for the leave period.
6. **Given** approval, **Then** the balance is reduced, the leave shows on the team calendar, the employee's status shows "out of office" on the profile (spec 010) for the dates, and the HR system is updated when connected (spec 019).
7. **Given** the employee cancels or shortens approved leave, **Then** the change follows the type's rule (approval required or not) and the balance is restored.
8. **Given** balances are owned by the HR system (spec 019), **Then** TeamBeats shows the HR balance and sends approved leave to HR; if HR is unreachable the request is accepted and synced later, and the employee sees "balance pending update".

### User Story 2: Calendars and working days (Priority: P1, R1)

**Acceptance scenarios:**
1. **Given** an HR officer, **When** they set the UAE public holidays for next year and a Monday to Friday working week for the Dubai location and Sunday to Thursday for Riyadh, **Then** leave day counts, spec 016 due times and spec 024 working-day timers use the calendar of the person's location.
2. **Given** holidays announced late (for example religious holidays confirmed shortly before), **When** HR updates the dates, **Then** open spec 016 due times and spec 024 timers are recalculated from the next evaluation, and (from R2, with leave) leave already approved over those dates is recounted, balances are adjusted, and affected employees are notified.
3. **Given** Ramadan working hours for a location, **Then** attendance rules use the reduced hours for those dates. *(R2)*
4. **Given** a tenant in R1 where no HR officer has set a calendar, **Then** a single tenant working week applies (Monday to Friday until a tenant administrator or HR officer changes it, for example to Sunday to Thursday), with no holidays, and due times still work.

### User Story 3: Attendance and exceptions (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** web or mobile sign in is enabled with an optional location check, **When** an employee signs in outside the allowed radius, **Then** the sign in is recorded with a "remote" flag or refused, as the tenant configured.
2. **Given** clock data imported from a time system through spec 017, **Then** each day shows hours worked, late arrival, early leave or missing punch against the working hours rule.
3. **Given** a missing punch, **When** the employee requests a correction with a reason, **Then** it routes to the manager, and on approval the day is corrected and marked "corrected".
4. **Given** an employee worked on a public holiday, **Then** a compensation day is credited according to the leave policy after manager approval.
5. **Given** a monthly summary, **Then** the employee and manager see totals, and HR can export the summary for payroll.

### User Story 4: Onboarding journey (Priority: P1, R2)

Omar's start date is entered in HR. Ten days before, the journey starts: IT gets laptop and account tasks, facilities a desk and badge task, the manager a "plan first week" task, and Omar gets a welcome page with what to bring. On day one he must accept the Code of Conduct and Information Security policy and finish two learning modules in his first week. On day 30, 60 and 90 the manager gets check-in forms.

**Acceptance scenarios:**
1. **Given** a journey template with steps relative to the start date, **When** HR records a new hire (from spec 019) or directory sync creates the user (spec 003 `UserProvisioned`), **Then** one journey starts for that person, with each task scheduled and assigned to its role or queue.
2. **Given** the start date moves, **Then** every pending step is rescheduled.
3. **Given** a pre-boarding step for the new joiner before they have an account, **Then** it is sent only to the personal email held in HR, with a time-limited link to a pre-boarding page that shows no internal content beyond what the template allows. [NEEDS CLARIFICATION: is pre-boarding before the account exists required?] Default: yes, R2, behind a tenant setting.
4. **Given** policies flagged "onboarding required" in spec 025, **Then** attestation tasks are created on day one with the journey's deadline.
5. **Given** learning assignments in the template, **Then** they are created in the learning module (planned spec 028) or a linked LMS.
6. **Given** a step is overdue, **Then** reminders and escalation follow spec 016 rules, and the HR officer sees all journeys with overdue steps.
7. **Given** a journey dashboard, **Then** HR sees each new joiner's progress by step and owner.

### User Story 5: Offboarding journey and access removal (Priority: P1, R2)

**Acceptance scenarios:**
1. **Given** an approved resignation (spec 016 service) or an HR termination event, **Then** an offboarding journey starts with the last working day as the anchor date.
2. **Given** the journey, **Then** it creates tasks: manager knowledge hand-over, asset return per asset assigned (from HR or an asset system), IT access removal, finance clearance, exit interview, and final settlement steps.
3. **Given** the last working day passes, **When** directory sync disables the account (spec 003), **Then** TeamBeats sessions are revoked, document check-outs go to the library manager (spec 011), content ownership moves to backup owners (spec 009), and open requests follow spec 016 exit rules.
4. **Given** the access removal checklist, **Then** each connected system shows whether access was removed, and any not confirmed after 1 working day is escalated to IT and security officers.
5. **Given** the exit interview, **Then** structured reasons are recorded for analytics (spec 021), and free text is visible only to HR officers.

### User Story 6: Employee data change (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** an employee changes their bank details, **When** they submit, **Then** the request requires a supporting document, routes to HR, and on approval is written to the HR system through spec 019; TeamBeats does not keep bank details after the write succeeds.
2. **Given** a field owned by HR, **Then** the profile shows the HR value until HR confirms the change.
3. **Given** a change to bank details, **Then** the employee gets a notification on all channels so that a fraudulent change by someone else is noticed.

### User Story 7: HR letters and certificates (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** a salary certificate request addressed to a bank, **When** the employee submits, **Then** the letter is generated from the tenant template with data from HR, in Arabic or English as chosen, and routed to HR if the letter type requires approval.
2. **Given** approval, **Then** the employee downloads a PDF with a reference number and a verification code; a verification page (public, no sign-in) confirms the letter's reference, type, issue date and the company name only, never salary or personal details. [NEEDS CLARIFICATION: is public verification acceptable?] Default: on, tenant can turn off.
3. **Given** a template change, **Then** issued letters stay as issued.

### User Story 8: Expense claims (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** an employee adds claim lines with receipts, **Then** each line is checked against the policy limit for their grade and category, and lines over the limit need a justification.
2. **Given** AI receipt reading is enabled, **When** a receipt is uploaded, **Then** merchant, date, amount, currency and tax are suggested for the employee to confirm. Nothing is filled without confirmation.
3. **Given** a claim in a foreign currency, **Then** it is converted at the rate source configured by finance on the receipt date, and both amounts are shown.
4. **Given** a duplicate receipt (same merchant, date and amount, or same image hash), **Then** the claim is flagged for the approver.
5. **Given** routes by amount (for example manager up to 5,000, plus department head above), **Then** the route runs on spec 024.
6. **Given** finance approval, **Then** the claim is handed to the ERP or finance system through spec 017, and the external reference and payment status are shown.

### Edge cases and exception flows
- Leave across a year end with carry-over limits: days are counted against the right year's balance.
- Leave request overlapping an existing approved leave: refused.
- Employee changes location mid-year: the new calendar applies from the transfer date.
- Manager is the requester's replacement and approver: allowed, but the manager still cannot approve their own requests.
- An onboarding journey for a re-hire: starts a new journey, linked to the earlier employment.
- A new joiner never shows up: HR cancels the journey, which cancels pending tasks and starts the access removal checklist.
- Clock data arrives late or twice: imports are idempotent per punch ID.
- Expense receipts in Arabic: receipt reading supports Arabic and English.

## 5. Functional requirements

- **FR-001 / LVA-001**: The system MUST support leave types with configurable accrual, entitlement rules, carry-over, maximums, notice, required documents, half days and hours, and eligibility rules.
- **FR-002 / LVA-002**: The leave request MUST show current balance, days used and balance after, counting only working days of the person's location calendar.
- **FR-003 / LVA-003**: The requester and approver MUST see team overlap with names, and leave types only when the viewer is permitted.
- **FR-004 / LVA-004**: Leave requests MUST support a replacement person with accept or decline, and offer approval delegation (spec 016) for the period.
- **FR-005 / LVA-005**: Approved, cancelled and shortened leave MUST update balances, the team calendar, profile status and the HR system when connected (spec 019). *(INT-007)*
- **FR-006 / LVA-006**: When the HR system owns balances, the system MUST read balances from it and queue writes when it is unavailable.
- **FR-007 / LVA-007**: *(R1)* HR officers MUST manage public holiday and working week calendars per country and location (reduced hours periods from R2), and these calendars MUST be the working-day source for specs 016 and 024 through the `Hr` working-day contract (count and add working days for an org unit or location). Calendars are part of every package and have no capability key, because workflow due times depend on them.
- **FR-008 / LVA-008**: Changes to holidays MUST recount affected approved leave and notify affected people.
- **FR-009 / LVA-009**: The system MUST provide a team availability view by team and unit with privacy rules on leave types.
- **FR-010 / LVA-010**: Attendance MUST support web and mobile sign in with optional location check, clock data import, working hours rules, exception detection and correction requests.
- **FR-011 / LVA-011**: Compensation days MUST be credited by policy after approval.
- **FR-012 / LVA-012**: Monthly attendance summaries MUST be viewable and exportable for payroll.
- **FR-013 / HRW-001**: HR officers MUST be able to define journey templates (onboarding, offboarding, transfer, promotion) with steps relative to an anchor date, assigned to roles, queues, the person, the manager or a buddy, built and run on spec 024.
- **FR-014 / HRW-002**: Journeys MUST start from HR events (spec 019), directory events (spec 003) or approved services (spec 016), once per person and event, and MUST reschedule when the anchor date changes.
- **FR-015 / HRW-003**: Onboarding MUST be able to create policy attestation tasks (spec 025) and learning assignments (planned spec 028).
- **FR-016 / HRW-004**: Pre-boarding MUST use a time-limited link to the personal email from HR and show only template content.
- **FR-017 / HRW-005**: Offboarding MUST include an access removal checklist per connected system with confirmation and escalation, and MUST trigger the ownership hand-overs of specs 009, 011 and 016.
- **FR-018 / HRW-006**: Exit interviews MUST record structured reasons for analytics and keep free text visible to HR only.
- **FR-019 / HRW-007**: Employee data change requests MUST require documents per field, route to HR, write back to HR through spec 019, and not retain sensitive values (bank details) after a successful write.
- **FR-020 / HRW-008**: Sensitive profile changes MUST notify the employee on all channels.
- **FR-021 / HRW-009**: HR letters MUST be generated from bilingual templates with HR data, approved where required, issued as PDF with a reference and verification code, and kept as issued.
- **FR-022 / HRW-010**: The letter verification page MUST reveal only reference, letter type, issue date and company name.
- **FR-023 / HRW-011**: Expense claims MUST support lines, receipts, categories, per diem, mileage, cost centres, multi-currency with a configured rate source, policy limits by grade and category, and duplicate detection.
- **FR-024 / HRW-012**: AI receipt reading MUST only suggest values for the employee to confirm, and MUST follow the tenant AI policy for the receipt's classification.
- **FR-025 / HRW-013**: Approved claims MUST hand off to finance or ERP through spec 017 with external reference and payment status shown.
- **FR-026 / HRW-014**: Every HR module service MUST register in the catalogue through the module contract of spec 016 (FR-010), and every human step MUST be a task in the task inbox. *(UC-08)*

## 6. Business and validation rules
- Default rounding: leave in half days unless the type allows hours.
- Balances are never negative unless the type allows it, with a maximum negative set per type.
- An employee cannot approve their own leave, attendance correction or expense, directly or as delegate.
- Leave types marked sensitive (for example sick, maternity, bereavement) are shown to colleagues as "out of office" only.
- Journey tasks assigned to someone who has left fall back to the queue for that step.
- Expense claims must be submitted within the policy window (default 60 days after the expense date).

## 7. Tenant and organization scope
- Tenant-owned data: leave types, balances (when owned by TeamBeats), requests, calendars, attendance, journeys, letters, claims.
- Organization scoping: policies, calendars, journeys and letter templates can be set per org subtree and location; HR officers are scoped to subtrees.
- Cross-tenant denial cases: balance or claim lookup by ID; letter verification code from another tenant; clock data import mapped to another tenant's person.
- Sibling, parent and child boundary cases: an HR officer of "Acme Gulf" cannot see "Acme Egypt" leave or claims; the team calendar shows only the viewer's permitted teams; a parent-scope HR officer sees children only with subtree scope.

## 8. Data, ownership and classification

| Entity | System of record | Classification | Retention | Personal data? |
|---|---|---|---|---|
| Leave request, balance | HR system if connected, else product | Confidential | 7 years default | Yes |
| Sick leave document | Product (object storage) | Restricted | 2 years default | Yes (health data) |
| Attendance record | Time system or product | Confidential | 5 years default | Yes, location if enabled |
| Journey and tasks | Product | Internal | 3 years after completion | Yes |
| Exit interview | Product | Confidential | 5 years | Yes |
| HR letter | Product | Confidential | 7 years | Yes |
| Expense claim and receipts | Product, then ERP | Confidential | 10 years default (tax) | Yes |

Location data from sign in is collected only if the tenant enables it, shown to the employee when collected, and kept as distance from the allowed point, not raw coordinates, unless the tenant requires raw coordinates.

## 9. Package entitlements

| Capability key | Workplace | Workplace Plus | Enterprise & Government |
|---|---|---|---|
| `hr.leave` | on | on | on |
| `hr.attendance` | off | on | on |
| `hr.journeys` (onboarding, offboarding, transfer) | off | on | on |
| `hr.letters` | on | on | on |
| `hr.data_change` | off | on | on |
| `hr.expenses` | off | on | on |
| `ai.receipt_reading` (counts against AI quota) | off | on | on |

## 10. UX, localization and accessibility
- Leave and attendance calendars support Gregorian with optional Hijri display (spec 007), and week start per location.
- Team calendar works by keyboard and is readable by screen readers as a table, not only as colored bars.
- Arabic and English letters are generated with correct right-to-left layout and Arabic numerals as the tenant set.
- Mobile first for sign in, leave request, approvals and expense receipts.

## 11. AI behavior and data policy
- Receipt reading and letter drafting go through the AI Gateway (spec 013) with the document's classification. Receipts are Confidential by default; external providers need tenant approval.
- The assistant can answer "how many leave days do I have" by calling the leave service with the user's own permissions through spec 014 and spec 020 tools; it cannot submit leave without the user's confirmation (constitution X).
- Health documents (Restricted) are never sent to AI.

## 12. Integrations and events
- Publishes: `WorkingCalendarChanged` (from R1), `LeaveApproved`, `LeaveCancelled`, `AttendanceCorrected`, `JourneyStarted`, `JourneyStepOverdue`, `JourneyCompleted`, `ExpenseApproved`, `LetterIssued`.
- Consumes: `UserProvisioned`, `UserDeprovisioned`, HR new hire, transfer and termination events (spec 019), resignation approval (spec 016), clock data imports (spec 017).
- Writes to HR (spec 019): leave, data changes. Writes to ERP (spec 017): expenses.
- Calendar export of approved leave to Microsoft 365 (spec 018) as out-of-office events, if the tenant enables it.

## 13. Audit, telemetry and analytics

| Event | Type | Key fields |
|---|---|---|
| hr.leave_submitted / approved / rejected / cancelled / on_behalf | audit | request, type, days, actor |
| hr.balance_adjusted | audit | person, type, delta, reason, actor |
| hr.calendar_changed | audit | location, dates, actor |
| hr.attendance_corrected | audit | person, date, before, after, approver |
| hr.journey_started / step_completed / cancelled | audit | journey, person, step, actor |
| hr.access_removal_confirmed / escalated | audit | person, system, actor |
| hr.data_change_approved / written_back | audit | person, field names (no values), actor |
| hr.letter_issued / verified | audit | letter type, reference, actor |
| hr.expense_submitted / approved / handed_off | audit | claim, total, currency, actor |
| hr.request_cycle_time | analytics | service, unit, duration |

## 14. Security and abuse cases
- Balance tampering: balances change only through approved requests, accrual jobs and audited HR adjustments.
- Bank detail change fraud: document required, all-channel notification, HR approval, optional cool-off before use.
- Fake letters: verification code and page; templates editable only by HR officers.
- Location tracking misuse: off by default, shown to the employee, minimal data stored.
- Expense fraud: duplicate detection, receipt hash, policy limits, sampling for finance review.

## 15. Non-functional requirements
- Leave request page with balance and overlap loads p95 under 1 second.
- Clock data import of 50,000 punches completes within 10 minutes.
- Journey steps fire within 5 minutes of their scheduled time.

## 16. Success criteria
- **SC-001**: An employee submits a leave request with balance and overlap shown in under 1 minute on mobile.
- **SC-002**: In a pilot tenant, 95% of new joiners have every day-one IT and facilities task done before day one.
- **SC-003**: For leavers, TeamBeats access ends within one directory sync interval plus processing of the directory disabling the account (35 minutes with the default 30 minute schedule, spec 003 SC-003), or at once when HR or a tenant admin disables the membership in TeamBeats, and every connected system's access removal is confirmed within 1 working day.
- **SC-004**: A salary certificate without approval is issued in under 2 minutes.

## 17. Open questions
- [NEEDS CLARIFICATION: is pre-boarding before the account exists required?] Default: yes, R2, tenant setting.
- [NEEDS CLARIFICATION: public letter verification page acceptable?] Default: on, tenant can turn off.
- [NEEDS CLARIFICATION: should TeamBeats keep leave balances itself for tenants without an HR system?] Default: yes.
- [NEEDS CLARIFICATION: is performance management in scope?] Default: no, linked app.

## 18. Traceability

| Requirement ID | Covered by |
|---|---|
| INT-007 | FR-005, FR-006, FR-019 |
| UC-08 | FR-026, US1, US8 |
| UC-09 | FR-006, FR-014, US4, US5 |

## Review checklist
- [x] No implementation details
- [x] Every FR is testable and traces to a requirement ID or is marked new
- [x] Cross-tenant and org boundary cases are listed
- [x] Entitlement behavior is defined
- [x] Audit events are defined
- [ ] No open [NEEDS CLARIFICATION] items that change scope
