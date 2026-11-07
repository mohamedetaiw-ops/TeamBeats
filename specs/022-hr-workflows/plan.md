# Implementation Plan: Leave, Attendance and HR Workflows

**Spec:** `specs/022-hr-workflows/spec.md` (v1.0, Approved) | **Date:** 2026-11-07
**Shared base:** `docs/architecture/r0-solution-structure.md`, ADR-0013 (ports inside modules), module map (`Hr`)

## Summary
R1 builds only User Story 2 and FR-007: holiday and working week calendars, and the working-day contract that due times (spec 016) and timers (spec 024) call. Leave, attendance, journeys, letters and expenses are R2 and are not planned here.

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
