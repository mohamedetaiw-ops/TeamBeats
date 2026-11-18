# Tasks: Analytics and Insights

Depends on 004, 006, 008, 009, 025, 032; 010 (location breakdown), 015 (reminders). R1 covers the event pipeline, item counts (User Story 1, R1 part), adoption basics and the R1 part of User Story 9.

## Phase 1: Pipeline
- [x] T001 `Analytics` and `Analytics.Contracts` projects; migration for events (monthly partitions), daily and weekly aggregates and the tenant analytics policy (forced RLS)
- [x] T002 Permission `analytics.view`; audit `analytics.policy_changed`
- [x] T003 [P] Tests: per-tenant pseudonymous keys; declared schemas only; nonessential off; retention job
- [x] T004 Event contract, web batch endpoint, aggregation job

## Phase 2: User Story 1, item reach and acknowledgement (P1, R1 counts)
- [x] T005 [P] [US1] Tests: counts and trend; minimum group size merged into "other"; remind this group only reaches non-readers; rights checked through the owning module
- [x] T006 [US1] Item analytics endpoint and the analytics panel on content and policy pages

## Phase 3: Adoption and experience quality
- [x] T007 Adoption basics endpoint and admin page with table views
- [x] T008 [US9] Frontend error reports with release, component version and correlation ID; support list

## Final phase
- [x] T009 OpenAPI; R1 implementation notes (location breakdown); spec evidence

## R2: Analytics workspace

R2 covers User Stories 1 (dashboard), 2, 4 to 8 and 9 (dashboards). Search gaps (US3) and judged search sets (US10 scenarios 1 and 2) are already built in spec 012 R2 and spec 034; the workspace links to them rather than building them again. Every dashboard reads module figures through their contracts (`IAnalyticsItemSource` and new per-module metric contracts), applies the minimum group size and the filters of FR-006, and is gated by `analytics.advanced` where FR-002 says so.

- [x] T010 Analytics workspace shell: `/analytics` with a section per dashboard the viewer may open (`analytics.view` at the scope), the shared filter bar (date, unit with subtree, location, channel, locale, membership type, site, content type) and the chart rules of FR-025; one metric query endpoint with the minimum group size and complementary suppression; tests for scope, minimum group size and another tenant
- [x] T011 [US1] [US2] Communications and campaigns: reach and acknowledgement trend across items, campaign funnel per step and channel, return on effort from tenant estimates (versioned configuration), side-by-side comparison of two campaigns; metric contract from Content
- [x] T012 [US4] Services and workflows: requests per week, median and p90 cycle time, time per step, breaches, rejection reasons and automation rate from Services and Workflow contracts; slow steps highlighted with a link to the spec 024 heatmap for operators; structured reasons by unit with the minimum group size
- [x] T013 [US5] Policy compliance: attestation rate per active campaign by unit and location, overdue counts and trend to the deadline from the Policies contract; drill down opens the spec 025 attestation report, which keeps its own permissions
- [x] T014 [US6] Location and unit dashboards: adoption, reach, compliance, open requests and breaches, and attendance exceptions for one scope with a group average that never reveals another single scope; "not enough data" below the minimum group size; for location heads by scope of `analytics.view`
- [x] T015 [US9] Experience dashboards (FR-017 to FR-021): template usage, layout distribution and version adoption by scope (matching the spec 032 page inventory), personalization enablement against use and resets, p75 LCP, INP and CLS by device class, theme, organization, page type and release with variants that miss spec 032 targets flagged; the outcome event contract for task start, complete and abandon
- [x] T016 [US7] Scheduled reports and export (FR-011, FR-012): a schedule per dashboard and filter set, weekly or monthly, to the owner or a group; permissions evaluated per recipient at send time, recipients without permission skipped and the owner told; exports (CSV and XLSX) built in the background with the same aggregation, audited, refused without `analytics.export`
- [ ] T017 [US8] Sentiment and AI summaries (FR-013, FR-016): comment and survey free text classified through the AI Gateway only when the tenant enables it, aggregated with the minimum group size, never linked to an anonymous respondent, free text only with at least 5 responses; a dashboard summary labelled as AI-generated with the figures it used; AI usage figures read from spec 013 and quality from spec 034 so one number shows everywhere
- [ ] T018 Retention (FR-014): raw events kept 13 months by default, tenant setting from 3 to 25 months, purge job; aggregates kept
- [ ] T019 OpenAPI, R2 implementation notes, e2e with axe for the workspace, a schedule and an export in `ar` and `en`, spec evidence
