# Implementation Plan: Analytics and Insights

**Spec:** `specs/021-analytics/spec.md` (v1.0, Approved) | **Date:** 2026-11-07
**Shared base:** ADR-0013, module map (`Analytics`, a derived read model), spec 032 plan (renderer telemetry, PR #63), spec 009 plan (reach and acknowledgement)

## Summary
R1 builds the analytics event pipeline with one schema and pseudonymous user keys (FR-003), the tenant analytics policy for nonessential events (FR-018), item-level reach and acknowledgement counts shown inside content (spec 009) and policies (spec 025) with the minimum group size (User Story 1, R1 counts and "remind this group"), adoption basics for tenant administrators (weekly active users, home personalization use, template adoption counts), and the R1 part of User Story 9: experience telemetry through the shared schema and frontend errors with release, component version and correlation ID. The analytics workspace, dashboards, scheduled reports, export, sentiment and experiments are R2 and R3.

## Constitution check
| Principle | Pass | Evidence |
|---|---|---|
| I tenant isolation | Yes | Events and aggregates tenant-owned with forced RLS; pseudonymous keys are per tenant (HMAC with a tenant salt) so keys never match across tenants |
| II org scope | Yes | Counts are filtered to what the viewer may see; breakdowns by unit follow the viewer's scope |
| III authorization | Yes | Item analytics need edit or publish rights on the item, checked by the owning module's contract; adoption needs `analytics.view` at tenant scope |
| Privacy | Yes | Minimum group size 5 merged into "other"; individual analytics off by default; nonessential events stop when the tenant turns them off |
| XIV derived model | Yes | `Analytics` owns only data rebuilt from events; owning modules keep their records (for example acknowledgements in `Content`) |

## Pipeline
- `IAnalyticsEvents.Emit(name, fields)` in `Analytics.Contracts` writes to an outbox-backed queue with tenant, pseudonymous user key (HMAC-SHA256 of user ID with a per-tenant secret from platform configuration), org unit at event time, channel, locale and fields from a registered event schema. Unknown fields are dropped and counted, as in the renderer telemetry endpoint.
- Web events (views, interactions) go through the existing `POST /api/v1/experience/telemetry` pattern: a generic `POST /api/v1/analytics/events` takes a batch of up to 20 declared events, tagged with the session tenant. Essential operational telemetry (errors, performance without user keys) stays on the OpenTelemetry meters.
- Tenant policy `analytics_policy`: nonessential on or off, retention of raw events (13 months default), individual analytics off by default.
- Raw events in `analytics.event` (partitioned by month), rolled into `analytics.item_daily` and `analytics.adoption_weekly` by a job.

## Item reach and acknowledgement (US1, R1)
- `content.viewed` events from the content page and feed; reach = unique readers over audience size (audience size from the `Content` contract).
- `GET /api/v1/analytics/items/{module}/{id}` returns audience size, unique readers, reach, acknowledgement percentage (from the `Content` or `Policies` contract), reactions and comments, a daily trend, and a breakdown by org unit with groups under 5 merged into "other". Location breakdown waits for the people profile (planned spec 010).
- "Remind this group" calls the owning module, which sends through notifications (planned spec 015) to people in the group who have not read or acknowledged; names are shown only where the acknowledgement report right exists (spec 009).

## Adoption basics
Tenant administrators see weekly active users, active users by unit (minimum group size), home personalization use (from spec 008 events), and pages per template and template version (from the spec 032 inventory). One admin page with table views.

## Frontend errors (US9 scenario 6)
The renderer already reports render failures (spec 032 T021). R1 adds a global error boundary report with release, component reference and version, correlation ID and redacted message, shown to support in an admin list filtered by release.

## Tests
- Integration: pseudonymous keys differ across tenants for the same person; unknown fields dropped; nonessential off stops view events and keeps errors; reach and acknowledgement counts with the 5 minimum; remind this group reaches only non-readers; viewers without rights on the item refused; adoption counts match the page inventory.
- Cross-tenant: events and aggregates of A never in B; event IDs of another tenant not found.
- Acceptance (e2e): item analytics panel and adoption page in English and Arabic with axe.
