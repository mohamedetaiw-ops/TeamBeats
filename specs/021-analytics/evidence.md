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

# Spec 021 evidence (R2)

R2 builds the analytics workspace and its dashboards, scheduled reports, export, sentiment, AI usage and AI summaries (T010 to T019). Defaults, deviations and gaps are in the spec 021 sections of `docs/architecture/r2-implementation-notes.md`. The spec stays Approved, not Implemented: R3 adds KPI tiles, benchmarks, the analytics API and experiments, and the R2 gaps below remain.

| Requirement | Status | Evidence |
|---|---|---|
| FR-002 advanced dashboards | Met | Campaigns, services and workflows, policy compliance, AI usage, search, knowledge, engagement, HR, security trends, location and unit, experience and sentiment are workspace sections under `analytics.advanced` or their own key (`CampaignAnalyticsTests`, `ServiceAnalyticsTests`, `ComplianceAnalyticsTests`, `AnalyticsAiTests`, `SearchGapsTests`, `AnalyticsAreaTests` (all four), `AnalyticsAreaMathTests`, `ScopeDashboardTests`, `ExperienceAnalyticsTests`, `SentimentTests`, `analytics-areas.spec.ts` in en and ar with axe). Search is by locale and unit, with query refinements (`SearchGapsTests.Searches_split_by_unit_and_refinements_five_people_share_are_counted`, `analytics-search.spec.ts`) |
| FR-006 filters and viewer scope | Met | `AnalyticsWorkspaceTests` (all four), `AnalyticsWorkspaceMergeTests` |
| FR-007 zero result and no click queries, five people, no personal data | Met | `SearchGapsTests`, `SearchQueryLogTests`, `analytics-search.spec.ts` (en and ar, axe) |
| FR-008 service volume, cycle time, steps, breaches, reasons, automation | Met | `ServiceAnalyticsTests`, `analytics-services.spec.ts` |
| FR-009 campaign funnel by channel and return on effort | Met | `CampaignAnalyticsTests`, `CampaignAnalyticsMergeTests`, `analytics-campaigns.spec.ts` |
| FR-010 location and unit dashboards that never reveal another single scope | Met for R2 (KPI tiles are R3) | `ScopeDashboardTests` (all three), `analytics-scopes.spec.ts` |
| FR-011 permissions per recipient at send time | Met | `AnalyticsReportTests.Schedule_to_a_unit_sends_each_member_what_they_may_see_skips_the_rest_and_tells_the_owner` |
| FR-012 exports with the same rules, in the background, audited | Met | `AnalyticsReportTests.Export_is_made_in_the_background_...`, `Export_is_refused_without_the_export_capability`, `ReportTablesTests`, `analytics-reports.spec.ts` |
| FR-013 sentiment on de-identified text, AI policy, no link to anonymous answers | Met | `SentimentTests` (all three), `SentimentMathTests`, `analytics-sentiment.spec.ts` |
| FR-014 retention | Met (R1) | `AnalyticsPipelineTests.Raw_events_past_the_tenants_retention_are_removed_and_aggregates_stay` |
| FR-016 one number for AI usage and quality | Met | `AnalyticsAiTests.Ai_usage_shows_the_gateway_numbers_and_hides_small_groups` compares with the spec 013 usage page; agent quality is the spec 034 gate result the Agent Hub shows |
| FR-019 to FR-021 experience, vitals and outcomes | Met for R2 | `ExperienceAnalyticsTests`, `ExperienceAnalyticsMathTests`, `analytics-experience.spec.ts` |
| FR-025 trends as charts with table alternatives | Met | Workspace line and bar charts with tables (`WorkspaceExplorer`), weekly charts on sentiment and AI usage; the a11y suite checks every analytics route |
| US2 to US9 R2 scenarios | Met except as noted | The tests above; US8 scenario 3 by `AnalyticsAiTests.A_summary_names_the_figures_it_used_and_follows_the_dashboard_access`, `AnalyticsAiMathTests` and `analytics-ai.spec.ts` |
| Section 7 cross-tenant cases | Met | Each R2 integration class reads from a second tenant and sees none of the first tenant's figures |

# Spec 021 evidence (R3)

| Requirement | Status | Evidence |
|---|---|---|
| FR-010 KPI tiles from connected systems with source and freshness | Met | `KpiTileTests.A_location_dashboard_shows_each_tiles_latest_reading_with_its_source_and_hides_tiles_the_viewer_may_not_see` |
| US6 scenario 2 tile hidden without permission to the data | Met | Same test (viewer without the tile's permission) |
| KPI tile settings versioned, checked, audited, package and tenant boundary | Met | `KpiTileTests.The_tile_set_is_checked_and_versioned_the_package_decides_and_nothing_crosses_tenants`, `analytics-kpi-tiles.spec.ts` (en, ar, axe) |
| FR-024 personalization and recommendations evaluated from outcomes, minimum group size, no individual scored | Met | `ExperienceAnalyticsTests.Recommendation_usefulness_counts_people_per_surface_and_hides_small_groups`, `AssistantSuggestionTests` (shown outcome), `analytics-experience.spec.ts` (en, ar, axe) |
| FR-022 experiments approved by a tenant administrator, consenting users only, stable scoped reversible allocation, no individual measured | Met | `ExperimentTests.An_approved_experiment_shows_the_variant_only_to_consenting_people_in_a_stable_arm_and_stopping_leaves_nothing`, `ExperimentTests.The_proposer_cannot_approve_their_own_and_the_package_and_tenant_decide`, `experiments.spec.ts` (en, ar, axe) |
