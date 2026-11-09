# Spec 021 evidence (R1)

The spec stays Approved, not Implemented, until its R2 and R3 parts are built: the analytics workspace and dashboards (FR-002, FR-006 to FR-010, FR-016, FR-020 dashboards, FR-023, FR-024), scheduled reports (FR-011), export (FR-012), sentiment (FR-013), KPI tiles, benchmarks, the analytics API and experiments (FR-022). R1 builds the event pipeline, item reach and acknowledgement inside content and policies, adoption basics and the frontend error list. Defaults, deviations and gaps are in the spec 021 sections of `docs/architecture/r1-implementation-notes.md`.

| Requirement | Status | Evidence |
|---|---|---|
| FR-001 core usage and content analytics in every package | Met for R1 | `analytics.core` in every package version at release (Entitlements 0015); `analytics.spec.ts` runs on `workplace`, `ItemAnalyticsTests` and `AdoptionTests` on `workplace_plus` |
| FR-003 one schema, tenant, pseudonymous key, unit and location at event time | Met | `AnalyticsPipelineTests.Only_declared_events_and_fields_are_kept_with_a_pseudonymous_key_and_the_unit_at_event_time`, `AnalyticsSchemaTests` |
| FR-004 minimum group size 5, never lower, individual analytics off | Met | `AnalyticsPipelineTests` (policy limits), `ItemAnalyticsTests` (groups merged into "other", complementary suppression), `ItemAnalyticsMergeTests`, `AdoptionCountsTests`, `AdoptionTests` (units and layout use under the minimum hidden) |
| FR-005 person-level lists only in owner reports | Met | Item analytics return counts only; "remind this group" passes people to the owner, which sends, and the author never sees them (`ItemAnalyticsTests.Item_reach_counts_readers_in_the_audience_merges_small_groups_and_reminds_only_non_readers`) |
| FR-014 raw retention 3 to 25 months, 13 by default, aggregates kept | Met | `AnalyticsPipelineTests` (retention job and policy limits) |
| FR-015 no productivity monitoring | Met | No declared event or field type can carry keystrokes, idle or screen time, or free text (`AnalyticsSchemaTests`, undeclared events dropped in `AnalyticsPipelineTests`) |
| FR-017 one schema covers page views, interactions and UI errors | Met for R1 events | `AnalyticsEvents.Schemas`: `content.viewed`, `page.viewed`, `search.queried`, `personalization.changed`, `task.outcome`, `ui.error` |
| FR-018 essential errors continue when nonessential analytics are off, without a key | Met | `AnalyticsPipelineTests.Turning_nonessential_analytics_off_stops_people_events_and_keeps_errors_without_a_key`, `AdoptionTests.Support_sees_frontend_errors_by_release_with_component_and_correlation_id_and_no_message` |
| FR-019 template usage and template version adoption | Met for the whole tenant | `AdoptionTests.Adoption_basics_count_active_people_by_unit_layout_use_and_template_pages_within_the_minimum_group_size`; by scope and layout distribution are R2 dashboards |
| FR-020 redacted client errors, failed component boundaries, release, correlation IDs | Met for the error list | `AdoptionTests.Support_sees_frontend_errors_...`, `analytics.spec.ts`; Core Web Vitals dashboards are R2 (the renderer already reports vitals, spec 032) |
| FR-021 outcome event contract | Met for the contract | `task.outcome` with flow, step and outcome start, complete or abandon in the declared schema; the outcome reports are R2 |
| US1 scenarios 1 to 3 | Met | `ItemAnalyticsTests` (all three), `content-authoring.spec.ts` and `policies.spec.ts` (en and ar, axe) |
| US9 scenario 6 | Met | `AdoptionTests.Support_sees_frontend_errors_...`, `analytics.spec.ts` (en and ar, axe) |
| SC-001 share read and acknowledged in under 30 seconds | Met | The item editor and the policy page link to the analytics page, which leads with audience, readers, reach and acknowledgement (`content-authoring.spec.ts`, `policies.spec.ts`) |
| SC-002 no group under the minimum in privacy tests | Met for R1 views | `ItemAnalyticsMergeTests`, `AdoptionCountsTests`, `ItemAnalyticsTests`, `AdoptionTests` |
| Section 7 cross-tenant cases | Met | Each test class reads from a second tenant: salts and keys differ, events, aggregates, items, adoption and errors of one tenant are never in another |
