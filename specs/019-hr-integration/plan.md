# Implementation Plan: HR System Integration and Payslips

**Spec:** `specs/019-hr-integration/spec.md` (v1.0, Approved) | **Date:** 2026-11-07
**Shared base:** spec 017 plan (connector runtime), ADR-0013, ADR-0006 (outbox), spec 010 plan (profile fields), spec 022 plan (journeys)

## Summary
An HR capability contract in `Integrations.Contracts` (`IHrDirectory`: employees, changes, payslips) is implemented by the HR REST and SFTP packs on the spec 017 runtime. A `HrSync` use case in `Integrations` matches records, holds future-dated changes and publishes lifecycle events through the outbox. `People` (010) and `Organization` (002) consume the events to update profiles, reporting lines and memberships; `Identity` (003) disables leavers; `Workflows` (022) starts journeys. Payslips are read live through the port and streamed, never stored.

## Constitution check
| Principle | Pass | Evidence |
|---|---|---|
| I tenant isolation | Yes | Match links, held changes and runs tenant-owned with forced RLS; tenant from the session for payslips |
| III authorization | Yes | `hr.integration_manage`, `people.hr_fields.read` through `IAuthorizationService`; payslips self only |
| V entitlements | Yes | Section 9 keys through `IEntitlementService` |
| XVII connectors | Yes | HR DTOs stay in the pack; canonical contract only |
| Rule 5 module tables | Yes | Other modules react to events, never read `integrations` tables |

## Data model (schema `integrations`)
- `hr_match` (user, HR record ID, rule, linked by), `hr_run` (counts, status, threshold result), `hr_held_change` (employee, change type, effective date, payload hash, status), `hr_org_code` (code, unit, status).

## Key decisions
- Field ownership is a profile field attribute in 010 (`source = hr`), set by the event consumer, so the UI does not need HR knowledge.
- Effective-dated changes run from a daily job at 00:05 tenant time plus a check on each event.
- Payslip PDFs stream through the API with a watermark added by the existing PDF library used for document previews; no new dependency.
- The change threshold check runs before the apply phase as a dry run that counts terminations and moves.
- Leave (US7, T013): `IHrLeave` in `Integrations.Contracts` gives the leave balance source setting (`integrations.hr_leave_setting`, versioned, audited), a person's balances read live through the HR REST pack with the last known kept in `integrations.hr_leave_balance`, and hand-offs of approved leave, cancellations and changes as durable `submit_leave` connector jobs in `integrations.hr_leave_handoff`. An unanswered hand-off is sent again every hour for three days, then failed; the HR integration page lists hand-offs and sends a failed one again. The `Hr` module reads only this contract and the `HrLeaveHandOffChanged` event. See the spec 022 plan, slice 2.

## Tests
Fake HR system in `DevFakes` with matching, conflict, manager loop, future-dated and mass termination fixtures; cross-tenant match denial; payslip access by manager and administrator denied; prefill tampering refused; connector failure paths.
