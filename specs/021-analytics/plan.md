# Implementation Plan: Analytics and Insights

**Spec:** `specs/021-analytics/spec.md` (v1.0, Approved) | **Date:** 2026-11-07
**Shared base:** ADR-0013, module map (`Analytics`, a derived read model), spec 032 plan (renderer telemetry, PR #63), spec 009 plan (reach and acknowledgement)

## Summary
R1 builds the analytics event pipeline with one schema and pseudonymous user keys (FR-003), the tenant analytics policy for nonessential events (FR-018), item-level reach and acknowledgement counts shown inside content (spec 009) and policies (spec 025) with the minimum group size (User Story 1, R1 counts and "remind this group"), adoption basics for tenant administrators (weekly active users, home personalization use, template adoption counts), and the R1 part of User Story 9: experience telemetry through the shared schema and frontend errors with release, component version and correlation ID. The analytics workspace, dashboards, scheduled reports, export, sentiment and experiments are R2 and R3.

## R3 plan
**Date:** 2026-11-25. R3 adds connected business KPIs on location dashboards, benchmarks across anonymized tenants with consent, the analytics API, governed experience experiments, and personalization and recommendation evaluation (header, target release R3). The first R3 slice is KPI tiles, built with spec 017 R3 on its KPI feed contract (FR-022); the other R3 items follow in the R3 order.

R3 slice 1, **KPI tiles (US6 scenario 2, FR-010):**
- The tile set is versioned tenant configuration (constitution XXIX) in `analytics.kpi_tile_set`: up to 12 tiles, each the metric name a KPI feed sends, a name in each language and an optional permission. People who manage tenant settings change it; changes are audited as `analytics.kpi_tile_configured` with the metrics and permissions before and after.
- A location dashboard gains `kpis`: for each tile the viewer may see, the latest reading for the location whose period starts before the end of the dashboard range, with its value, unit, period, source connection and the source and receipt times. Readings come only through `IKpiFeed` (Integrations.Contracts); Analytics reads no Integrations table.
- Under `analytics.kpi_tiles` (Enterprise and Government), already in the catalogue.

Defaults (spec silent):
- "Hidden from users without permission to that data" is a permission chosen per tile, which the viewer must hold for the whole tenant, on top of the location dashboard's own `analytics.view` for the whole tenant. A tile without one is shown to everyone who sees the dashboard.
- KPI values are business figures, not figures about people, so the minimum group size does not apply; no group average is shown, so no other location's figure is ever derived.
- Tiles are on location dashboards only, since KPI feeds carry a location code and not an organization unit.
- A tile without a reading says so rather than being left out, so the administrator can see that a feed is not arriving.

R3 slice 2, **personalization and recommendation usefulness (US10 scenario 3, FR-024):**
- One declared event, `recommendation.outcome` with `surface` (`search` for results ranked from use in 012, `assistant` for suggestions in 014, `learning` for course recommendations in 028) and `outcome` (`shown`, `opened`, `dismissed`). Owners send `shown` and `dismissed` from the server when something was shown or declined; the pages send `opened` when the person opens a ranked or suggested thing.
- The experience dashboard gains `recommendations`: per surface, the people something was shown to, and of them who opened or dismissed it and the opened rate. Counted by person, not event; an opening by someone who was not shown anything is not counted; under the minimum group size only the number of people shows.

Defaults (spec silent):
- Judged samples for search stay the judged query sets of spec 034 (R2), which run without personalization, so a ranking change is still judged the same way; personalization itself is judged from outcomes only.
- Quick navigation (008) is not a surface: it ranks as the person types, so "shown" would count keystrokes.
- No breakdown by unit or location, so a small group is never singled out within a surface.

R3 slice 3, **governed experience experiments (US9 scenario 8, FR-022):**
- Experiments live in the Experience module beside the page renderer (`experience.experiment`, `experience.experiment_consent`), under `analytics.experiments`. A page publisher for the page's scope proposes one: a name, an earlier release of the page as the variant, a share of 1 to 50 percent and optionally a unit inside the page's scope. A tenant administrator (`tenant.settings_manage`) other than the proposer approves it, which starts it; a page publisher or a tenant administrator stops it. Proposed, approved and stopped are audited (`experience.experiment_*`). One open experiment per page.
- People take part only after agreeing on their preferences page (`/api/v1/me/experiments`). For a running experiment, each consenting viewer in scope is in the variant when a SHA-256 of the experiment and person falls below the share, so the arm is stable and nothing about the person is stored. Shared kiosk devices never take part. The renderer sends `experiment.exposure` (experiment and arm, server only) and shows the variant release; stopping shows the current release to everyone at once.
- Results: `GET /api/v1/analytics/experiments/{id}` for whole tenant analytics viewers: per arm the people who saw it and, of them, who completed a task flow or opened an item afterwards, with the minimum group size. Page: Analytics > Experiments.

Defaults (spec silent):
- "Never on mandatory or critical content": pages hold no mandatory content of their own (items to acknowledge and policies are specs 009 and 025, critical alerts are in the shell), so an experiment only changes a page's arrangement and can never hide something a person must read.
- The variant is an earlier immutable release, so an experiment never shows content nobody published.
- Consent is opt in; the approver must differ from the proposer.

R3 slice 4, **analytics API for customer BI tools (section 12):**
- Public API routes `GET /api/public/v1/analytics/metrics` (catalogue and minimum group size) and `POST /api/public/v1/analytics/query` (the workspace query), under the delegated scope `analytics:read` and the capability `analytics.api`. The call runs as the person who consented, with their own `analytics.view` coverage, so units, the minimum group size and "other" merging are exactly the workspace's. Only weekly aggregates and breakdowns, never raw events or person keys.

Defaults (spec silent):
- The scope is delegated only and needs an administrator's consent; an application app cannot hold it, because analytics rights belong to a person's unit coverage.
- No separate export format: the answer is the workspace JSON, which BI tools read directly; CSV stays the in-app export.

R3 slice 5, **benchmarks across anonymized tenants with consent (section 7, open question default opt in only):**
- A tenant administrator (`tenant.settings_manage`) opts the tenant in on Analytics > Benchmarks (`GET/PUT /api/v1/analytics/benchmarks`, audited `analytics.benchmarks_changed`), under the new capability `analytics.benchmarks`. While it takes part, the analytics maintenance job gives one figure per metric for the last complete week, aggregated for the whole tenant before it leaves the tenant: the share of active members who were active (`weekly_active_share`). Tenants with fewer members than the minimum group size give nothing.
- Figures sit in `analytics.benchmark_contribution`, which the application role cannot read. They change only through the security definer functions `analytics.contribute_benchmark` and `analytics.withdraw_benchmarks` under the caller's tenant context, and are read only through `analytics.benchmark_figures`, which answers a tenant that takes part with the number of tenants and, from five tenants, the quartiles rounded to one decimal. No tenant is ever named or listed. Opting out deletes every figure the tenant gave.
- Viewers: whole tenant `analytics.view` holders and tenant administrators.

Defaults (spec silent):
- Give to get: only a tenant that takes part sees benchmarks.
- Five tenants at least before quartiles show, fixed in the database function so no setting lowers it; the count of tenants that take part is shown.
- One metric first (weekly active share), since it is size neutral and exists in every package; more metrics are added to the same list.
- Enterprise and Government only, like the other R3 analytics features.

### R3 tests
- Slice 5: nothing given or shown before opting in; employees refused; four tenants show the count and own figure without quartiles, five show quartiles; the figures table is unreadable for the application role; opting out removes the tenant's figure; the package decides; the choice is checked; the job gives once a week; the page in English and Arabic with axe.
- Slice 4: the API answer equals the workspace answer; an unknown metric is refused; a person without analytics rights, a token without the scope, an application app, an employee's own consent, another tenant and a package without the analytics API are all refused.
- Slice 3: proposal checks and permissions; approval only by another tenant administrator; only consenting people take part, each in the same arm every time; exposure counted once per person; results hide small arms and need analytics.view; stopping returns everyone to the current release; package and tenant boundaries; the page and preference in English and Arabic with axe.
- Slice 2: people counted once per surface, an opening without a showing ignored, unknown surfaces dropped, small groups hidden; the assistant and learning owners send `shown`; the dashboard section in English and Arabic with axe.
- The latest reading for the location with source and times; another location's reading never shows; a tile with a permission is left out for a viewer without it; the tile set is checked (metric once, names in both languages, a known permission, at most 12) and versioned; changes are audited; the package decides; another tenant sees nothing.

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
