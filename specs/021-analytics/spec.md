# Feature Specification: Analytics and Insights

| Field | Value |
|---|---|
| Spec ID | 021-analytics |
| Area | analytics |
| Status | Approved |
| Version | 1.0 |
| Owner | Mohamed |
| Approver | Mohamed |
| Target release | R1 (item-level reach and acknowledgement counts inside specs 009 and 025, adoption basics for tenant admins), R2 (analytics workspace: adoption, content, communications and campaigns, search, knowledge, services and workflows, policy compliance, AI usage, location and unit dashboards, scheduled reports, export, survey and comment sentiment, search relevance evaluation), R3 (connected business KPIs on location dashboards, benchmarks across anonymized tenants with consent, analytics API, governed experience experiments, personalization and recommendation evaluation) |
| Last updated | 2026-11-05 |
| Approved | 2026-11-05 by Mohamed |
| Requirement IDs | ANL-001 (was LOC-005), ANL-002 (was LOC-006), ANL-003 to ANL-024 (new), AI-013 (usage visibility, shared with spec 013), CX-MET-001 to CX-MET-008 |
| Use cases | UC-04, UC-08, UC-13 (usage figures only) |
| Depends on | 002 (org units, locations), 004, 005, 006, 008, 009, 011, 012, 014, 016, 022, 023, 024, 025, 026, 032, 033, 013 (AI usage figures), 034 (AI quality figures, shared evaluation services) |

## 1. Problem and outcome

**Problem:** Communications, HR and IT leaders must show that the intranet works: who uses it, whether critical messages land, which content is useful, where people search and find nothing, how long services take, and whether policies are accepted. Without this they cannot justify the spend or fix what is broken. At the same time, employee monitoring is sensitive and regulated: analytics must not become a tool to watch individuals.

**Outcome:** TeamBeats gives each role a set of ready-made dashboards with drill-down by org unit, location, channel and time, built on aggregated data with a minimum group size. Communicators see reach and engagement for every item and campaign, including a simple return on effort. HR and service owners see cycle times and service level breaches. Compliance sees acceptance rates. Knowledge owners see search gaps and unhelpful articles. Location and department heads get one dashboard for their scope. Reports can be scheduled and exported within permission and privacy rules.

## 2. Scope

**In scope:**
- Analytics event collection from all modules, with a shared event schema and pseudonymous user keys.
- Dashboards:
  - Adoption: active users (daily, weekly, monthly), active rate per unit and location, channel split (web, mobile, Teams), new user activation, feature usage.
  - Content: views, unique readers, reach as share of audience, read depth and time buckets, reactions, comments, shares, top and bottom items, content by site and author, stale content (from spec 009).
  - Communications and campaigns: delivery, open, click and completion per channel and step; critical announcement acknowledgement rates and time to acknowledge; audience coverage gaps (units with low reach); return on effort (reach and actions per item against authoring effort and send cost estimates set by the tenant).
  - Search: queries, zero result queries, queries with no click, top queries, query refinements, by unit and locale (spec 012).
  - Knowledge: article helpfulness, assistant questions with no answer, most cited documents (specs 011 and 014).
  - Services and workflows: requests by service, volume trend, cycle time (median and 90th percentile), time per step, service level breaches, rejection reasons, automation rate, backlog per queue (specs 016 and 024).
  - HR: leave patterns, attendance exceptions, onboarding step completion, exit reasons (spec 022), all aggregated.
  - Policy compliance: attestation rate per policy, unit and location, overdue counts, time to attest, exemptions (spec 025).
  - Engagement: survey and poll results, recognition counts, community activity (spec 023), sentiment trends on comments and survey free text.
  - AI usage: requests, users, answer rate, citation rate, feedback, quota use (shared with spec 013), and headline AI quality scores per agent (shared with spec 034). Governance views are in spec 027 and evaluation detail in spec 034.
  - Security posture trends (spec 026): alerts, guest counts, policy weaker than baseline.
  - Experience: Core Web Vitals and frontend errors per tenant, org unit, page type, template, theme, device class and release; navigation success; template, layout and template version adoption; personalization enablement against actual use and resets; task completion, drop-off and findability outcomes (specs 008, 032 and 033).
- Location and unit dashboard: one page per location or unit combining adoption, communications reach, policy compliance, service performance and, in R3, business KPIs from connected systems through spec 017.
- Filters and drill-down: date range, org unit (with subtree), location, channel, locale, membership type, content type, site.
- Scheduled reports by email or Teams, as PDF or CSV.
- Export of aggregated data, and of person-level data only where a module defines a permitted personal report (for example acknowledgement and attestation lists in specs 009 and 025).
- Privacy controls: minimum group size, individual analytics off by default, purpose labels, retention of raw events.
- Product experience evaluation: whether search ranking, personalization, recommendations and employee journeys deliver useful results, measured with judged query sets and outcome metrics, reusing the dataset, run and scoring services of spec 034 (R2 search, R3 personalization and recommendations).
- Governed experience experiments (R3): controlled variants of pages or layouts for consenting users, with stable allocation and rollback.
- AI insights: plain-language summaries of dashboard changes and sentiment classification, through the AI Gateway, on aggregated or de-identified text.

**Out of scope:**
- A general business intelligence tool. Customers who need custom analysis export to their own BI tool through the analytics export or the R3 API.
- Productivity monitoring of individuals (keystrokes, screen time, idle time). Not planned.
- Process engine monitoring for operators (instance-level views in spec 024). This spec shows aggregates exported by spec 024.
- AI agent governance dashboards (spec 027).
- AI evaluation suites, release gates and the evaluation dashboard (spec 034).

## 3. Actors and permissions

| Actor | What they can see | Permission / scope |
|---|---|---|
| Tenant administrator | Adoption, AI usage, security trends; all dashboards at tenant scope | `analytics.view` at tenant scope |
| Communications lead | Content, campaigns, search, adoption | `analytics.view_comms` at site or org scope |
| Site owner | Content analytics for their site | `site.manage` |
| Author | Analytics for their own items | Author of the item |
| HR officer | HR and service analytics in scope | `analytics.view_hr` at org scope |
| Service owner | Their services' analytics | Service owner (spec 016) |
| Compliance officer | Policy compliance analytics | `policy.manage` |
| Location or unit head | Location or unit dashboard for their scope | `analytics.view_location` at that location or unit |
| Knowledge editor | Search and knowledge analytics | `kb.edit` |

## 4. User scenarios

### User Story 1: Did the message land? (Priority: P1, R1 counts, R2 dashboard)

Layla sent the evacuation procedure announcement to two plants. She checks after three days: 92% of the audience has seen it, 81% acknowledged, Plant B's night shift is at 40%. She sends a reminder to that group only.

**Independent test:** publish an item with acknowledgement to a test audience, generate views and acknowledgements, check the counts, breakdown and drill-down.

**Acceptance scenarios:**
1. **Given** a published item, **When** the author opens its analytics, **Then** they see audience size, unique readers, reach percentage, acknowledgement percentage (if required), reactions and comments, with a trend over time.
2. **Given** a breakdown by unit and location, **Then** groups below the minimum group size (default 5) are merged into "other" and never shown alone.
3. **Given** a group with low reach, **When** the author chooses "remind this group", **Then** a reminder is sent through spec 015 to people in the group who have not read or acknowledged, without showing the author their names unless the item requires acknowledgement and the author has the right to the acknowledgement report (spec 009).

### User Story 2: Campaign performance and return on effort (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** a campaign (spec 009 US8), **Then** the dashboard shows for each step and in total: delivered, opened, clicked, completed, by channel, unit and location, plus the share of the audience that completed the call to action.
2. **Given** the tenant has set estimates (authoring hours per item, cost per hour, cost per send per channel), **Then** the dashboard shows cost per reached employee and per completed action, and compares campaigns.
3. **Given** two campaigns, **Then** the user can compare them side by side.

### User Story 3: Search gaps (Priority: P1, R2)

**Acceptance scenarios:**
1. **Given** the search dashboard, **Then** it lists the top queries with no results and with no clicks for the period, by locale.
2. **Given** a zero-result query, **When** a knowledge editor opens it, **Then** they can create a knowledge article or add a "best bet" (spec 012) for it.
3. **Given** a query that contains personal data (for example a person's ID number), **Then** it is not shown; queries appearing for fewer than 5 distinct users are hidden.

### User Story 4: Service performance (Priority: P1, R2)

**Acceptance scenarios:**
1. **Given** a service owner, **Then** they see requests per week, median and 90th percentile cycle time, time per step, service level breaches, rejection reasons and the auto-approval rate.
2. **Given** a step that is slow, **Then** the dashboard highlights it and links to the process heatmap in spec 024 for operators.
3. **Given** a request type with structured reasons (for example resignation reasons), **Then** reasons are shown as counts by unit with the minimum group size.

### User Story 5: Policy compliance across the organization (Priority: P1, R2)

**Acceptance scenarios:**
1. **Given** a compliance officer, **Then** they see attestation rates for each active campaign, by unit and location, with overdue counts and the trend to the deadline.
2. **Given** a drill-down to a unit, **Then** person-level lists come only from the attestation report of spec 025 with its permissions; the analytics dashboard itself stays aggregated.

### User Story 6: Location dashboard (Priority: P2, R2, business KPIs R3)

The general manager of the Jeddah plant opens one page that shows adoption, communications reach, policy compliance, open service requests and breaches, and attendance exceptions for Jeddah, compared with the group average.

**Acceptance scenarios:**
1. **Given** a location head, **Then** their dashboard shows only their location's data plus group averages (which never reveal another location's figures alone).
2. **Given** a tenant admin configures a KPI tile from a connected system (for example monthly sales or safety incidents from an ERP or HSE system through spec 017) in R3, **Then** the tile shows the value for the location with its source and last sync time, and is hidden from users without permission to that data.
3. **Given** a location with fewer than the minimum group size of active people, **Then** person-derived tiles show "not enough data".

### User Story 7: Scheduled reports and export (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** a user with a dashboard, **When** they schedule it weekly to themselves or to a group, **Then** each recipient receives only the data they are permitted to see; if a recipient has no permission, they are not sent anything and the owner is told.
2. **Given** an export, **Then** it contains aggregated data with the same minimum group size, is generated in the background and is audited.
3. **Given** `analytics.export` is not in the package, **Then** export actions are hidden and the API refuses them.

### User Story 8: Sentiment and AI summaries (Priority: P3, R2)

**Acceptance scenarios:**
1. **Given** sentiment is enabled, **Then** comments on content and survey free text are classified as positive, neutral or negative, and dashboards show trends by topic, unit and location, aggregated with the minimum group size.
2. **Given** anonymous surveys (spec 023), **Then** sentiment never links back to a person, and free text is shown only when at least 5 responses exist for the filter.
3. **Given** a dashboard, **When** the user asks for a summary, **Then** the AI Gateway produces a short description of notable changes with the figures it used, labelled as AI-generated.

### User Story 9: Experience quality and adoption (Priority: P2, R1 telemetry and frontend errors, R2 dashboards, R3 experiments)

Nadia, the tenant's digital workplace lead, checks which published pages are slow on phones, which templates and versions are in use per subsidiary, and whether employees actually personalize their home after it was enabled.

**Independent test:** emit experience events from two unrelated component packs with no component-specific code; confirm the dashboards break them down by scope, device class, template and release, and that disabling analytics stops nonessential events.

**Acceptance scenarios:**
1. **Given** pages built from any registered components, **Then** page performance, navigation success, key interactions and UI errors arrive through the shared event schema without feature-specific code. *(CX-MET-001)*
2. **Given** the tenant turns nonessential analytics off, **Then** nonessential experience events are not collected; essential operational telemetry (errors, performance without user keys) continues as the privacy notice states. *(CX-MET-002)*
3. **Given** the template adoption report, **Then** it lists current pages per template, layout distribution and template version adoption by scope, and the counts match the page inventory of spec 032. *(CX-MET-003)*
4. **Given** the personalization report, **Then** it compares where personalization is enabled with actual use and resets, aggregated with the minimum group size. *(CX-MET-004)*
5. **Given** the performance dashboard, **Then** it shows p75 LCP, INP and CLS by device class, theme, organization, page type and, where the tenant allows, country, and flags published variants that miss the targets of spec 032. *(CX-MET-005)*
6. **Given** a frontend error, **Then** support sees the release, component version and correlation ID with payloads redacted. *(CX-MET-006)*
7. **Given** two different task flows (for example "submit a request" and "find a policy"), **Then** both use the same outcome event contract for start, complete and abandon, configured without schema changes. *(CX-MET-007)*
8. **Given** an approved experiment, **Then** only consenting users in the chosen scope are allocated, allocation is stable per user, and stopping the experiment returns everyone to the control without residue. *(CX-MET-008)*

### User Story 10: Product experience evaluation (Priority: P2, R2 search, R3 personalization and recommendations)

Nadia wants to know whether search actually finds the right policy, and later whether home recommendations are useful, not only whether people click.

**Independent test:** create a judged query set for search, run it against two ranking configurations, and compare the scores and the changed queries.

**Acceptance scenarios:**
1. **Given** a judged query set (queries per locale with the items that should rank first, labelled by knowledge editors), **When** it runs against the search configuration of planned spec 012, **Then** the dashboard shows precision@K, recall@K and the rank of the first relevant result per locale, using the dataset and run services of spec 034, with queries run as test personas with real permissions.
2. **Given** a proposed ranking change (best bets rules, boosts, a new embedding model), **When** it is evaluated, **Then** the scores are compared with the current configuration before it is promoted; retrieval changes used by AI answers also pass the spec 034 gate.
3. **Given** personalization or recommendations in R3, **When** they are evaluated, **Then** usefulness is measured from outcome events (opened, completed, dismissed, reset) and judged samples, aggregated with the minimum group size, and never scores an individual employee.
4. **Given** an employee journey defined on the outcome event contract (FR-021), **When** it is evaluated, **Then** completion, drop-off step and time to complete are compared between periods or releases.

### Edge cases and exception flows
- Org units restructure: historical data is reported under the unit at the time of the event by default, with an option for the current structure.
- A person moves location: their events count under the location at the time.
- Bots and service accounts: excluded from adoption metrics.
- Tenants with fewer than 50 users: most breakdowns show "not enough data"; totals still show.
- Late events (offline mobile): accepted up to 7 days late and counted on their event date.

## 5. Functional requirements

- **FR-001 / ANL-001**: The platform MUST provide core usage and content analytics in every package: adoption, item reach and engagement. *(LOC-005)*
- **FR-002 / ANL-002**: Advanced packages MUST add campaigns, search, knowledge, services and workflows, HR, policy compliance, engagement, AI usage and location dashboards. *(LOC-006)*
- **FR-003 / ANL-003**: All modules MUST emit analytics events to one schema with tenant, pseudonymous user key, org unit and location at event time, channel, locale and event-specific fields.
- **FR-004 / ANL-004**: Analytics MUST be aggregated by default with a minimum group size (default 5, tenant can raise it, never lower than 5). Individual-level analytics MUST be off by default. *(constitution XXV)*
- **FR-005 / ANL-005**: Person-level lists MUST only appear where a module defines a permitted personal report (acknowledgement in 009, attestation in 025, journey progress in 022) and MUST follow that module's permissions.
- **FR-006 / ANL-006**: Dashboards MUST support filters by date, org unit with subtree, location, channel, locale, membership type, site and content type, and respect the viewer's scope.
- **FR-007 / ANL-007**: The search dashboard MUST show zero-result and no-click queries, hiding queries from fewer than 5 distinct users and queries matching personal data patterns.
- **FR-008 / ANL-008**: Service analytics MUST show volume, median and 90th percentile cycle time, step durations, breaches, rejection reasons and automation rate. *(WF-012)*
- **FR-009 / ANL-009**: Campaign analytics MUST show per-step and total funnel by channel and return on effort from tenant-set estimates.
- **FR-010 / ANL-010**: Location and unit dashboards MUST combine module metrics for one scope, with group averages that never reveal another single scope, and R3 KPI tiles from connected systems with source and freshness.
- **FR-011 / ANL-011**: Scheduled reports MUST evaluate permissions per recipient at send time.
- **FR-012 / ANL-012**: Exports MUST apply the same aggregation rules, run in the background and be audited.
- **FR-013 / ANL-013**: Sentiment analysis MUST work only on aggregated or de-identified text, follow the tenant AI policy, and never link anonymous responses to a person.
- **FR-014 / ANL-014**: Raw analytics events MUST be kept for 13 months by default (tenant configurable between 3 and 25 months), and aggregates for the life of the tenant.
- **FR-015 / ANL-015**: Analytics MUST NOT include keystroke, idle time, screen time or similar productivity monitoring.
- **FR-016 / ANL-016**: AI usage figures MUST be shared with spec 013 and AI quality figures with spec 034, so that one number is shown everywhere. *(AI-013)*
- **FR-017 / ANL-017**: The shared event schema MUST cover page performance, navigation success, key interactions and UI errors for every registered component, emitted through the component event contract of spec 032 with no component-specific analytics code. *(CX-MET-001)*
- **FR-018 / ANL-018**: Experience telemetry MUST minimize personal data, follow the tenant's analytics policy and retention (FR-014), and stop nonessential events when the tenant disables them. *(CX-MET-002)*
- **FR-019 / ANL-019**: Experience dashboards MUST report template usage, layout distribution and template version adoption by scope, and personalization enablement against use and resets, at aggregate level only. *(CX-MET-003, CX-MET-004)*
- **FR-020 / ANL-020**: Performance dashboards MUST show Core Web Vitals by device class, geography where allowed, theme, organization, page type and release, and flag published variants below target. Frontend error views MUST show redacted client errors, failed component boundaries and release version with correlation IDs. *(CX-MET-005, CX-MET-006, CX-NFR-009)*
- **FR-021 / ANL-021**: Task completion, drop-off and findability outcomes SHOULD be configurable on one outcome event contract without component-specific schema changes. *(CX-MET-007)*
- **FR-022 / ANL-022**: Experience experiments COULD be run only when a tenant admin approves them, only for users who consented, with stable, scoped and reversible allocation, never on mandatory or critical content, and never to measure individuals. *(CX-MET-008, R3)*
- **FR-023 / ANL-023**: Search relevance MUST be measurable with judged query sets per locale (precision@K, recall@K, rank of first relevant result), run as test personas with real permissions on the dataset and run services of spec 034, and compared between search configurations before a change is promoted. *(R2)*
- **FR-024 / ANL-024**: Personalization, recommendations and employee journeys SHOULD be evaluated for usefulness from outcome events and judged samples, aggregated with the minimum group size, without scoring individuals. *(R3 for personalization and recommendations, R2 for journeys)*

## 6. Business and validation rules
- Reach = unique readers in the audience / audience size at the time of the reading period.
- Active user = at least one signed-in action (not only a notification delivery) in the period.
- Cycle time = submission to final state, excluding time the request was returned to the requester, shown both ways.
- Sentiment is shown as a share, not as a score per person.

## 7. Tenant and organization scope
- Tenant-owned data: events, aggregates, dashboards, schedules, KPI tile settings.
- Organization scoping: every dashboard is scoped by the viewer's analytics scope; drill-down never goes beyond it.
- Cross-tenant denial cases: no cross-tenant aggregation; R3 benchmarks only use opted-in tenants, with each tenant's data aggregated before combining and no tenant identifiable.
- Sibling, parent and child boundary cases: a unit head of "Acme Gulf" cannot see "Acme Egypt"; a parent-scope viewer sees children only with subtree scope; group averages exclude the viewer's own unit when that would reveal a sibling's number.

## 8. Data, ownership and classification

| Entity | System of record | Classification | Retention | Personal data? |
|---|---|---|---|---|
| Raw analytics event | Product | Confidential | 13 months default | Pseudonymous |
| Aggregate | Product | Internal | Life of tenant | No |
| Dashboard and schedule | Product | Internal | Life of tenant | Owner ID |
| KPI tile value | Connected system | Per source | 25 months | No |

## 9. Package entitlements

| Capability key | Workplace | Workplace Plus | Enterprise & Government |
|---|---|---|---|
| `analytics.core` (adoption, item analytics) | on | on | on |
| `analytics.advanced` (all dashboards in section 2) | off | on | on |
| `analytics.location_dashboards` | off | on | on |
| `analytics.sentiment` (counts against AI quota) | off | on | on |
| `analytics.export` | off | off | on |
| `analytics.kpi_tiles` (connected business KPIs) | off | off | on |
| `analytics.api` | off | off | on |
| `analytics.experience` (experience quality, template adoption and personalization dashboards, product experience evaluation) | off | on | on |
| `analytics.experiments` (R3) | off | off | on |

## 10. UX, localization and accessibility
- Every chart has a table view and a text summary for screen readers; color is never the only signal.
- Numbers and dates follow the user's locale settings (spec 007), including the tenant's digit choice.
- Charts mirror correctly in right-to-left layouts (time axes keep reading order set by locale guidance).
- "Not enough data" states explain the privacy reason.

## 11. AI behavior and data policy
- Summaries and sentiment go through the AI Gateway (spec 013), with aggregated or de-identified inputs only.
- AI summaries quote the figures they use and are labelled.
- No AI scoring of individual employees.

## 12. Integrations and events
- Consumes analytics events from all modules and exports from spec 024.
- R3 KPI tiles read through connectors (spec 017).
- R3 analytics API and export to customer BI tools, tenant-scoped with the same aggregation rules.

## 13. Audit, telemetry and analytics

| Event | Type | Key fields |
|---|---|---|
| analytics.export_requested / completed | audit | dashboard, filters, actor |
| analytics.schedule_created / changed | audit | dashboard, recipients, actor |
| analytics.privacy_setting_changed | audit | setting, before, after, actor |
| analytics.kpi_tile_configured | audit | source, scope, actor |
| analytics.experiment_started / stopped | audit | experiment, scope, variants, actor |

## 14. Security and abuse cases
- Narrowing filters to identify one person (differencing attacks): minimum group size applies to every filter combination, and complementary suppression hides a second cell when one is suppressed.
- Managers trying to monitor one employee: no individual analytics; only module-defined reports with their own rules.
- Exports as a data leak path: package-gated, audited, aggregated.

## 15. Non-functional requirements
- Dashboards load p95 under 3 seconds for tenants up to 100,000 users and 12 months of data.
- Event to dashboard freshness under 15 minutes for counts, under 24 hours for heavy aggregates.
- Event collection never slows the user action that triggers it (asynchronous).

## 16. Success criteria
- **SC-001**: A communicator answers "what share of the audience read and acknowledged this" in under 30 seconds.
- **SC-002**: In privacy tests, no filter combination returns a group smaller than the minimum group size.
- **SC-003**: A pilot tenant's leadership receives a monthly scheduled report without manual work.

## 17. Open questions
- [NEEDS CLARIFICATION: should tenants be able to turn on individual analytics with works council or legal approval?] Default: no in R2; revisit with customer demand.
- [NEEDS CLARIFICATION: cross-tenant benchmarks in R3?] Default: opt-in only.

## 18. Traceability

| Requirement ID | Covered by |
|---|---|
| LOC-005 (now ANL-001) | FR-001, US1 |
| LOC-006 (now ANL-002) | FR-002, US2 to US6 |
| AI-013 | FR-016 |
| WF-012 | FR-008, US4 |
| UC-04 | US1, US2 |
| UC-08 | US4 |
| CX-MET-001 | FR-017, US9 |
| CX-MET-002 | FR-018, US9 |
| CX-MET-003 | FR-019, US9 |
| CX-MET-004 | FR-019, US9 |
| CX-MET-005 | FR-020, US9 |
| CX-MET-006 | FR-020, US9 |
| CX-MET-007 | FR-021, US9 |
| CX-MET-008 | FR-022, US9 |
| Product experience evaluation (new) | FR-023, FR-024, US10 |

## Review checklist
- [x] No implementation details
- [x] Every FR is testable and traces to a requirement ID or is marked new
- [x] Cross-tenant and org boundary cases are listed
- [x] Entitlement behavior is defined
- [x] Audit events are defined
- [ ] No open [NEEDS CLARIFICATION] items that change scope
