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
- HR adjustments have their API and audit; their page comes with slice 2 (balances from the HR connection).
- People profile "out of office" reads `ILeave.AvailabilityAsync`; wiring it into the profile is left to spec 010's next change.

Slices 2 to 6 get their data model, API and tests here when each starts.
