# Implementation Plan: Leave, Attendance and HR Workflows

**Spec:** `specs/022-hr-workflows/spec.md` (v1.0, Approved) | **Date:** 2026-11-07
**Shared base:** `docs/architecture/r0-solution-structure.md`, ADR-0013 (ports inside modules), module map (`Hr`)

## Summary
R1 builds only User Story 2 and FR-007: holiday and working week calendars, and the working-day contract that due times (spec 016) and timers (spec 024) call. R2 builds the rest of the spec: leave, reduced hours, team availability, attendance, onboarding and offboarding journeys, employee data change, HR letters and expense claims. The R2 plan is at the end of this file. Probation and contract milestones, shift rosters and AI receipt reading for all tenants stay in R3.

R1 slices, in order:
1. **Backend:** the `Hr` module with calendars, the `IWorkingCalendar` contract, endpoints and the `WorkingCalendarChanged` event.
2. **Admin page:** Calendars under the admin shell, in English and Arabic, RTL.

## Constitution check
| Principle | Pass | Evidence |
|---|---|---|
| I, II tenant isolation | Yes | All tables tenant-owned with forced RLS; calendar assignment is to an org unit of the same tenant |
| III authorization | Yes | `hr.calendar_manage` checked through `IAuthorizationService` at the unit's scope; a scoped HR officer manages calendars of their subtree only |
| XXI entitlements | Yes | FR-007: calendars have no capability key, they are in every package |
| XIV modules | Yes | `Hr` owns calendars; specs 016 and 024 call `IWorkingCalendar`, never the tables |
| XXIX configuration | Yes | Calendars are data with validation, versioned through audit history |

## Where a calendar applies (spec gap, smallest safe default)
The spec says "per country and location", but the hierarchy of spec 002 has no location entity. Default: a calendar is assigned to an org unit and applies to that unit's subtree; the nearest assigned ancestor wins. A person's calendar is the one that applies to their primary membership unit (spec 002). With no assignment, the tenant calendar applies (Monday to Friday, no holidays, US2 scenario 4). A country is a label on the calendar, not a separate entity. Recorded in the R1 implementation notes; a location entity, if spec 010 or 022 adds one later, maps onto the same assignment.

## Data model (schema `hr`)
- `working_calendar`: `tenant_id`, `id`, `name` (localized jsonb), `country` (ISO 3166 alpha-2, optional), `week_days smallint[]` (ISO day numbers), `week_start`, `is_tenant_default`, `version`, timestamps. Exactly one tenant default per tenant (partial unique index).
- `calendar_holiday`: `tenant_id`, `calendar_id`, `date`, `name` (localized), `half_day bool`. Unique per calendar and date.
- `calendar_assignment`: `tenant_id`, `org_unit_id` (unique), `calendar_id`.
- Seed on first use: a tenant default calendar Monday to Friday.

## Contract (`Hr.Contracts`)
- `IWorkingCalendar.ForUnitAsync(orgUnitId)` and `ForUserAsync(userId)` return a resolved calendar snapshot.
- `IsWorkingDay(date)`, `CountWorkingDays(from, to)`, `AddWorkingDays(start, n)` (in the tenant time zone of spec 001), pure functions over the snapshot, unit tested.
- Snapshot cached in memory per tenant and calendar version; invalidated by `WorkingCalendarChanged`.

## API (v1)
- `GET/POST /api/v1/hr/calendars`, `GET/PUT/DELETE /api/v1/hr/calendars/{id}` with `If-Match` versions.
- `PUT /api/v1/hr/calendars/{id}/holidays` (replace the holiday list for a year), `PUT /api/v1/hr/assignments/{orgUnitId}`.
- `GET /api/v1/hr/working-days?orgUnitId=&from=&to=` for admin preview.
- Validation keys `validation.calendar.*`; the default calendar cannot be deleted; a calendar in use cannot be deleted until unassigned.

## Events and audit
- `WorkingCalendarChanged(calendarId, affectedDates)` on any change. Specs 016 and 024 recalculate open due times and timers from the next evaluation (US2 scenario 2).
- Audit `hr.calendar_changed` with calendar, dates and actor.

## UX
- Admin shell section "Calendars": list, edit form (name in each enabled locale, country, working week picker, holiday table with add, edit and remove), assignment to units with the org tree picker, and a working-day preview. Gregorian dates, optional Hijri display per spec 007. Keyboard and screen reader use as a table.

## Tests
- Unit: working-day arithmetic across weekends, holidays, half days, Sunday to Thursday weeks, year ends, time zones.
- Integration: US2 scenarios 1, 2 (event published, R1 part) and 4; nearest assigned ancestor wins; HR officer of a sibling unit is denied; cross-tenant calendar and unit IDs are not found; audit asserted.
- Acceptance (e2e): create a calendar, add holidays and assign it, in English and Arabic, with no critical axe findings.

# R2 plan

**Date:** 2026-11-11. Builds on R1 (calendars), spec 019 (HR records and lifecycle events), spec 016 (catalogue, module services, task inbox, delegation), spec 024 (process engine) and spec 025 (attestations).

## R2 slices, in order
1. **Leave** (US1 scenarios 1 to 7, FR-001 to FR-005, FR-009): leave types and policies, a balance ledger kept by TeamBeats, accrual and carry-over, requests with live balance and day count, overlap, replacement, approval on the engine with tasks in the inbox, cancel and shorten, the team availability view, and the Leave module service in the catalogue.
2. **HR balances, recount and reduced hours** (US1 scenario 8, FR-006, FR-008, US2 scenario 3; with spec 019 US7 and T013): balances read from HR when HR owns them, approved leave sent to HR with a retry queue, recount of approved leave when holidays change, and reduced hours periods (Ramadan) on calendars.
3. **Journeys** (US4, US5, FR-013 to FR-018): templates with steps relative to an anchor date, started by spec 019 and spec 003 events or an approved resignation, rescheduling, attestation steps (spec 025), the access removal checklist with escalation, exit interviews, the journeys dashboard, and pre-boarding behind a tenant setting.
4. **Attendance** (US3, FR-010 to FR-012): sign in and out with an optional location check, clock data from spec 017 imports, working hours rules, exceptions and corrections, compensation days and the monthly summary with export.
5. **Letters and data change** (US6, US7, FR-019 to FR-022): letter templates, generated bilingual PDFs with a reference and verification code, the public verification page, and data change requests written back to HR.
6. **Expenses** (US8, FR-023 to FR-025): claims with lines and receipts, policy limits, currency conversion, duplicate detection, routes by amount and hand-off to finance through spec 017. AI receipt reading (FR-024) is built here behind `ai.receipt_reading` through the AI Gateway.

Each slice is one pull request with its tests, e2e and implementation notes.

## Constitution check (R2)
| Principle | Pass | Evidence |
|---|---|---|
| I, II tenant isolation | Yes | Every new table is tenant-owned with forced RLS; IDs from another tenant are not found |
| III authorization | Yes | One new permission, `hr.manage`, for HR officers at org scope. Approvals use the line manager candidate rule of spec 024, and nobody approves their own request (section 6) |
| XXI entitlements | Yes | `hr.leave`, `hr.attendance`, `hr.journeys`, `hr.letters`, `hr.data_change`, `hr.expenses`, `ai.receipt_reading` checked through the entitlement service |
| XIV modules | Yes | `Hr` owns leave, attendance, journeys, letters and claims. It reads people through People and Identity contracts, units through Tenancy, HR data through `IHrRecords`, and starts approvals through `IProcessEngine` with an `ITaskSource` |
| X AI | Yes | Receipt reading goes through the AI Gateway and only suggests values; health documents never go to AI |
| XXIX configuration | Yes | Leave types, policies, journey templates and letter templates are versioned, validated configuration |

## Gaps and smallest safe defaults (R2)
- **Location**: as in R1, a calendar and a leave policy apply to an org unit subtree; the nearest assigned ancestor wins.
- **Balances owner**: TeamBeats keeps balances (open question default) unless the tenant's HR connection owns them (slice 2).
- **Accrual**: monthly, on the first day of the month in the tenant time zone, `entitlement / 12` per month; a yearly grant is the other choice. Service years and grade rules pick the entitlement from the first matching band.
- **Carry-over**: at the start of the leave year (1 January unless the type sets another month), the unused balance up to the type's carry-over limit moves to the new year; the rest lapses, in the ledger.
- **Leave year of a request**: each day counts against the year it falls in (edge case "across a year end").
- **Half days**: a request may start or end on a half day; hours only when the type allows hours.
- **Sensitive types**: shown to colleagues as "out of office" only (section 6); the approver and HR officers see the type.
- **Replacement**: an accept task in the inbox; a decline notifies the requester, who can name someone else. Approval does not wait for the replacement.
- **Profile status**: People shows "out of office" from `LeaveApproved` and `LeaveCancelled` (spec 010 profile status), dates only.
- **Pre-boarding**: on, behind a tenant setting, off by default until the HR officer turns it on (open question default "yes, tenant setting").
- **Letter verification**: on by default, the tenant can turn it off (open question default).
- **Performance management**: not planned (open question default).

## Data model (schema `hr`, slice 1)
- `leave_type`: id, key, names (localized), unit (`day` or `hour`), half days allowed, paid, sensitive, needs document after N days, notice days, negative allowed and its maximum, eligibility (membership types, gender when enabled), carry-over limit, year start month, cancel needs approval, active, version.
- `leave_policy`: id, leave type, org unit (null for the tenant), entitlement bands (by service years and grade), accrual (`monthly` or `yearly`), version. The nearest ancestor policy wins, like calendars.
- `leave_ledger`: one row per change to a person's balance: person, type, leave year, delta, reason (`accrual`, `carry_over`, `lapse`, `request`, `cancel`, `adjustment`, `recount`), request, actor, at. The balance is the sum; it is never stored on its own (section 14, balance tampering).
- `leave_request`: id, number, person, filed by, type, start, end, start half, end half, days, status (`pending`, `approved`, `rejected`, `cancelled`, `withdrawn`), replacement and its answer, document attachment, process instance, comment, timestamps. Days are kept per date in `leave_request_day` (date, fraction, leave year) so a recount and a team view read the same days.

## Contracts (`Hr.Contracts`, slice 1)
- `ILeave.BalancesAsync(userId)` and `AvailabilityAsync(userIds, from, to)` (out of office dates, the type only when the viewer may see it), for the assistant (spec 014) and profiles (spec 010).
- Events `LeaveApproved(RequestId, UserId, From, To)` and `LeaveCancelled(RequestId, UserId, From, To)`; IDs and dates only.

## API (v1, slice 1)
- `GET /api/v1/hr/leave/types`, admin `PUT /api/v1/hr/leave/types/{key}`, `PUT /api/v1/hr/leave/policies/{id}` with `If-Match`.
- `GET /api/v1/me/leave` (balances and requests), `POST /api/v1/hr/leave/preview` (days, balance after, overlap), `POST /api/v1/hr/leave/requests` (idempotency key), `POST .../{id}:cancel`, `POST .../{id}:shorten`, `POST .../{id}/replacement` (accept or decline).
- `GET /api/v1/hr/leave/team?from=&to=` (team availability by the viewer's teams and units).
- Admin `POST /api/v1/hr/leave/adjustments` (audited HR adjustment).

## Approval
A leave request starts an instance of the process key `hr.leave` on spec 024 with one line manager step (`ExcludeStarter`). The Leave module is an `ITaskSource`, so the approval shows in the task inbox with the team calendar link; delegation of spec 016 applies. The request's org unit is the person's primary unit.

## Jobs
- Accrual and carry-over run in the connector-style tenant tick once a day, idempotent per person, type and month (a unique ledger key).

## Audit
`hr.leave_submitted`, `hr.leave_approved`, `hr.leave_rejected`, `hr.leave_cancelled`, `hr.leave_on_behalf`, `hr.balance_adjusted`, `hr.leave_type_changed`, `hr.leave_policy_changed`.

## UX (slice 1)
- Leave page (`/leave`): balances by type, request form with the live day count, balance after and overlap names, my requests with cancel and shorten.
- Team availability (`/leave/team`): a table by person and day, keyboard and screen reader friendly, sensitive types as "out of office".
- Admin Leave section: leave types and policies.

## Tests (slice 1)
- Unit: day count with half days, holidays and week patterns; accrual and carry-over; eligibility; balance from the ledger.
- Integration: US1 scenarios 1 to 7; overlapping own request refused; year-end split; sibling HR officer denied; another tenant's request and type not found; plan without `hr.leave` refused; nobody approves their own request; audit asserted.
- e2e: request leave with balance and overlap, approve from the inbox, in English and Arabic with axe.

## Implementation notes (slice 1)
- The accrual and carry-over job runs every hour per active tenant (`LeaveMaintenanceWorker`); each period writes once per person and type, so the extra runs write nothing.
- Leave is a module service (`hr.leave`, route `/leave`) in the services catalogue, shown only when the plan has `hr.leave`. Module service registrations now carry an optional capability key.
- The preview returns each problem as a key with the message in the caller's language, so the page shows it as is.
- An hour type takes one day per request, in quarter hours; against the day count it weighs 8 hours a day.
- Eligibility is by membership type only; gender and religion rules wait for a spec decision. A person needs no org unit to take leave; without one the tenant policy applies.
- Replacement asks are personal tasks (`leave_replacement`) besides the request page. Cancelling or shortening approved leave starts a second approval (`leave_change`) when the type says so.
- HR adjustments have their API and audit; their page on /admin/leave came with slice 2 (pick a person, see the ledger, add or remove days with a reason).
- People profile "out of office" reads `ILeave.AvailabilityAsync`; wiring it into the profile is left to spec 010's next change.

## Slice 2: HR balances, recount and reduced hours
- Data: `hr.calendar_period` (name in each language, start, end, hours a day; at most 50 per calendar, under 100 days each, no overlap). `hr.leave_request` gains `hr_state` (queued, recorded, failed, unavailable), `hr_reference` and `hr_changes`. In the Integrations module: `integrations.hr_leave_setting` (source, allow stale submit, version), `integrations.hr_leave_balance` (last known balance per person and type, with the time read) and `integrations.hr_leave_handoff` (one row per request and change).
- Contracts: `WorkingCalendarSnapshot.ReducedHours(date)` and `Periods`; `IHrLeave` in `Integrations.Contracts` (setting, balances, hand-off) and the `HrLeaveHandOffChanged` event.
- API: `PUT /api/v1/hr/calendars/{id}/periods` (If-Match); `GET` and `PUT /api/v1/hr/integration/leave-source` (If-Match, `hr.integration_manage`, plan feature `hr.integration`); `GET /api/v1/hr/integration/leave-handoffs` and `POST .../leave-handoffs/{id}:retry`. The working days preview gives each day's reduced hours. Balances and the preview say whether the balance came from HR and when it was read.
- HR as the source: the HR REST pack reads one person's balances (HR codes mapped back to TeamBeats type keys) and takes approved leave, cancellations and changes as a POST, with a stable idempotency header per change. HR's balance is one figure per type; TeamBeats takes off pending requests and approved ones HR has not recorded. While HR owns balances the ledger is not written, accrual stops and adjustments are refused. The last known balance is kept with its time; when HR is down a request goes in on it only if the tenant allows that. Smallest safe default: it does not.
- Hand-offs are durable connector jobs. HR down or no usable connection: tried again every hour for three days from the hand-off, then failed. A refusal fails at once. Failed hand-offs show on the HR integration page and an HR administrator sends one again (audited `hr.leave_handoff_retried`). A cancellation or change goes to HR only for leave HR was told about.
- Recount (FR-008): every `WorkingCalendarChanged` recounts open day-unit requests that touch the changed dates, or, for a week or assignment change, those still to end for the people concerned (everyone for the tenant default). Changed days replace the request days; approved leave gets a `recount` ledger row per leave year for the difference (or a change hand-off when HR owns balances), is audited `hr.leave_recounted` and the person is told. At most 2000 requests per change.
- Reduced hours apply to working days only; attendance (slice 6) reads them. Leave counts are by day, so reduced hours do not change a leave count.
- Tests: periods (validation, version, snapshot, preview, permission), recount (holiday added and removed, ledger, pending request, notification), HR source (live balance, stale balance and the setting, not matched, hand-off recorded, cancelled, refused and sent again, three day expiry, no ledger rows, adjustment and accrual refused, permission, plan and other tenant).

## Slice 3: Journeys (T018, T019)
- Data: `hr.journey_template` (key, kind onboarding, offboarding, transfer or promotion, names, optional org unit whose subtree it serves, start trigger, steps as validated JSON, version), `hr.journey` (template and version, person, buddy, unit, anchor date, status active, completed or cancelled, the event that started it) with one active journey per template and person, and `hr.journey_step` (key, name, offset in days from the anchor, assignee, due working days, scheduled date, status scheduled, open, done or cancelled, the engine instance).
- Starts: `EmployeeHired` and `EmployeeRehired` (spec 019) start onboarding from a template started by hires, with the HR start date as the anchor; a hire with no account yet waits for the account. `UserProvisioned` (spec 003) starts onboarding from a template started by new accounts (anchored on the HR hire date, else today) and, when HR knows the hire date, from one started by hires. A new account whose anchor is more than 30 days ago starts nothing, so a directory's first sync of existing staff starts no journeys (smallest safe default; the spec is silent). `EmployeeTerminated` starts offboarding with the last working day; an approved request of the service a template names (spec 016 `RequestCompleted`, read through a new `IRequestFacts` contract of Services) starts offboarding with the date in the form field the template names. `UserDeprovisioned` starts nothing: the account is disabled at the end of offboarding (US5 scenario 3). A template applies when its unit is the person's primary unit or an ancestor; the nearest wins, and only one active template per start, unit and service is allowed. A repeated event changes nothing (unique per template and source event); an event for a person who already has an active journey of that template moves its anchor, so every step not yet open is rescheduled (US4 scenario 2). A re-hire's journey links the earlier one.
- Run on spec 024: each template version publishes one single step process per step, `hr.journey.<template>.<version>.<step>` (source `journey_route`), so a journey keeps the routes of the version it started with. The journey worker (every two minutes, `Hr:Journeys:InProcess` in the development host) and every start or move open steps whose date has come, business key journey and step. Assignees: the person or the buddy (a new `CandidateRule.Subject` flag: the instance's subject, given as `requestedFor`), the line manager, named people, or holders of a permission at the person's unit (a queue). Person, buddy and manager steps fall back to the step's permission, `hr.manage` when not given, so a step of a person who left, or a buddy step with no buddy, goes to HR. The person is excluded from every step that is not theirs. Due working days give reminders and overdue escalation as for any task, and `JourneyStepOverdue` is published. Approve marks the step done, reject or return marks it not needed; the journey completes when no step is left. Tasks show in the inbox through an `ITaskSource` with the journey link, and the assignee is notified (queue steps are found in the inbox).
- Cancel: HR cancels a journey with a reason, which withdraws open steps and cancels the rest (audited). The engine lets the user a module authorized withdraw an instance the system started. A no-show cancels onboarding and, when an offboarding template started by termination (else a manual one) applies, starts it anchored today (edge case). HR also moves the anchor and names the buddy.
- Dashboard and pages: HR officers covering the person's unit see journeys with progress, open and overdue steps on `/admin/journeys`, next to the templates. `/hr/journeys/{id}` shows the steps to the person, the buddy, anyone with a task in it and HR; a waiting step is marked done there. `/hr/journeys` lists the person's own journeys. Audit: `hr.journey_template_changed`, `hr.journey_started`, `hr.journey_rescheduled`, `hr.journey_step_completed`, `hr.journey_cancelled`.
- T019 step types: a template step has a type, `task` (the default), `attestation`, `access_removal` or `exit_interview`. Every typed step still runs on the engine, so due dates, overdue escalation and `JourneyStepOverdue` work as for any task.
  - Attestation (US4 scenario 4, FR-015 with spec 025 FR-017): the person does it, and only the person. Spec 025 gets an "onboarding required" flag on the register entry and `policies.obligation`, the person's own attestation task outside any campaign, asked for through a new `IPolicyObligations` contract with the journey step as the source. When the step opens, every published flagged policy that applies to the person and whose version in force they have not attested to becomes an obligation due with the step (five working days when the step has none). The obligations show in the inbox and on My policies like campaign attestations, so the step's own task stays out of the inbox and cannot be decided. One attestation to the version in force settles every obligation for the policy. `PolicyAttested` and the worker close the step when nothing is left (a retired policy no longer counts); with nothing to attest to the step closes as not needed; a cancelled journey cancels the obligations.
  - Access removal (US5 scenario 4, FR-017): never done by the person or the buddy. When the step opens it gets one line per enabled connection that is not disabled (a new `IConnectedSystems` contract of Integrations) and one per system the template names (up to 20, for systems that are not connected). Whoever holds the task confirms each line as removed or not applicable, with an optional note (audited `hr.access_removal_confirmed`); the last line completes the task and the step. A decision from the inbox while a line is pending is refused. Lines still pending once the first working day after the step opened has ended (the person's unit calendar) go once to the tenant holders of `security.manage` and `integration.operate` (`hr.access_removal_escalated`, audited).
  - Exit interview (US5 scenario 5, FR-018): offboarding only, by the leaver or whoever the step names (HR officers through the fallback). The form records one to five reasons from a fixed list, an optional main reason, a rating from 1 to 5, whether they would come back and a comment of up to 2,000 characters, or that the person declined (the step then closes as not needed). The comment reaches HR officers covering the person only; the leaver and whoever recorded it see the structured answers; the audit log records that a comment exists, never its text. `GET /api/v1/hr/exit-reasons` counts reasons for the units the officer covers over a period (twelve months by default, at most three years), with the minimum group size of spec 021 (5): smaller units are merged into "other", and when that is still too small it is left out and counted as hidden. Spec 021 dashboards can read these counts when they are built.
- Pre-boarding (US4 scenario 3, FR-016) follows in its own change behind a tenant setting.
- Learning assignments (FR-015 second half) wait for spec 028; a template step can be a manual task until then.

Slices 4 to 6 get their data model, API and tests here when each starts.
