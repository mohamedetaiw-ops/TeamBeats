# Specs index

Status values: Proposed, Approved, In Progress, Implemented, Deprecated, Retired. Only Approved specs may be implemented.

| Spec | Area | Release | Status | Requirement IDs |
|---|---|---|---|---|
| [001 Tenant management](001-tenant-management/spec.md) | platform | R0 | Approved | TEN-001, TEN-002, TEN-009, TEN-010, ADM-001 |
| [002 Organization hierarchy](002-organization-hierarchy/spec.md) | platform | R0, R1 to R2 (AD mapping) | Approved; AD mapping pending approval | TEN-003 to TEN-006, TEN-010, INT-007 (directory) |
| [003 Identity and authentication](003-identity-and-authentication/spec.md) | platform | R0 to R2 | Approved; AD sign-in and sync pending approval | IAM-001 to IAM-007, IAM-009, IAM-010, INT-007 (directory) |
| [004 Authorization and permissions](004-authorization-and-permissions/spec.md) | platform | R0, R1 (attribute conditions) | Approved | TEN-007, TEN-008, IAM-007, IAM-008, ADM-001 |
| [005 Packages and entitlements](005-packages-and-entitlements/spec.md) | platform | R0, R2 | Approved | AI-001, ADM-001, ENT-001 to ENT-008 (new) |
| [006 Audit and admin shell](006-audit-and-administration/spec.md) | platform | R0, R1 to R2 (Connections area) | Approved (Connections area approved 2026-11-05) | ADM-001 to ADM-003, TEN-010, INT-001 (connection administration), AUD-001 to AUD-007 (new) |
| [007 Localization and accessibility](007-localization-and-accessibility/spec.md) | experience | R0 to R2 | Approved; composable experience additions pending approval | LOC-001 to LOC-004, EXP-008, CX-ACC-001 to CX-ACC-014, CX-LOC-001 to CX-LOC-005, CX-LOC-007 to CX-LOC-010 |
| [008 Experience shell, navigation, employee home and personalization](008-experience-shell-and-personalization/spec.md) | experience | R1 to R3 | Proposed | EXP-001, EXP-008, CX-EXP-001 to CX-EXP-010, CX-NAV-001 to CX-NAV-010, CX-PER-001 to CX-PER-015, CX-INT-001 to CX-INT-012 |
| [009 Content, publishing and governance](009-content-and-publishing/spec.md) | experience | R1 to R3 | Proposed | EXP-002 to EXP-004, LOC-004, PUB-001 to PUB-020 |
| [011 Document management and knowledge base](011-document-management/spec.md) | knowledge | R1 to R3 | Proposed | KNO-001, KNO-003, KNO-006, ADM-003, INT-002 (SharePoint documents), DOC-001 to DOC-033 |
| [013 AI Gateway and model governance](013-ai-gateway/spec.md) | ai | R1 to R3 | Proposed | AI-002 to AI-004, AI-006, AI-011 to AI-013, AIG-001 to AIG-019 |
| [016 Self services, service catalogue and workflows](016-service-catalogue-and-workflows/spec.md) | workflows | R1, R2 | Proposed | WF-001 to WF-016, UC-08 |
| [020 Agent Hub: registry, templates, studio and marketplace](020-agent-hub/spec.md) | ai | R1 to R3 | Proposed | AI-007 (agent part), AI-008, AGT-001 to AGT-020, UC-07 |
| [021 Analytics and insights](021-analytics/spec.md) | analytics | R1 to R3 | Proposed | ANL-001 to ANL-024 (ANL-001 and ANL-002 were LOC-005 and LOC-006), AI-013 (shared with 013), CX-MET-001 to CX-MET-008 |
| [022 Leave, attendance and HR workflows](022-hr-workflows/spec.md) | workflows | R2, R3 | Proposed | INT-007, LVA-001 to LVA-012, HRW-001 to HRW-014 |
| [024 Process engine (BPMN and DMN)](024-process-engine/spec.md) | workflows | R1 to R3 | Proposed | WF-002, WF-003, BPM-001 to BPM-016 |
| [025 Policy management](025-policy-management/spec.md) | knowledge | R1 to R3 | Proposed | EXP-004, KNO-001, KNO-007, POL-001 to POL-018 |
| [026 Security controls, compliance and certifications](026-security-and-compliance/spec.md) | platform | R1 to R3 | Proposed | TEN-009, IAM-009, ADM-002, ADM-003, SEC-001 to SEC-021, CMP-001 to CMP-004, CX-NFR-010 |
| [027 Agent governance, tools and safety](027-agent-governance/spec.md) | ai | R1 to R3 | Proposed | AI-005, AI-009, AI-010, AGV-001 to AGV-024, UC-07, UC-13 |
| [032 Page composition: layouts, templates, component registry and page builder](032-page-composition/spec.md) | experience | R1 to R3 | Proposed | CX-LAY-001 to CX-LAY-012, CX-TMP-001 to CX-TMP-010, CX-TMP-012, CX-EDT-001 to CX-EDT-015, CX-EXT-001 to CX-EXT-014, CX-GOV-001 to CX-GOV-012, CX-NFR-001 to CX-NFR-012 |
| [033 Brands, themes, white labeling and multi-organization experience](033-themes-and-white-labeling/spec.md) | experience | R1, R2 | Proposed | CX-BRD-001 to CX-BRD-015, CX-ORG-001 to CX-ORG-011, CX-TMP-011, CX-LOC-006, ADM-001 (branding) |
| [034 AI Evaluation Layer](034-ai-evaluation/spec.md) | ai | R1 to R3 | Proposed | EVL-001 to EVL-030, AI-008 (quality part), AI-010 (evidence part), UC-07, UC-13 |

Open questions across these specs, with defaults: [open-questions.md](open-questions.md). Release plan: [roadmap](../docs/product/roadmap.md).

## R0 build order

Each spec folder now has `spec.md` (Approved), `plan.md` and `tasks.md`. Shared setup is in `docs/architecture/r0-solution-structure.md` and in Phase 1 of `001-tenant-management/tasks.md`.

1. 001 Phase 1: platform setup, plus ADR-0006 to ADR-0008
2. 006 Phase 1 to 2 (`IAuditWriter`) and 007 Phase 1 to 3 (localization gates), in parallel
3. 001 and 005 core (tenants need packages)
4. 002, then 004 (authorization needs the hierarchy)
5. 003 R0 parts (dev sign-in, sessions)
6. 006 admin shell and the rest of 007
7. R0 exit: all cross-tenant and org boundary suites green

## Next specs (not yet written)

| Planned spec | Release | Requirement IDs |
|---|---|---|
| 010 Employee directory and org chart | R1 | EXP-005, EXP-006, UC-05. Profile fields and reporting line from AD (spec 003), My team, newcomers. Skills and expertise, badges from recognition (023), out of office status from leave (022) |
| 012 Search and retrieval | R1 | KNO-002 to KNO-006. Type-ahead; indexes people, pages, documents, policies, knowledge articles and services; AI summary of top results; best bets; federated results from connected systems (SharePoint, Google Drive, ticketing) with source permissions; search analytics feed to 021; search relevance measured with judged query sets (021 ANL-023) and retrieval changes used by AI pass the 034 gate. Sets the KNO-006 synchronization objective that specs 009 and 011 rely on: permission changes and deletions reach search and vector indexes within 5 minutes by default |
| 014 AI assistant and RAG | R1 | AI-005, AI-007, KNO-007, UC-06. The assistant is the universal entry point and routes requests to agents (spec 020 US7); it holds no tools of its own. Assistant routing and RAG configuration (chunking, embedding model, retrieval parameters) have suites in 034 and changes pass its release gate |
| 015 Notifications and email | R1 (in-app and email), R2 (Teams, mobile push, digital signage) | INT-008 (new NOT- IDs). In-app notification centre replaces the old "Intra mail" inbox. Channels: in-app, email, Teams, mobile push, digital signage feed; user channel preferences with mandatory messages exempt; used by campaigns in 009 |
| 017 Connector framework | R2 | INT-001, INT-009, INT-010. `Integrations` module behind the Connections area of 006 (FR-010 to FR-015) and the document repository contract of ADR-0016; custom REST API connectors; Slack as a notification and share channel; clock data and ERP hand-off used by 022; KPI tiles used by 021 |
| 018 Microsoft 365 and SharePoint | R2 | INT-002, INT-003, UC-10 |
| 019 HR integration | R2 | INT-007, UC-09. Payslip view (read-only, Confidential), HR record prefill; AD covers the directory part (specs 002, 003) |
| 023 Communities and engagement | R2 | EXP-007. Discussion channels (public and per department), Speak Up with anonymous option, polls, recognition (team and star of the period), celebrations, photo gallery, points and leaderboard. Anonymous surveys with results thresholds, custom badges, site-wide thank-you shout-outs, discussion rooms, in-product messaging |
| 028 Learning and mandatory training | R2 | New LRN- IDs. Courses and learning paths, mandatory training assignments with due dates (onboarding in 022, policy linked training in 025), completion tracking, external LMS connector as an alternative |
| 029 Project and team spaces | R2 | New PRJ- IDs. Spaces with tasks, deadlines, milestones and progress, files from 011, discussions from 023, team calendar; can be opened to guests (026) |
| 030 Mobile app and push | R2 | EXP-009. Branded mobile app for office and frontline staff (or PWA first, foundation open decision), push notifications, offline reading of critical items, kiosk and shared device mode |
| 031 Workplace booking | R2 | New BKG- IDs. Rooms, desks and resources for tenants without Microsoft 365 rooms (018 covers M365 room mailboxes); availability calendar; check-in |

## Requirement coverage
Every foundation requirement ID is mapped above to a written or planned spec, except INT-004 to INT-006 (Google, Oracle, SAP packs), which belong to later R2 and R3 specs. EXP-009 (PWA) is in planned 030; AI-013 (usage visibility) is in 021 with 013.

## AI Agent Governance Plan coverage

Source: "AI Agent Governance Plan" (2026-11-05). Each of its 15 product epics maps to a spec. Agent runtime choice: ADR-0011 (in the .NET monolith, not a separate Python and Temporal stack).

| Epic | TeamBeats home |
|---|---|
| 1 Agent registry and template library | 020 (registry, 15 templates in section 12) |
| 2 Agent Studio and configuration | 020 US1, US4, section 12 |
| 3 Agent marketplace and discovery | 020 US6 |
| 4 Universal assistant and orchestration | 014 (assistant surface), 020 US7 and US10 (routing, handoffs) |
| 5 AI model gateway and providers | 013 |
| 6 Knowledge sources, indexing and RAG | 014, 012, 011 |
| 7 Connector and tool registry | 027 FR-005, FR-006 (tools), 017 (connectors) |
| 8 Workflow and human approval engine | 027 US4, 016 (task inbox), 024 (process engine) |
| 9 Agent identity, authorization and tenant isolation | 027 US2, 004, ADR-0005 |
| 10 Agent governance and policy | 027 US1, US8, US9 |
| 11 Execution monitoring and audit | 027 US6, 006 |
| 12 Evaluation, testing and versioning | 034, with the gate shown in 020 US5 and safety suites in 027 US9 |
| 13 Cost, quota and usage | 013 US4, US5, 005 |
| 14 Multilingual agent experience | 020 FR-017, 007 |
| 15 Lifecycle, deployment and rollback | 020 US2, US3, 027 US7 |

## AI Evaluation Layer coverage

Source: Mohamed's AI Evaluation Layer recommendation (2026-11-05). Evaluation is a core platform capability in every package and is built first among the AI work; product experience evaluation follows.

| Recommendation | TeamBeats home |
|---|---|
| Evaluation as its own bounded context with control plane, execution plane and evidence store | 034 EVL-001, EVL-002, ADR-0015 |
| Async evaluation, inline safety checks stay in the request path | 034 EVL-003, 027 section 11, 013 |
| Ten evaluation dimensions (answer quality to user satisfaction) | 034 section 12 |
| Golden datasets (ar and en in R1; fr, ur, hi in R2), model as judge, trajectory, production evaluation, release gates | 034 US2 to US8 |
| Multi-tenant levels: platform, tenant, organization, agent, agent version | 034 US10 |
| Build the management capability, integrate DeepEval, Ragas, Langfuse and OpenTelemetry through adapters | ADR-0015, 034 EVL-028, EVL-029, 013 AIG-018 |
| First release: registry, datasets, engine, scoring and thresholds, dashboard, release gates | 034 R1 |
| Later: production sampling, review queues, drift, A/B comparisons, business outcomes | 034 R2 |
| Product experience evaluation (search, personalization, recommendations, journeys) | 021 US10, ANL-023, ANL-024 |
| Core layer in every package, advanced analytics by package | 034 section 9, 005 |

## Current intranet coverage

Source: functional review of the current Link Development intranet (Performly "Intra"), 2026-11-05. Every function maps to a spec. "Linked" means the catalogue (016) opens the other system.

| Current function | TeamBeats home | Release |
|---|---|---|
| Hamburger menu, quick links ribbon, home widgets as three routes to services | One Services catalogue (016), personal My services strip on home (008) | R1 |
| Header: clock, search, task badge, profile menu | Header in 008, search in 012, task inbox in 016 | R1 |
| Attendance sign in and out, working hours, productivity, punctuality, compensation day | 022 | R2 |
| Leave request with live balance, my and team leave, overlap detector, leave calendar | 022, calendar widget in 008 | R2 |
| Pending tasks, subordinates' tasks | Task inbox (016) | R1 |
| Manage replacement (delegate while away) | Delegation (016) | R1 |
| Payslip | 019 (read from payroll or HR) | R2 |
| Training and Udemy requests, HR appointments, resignation | Form services (016); Udemy as linked app; an approved resignation starts the offboarding journey (022) | R1 form, R2 templates |
| Expenses, purchase request, business cards, transportation, maintenance, travel, parking, referrals | Form services from the starter templates (016); expense claims with policy limits and receipts in 022; ERP hand-off via 017 | R2 |
| Meeting room booking | Microsoft 365 room mailboxes through 018 | R2 |
| Help Desk, VPN request, Es2alny FAQ | Help Desk as linked app or form service (016); VPN as form service; FAQ answered by the AI assistant (014) | R1 |
| Announcements, news, condolences, policy notices | 009 | R1 |
| Handbooks, policies, career paths and job descriptions, learning library | Searchable pages and documents (009, 011, 012) | R1 |
| Corporate offers, internal vacancies, promotion lists | 009 | R1 |
| Discussion channels, Speak Up, poll, gallery, team and star of the period, celebrations, banking points | 023 | R2 |
| Newcomers, My profile, team and peers | 010 | R1 |
| Profile photo, theme, language | 010 (photo), 008 (theme), 007 (language) | R1 |
| Change password | Not in TeamBeats: handled by AD or Entra ID self-service password reset | n/a |
| Email signature | Not carried over (belongs to the mail system) | n/a |
| Intra mail inbox | Notification centre (015) | R1 |
| Tool box: weather, world clock, time zone converter | Home widget (008) | R1 |
| Linkawy360, Talent, Agile, Tech Hub, PAL, SPIN, Happiness Circle | Linked apps (016) with SSO cue | R1 |
| iOS and Android apps | Responsive web and PWA (EXP-009); native apps are an open foundation decision | R2 |
| Five languages (en, ar, hi, bn, fr) | ar and en in R1; fr, ur, hi in R2. Bengali is not planned | R1, R2 |

Gaps in the current intranet that the specs close: single catalogue page (016), one look for all services (007, 016), policies as searchable pages (009, 012), search that finds services and documents (012), clear cue and SSO for other systems (016), personalizable home (008), one discussion source for widget and list (023), labelled icons and AA contrast (007).

## Composable Intranet Experience coverage

Source: "Composable Intranet Experience Layer" requirements v1.0 (2026-11-05), adopted as mandatory and kept at [docs/product/composable-experience-requirements.md](../docs/product/composable-experience-requirements.md). Its 182 requirements are cited with a `CX-` prefix because EXP, INT and LOC IDs also exist in the foundation. Architecture: ADR-0012. Every requirement maps to a functional requirement: see the [coverage map](../docs/product/composable-experience-coverage.md).

| Group | TeamBeats home |
|---|---|
| 5.1 Experience shell (CX-EXP) | 008 |
| 5.2 Responsive grid and layouts (CX-LAY) | 032 |
| 5.3 Templates, presets and variants (CX-TMP) | 032; starter wizard CX-TMP-011 in 033 |
| 5.4 Themes, tokens and white labeling (CX-BRD) | 033 |
| 5.5 Multi-organization inheritance (CX-ORG) | 033, with 002 for moves |
| 5.6 Personalization and targeting (CX-PER) | 008 |
| 5.7 Visual page builder (CX-EDT) | 032 |
| 5.8 Navigation and wayfinding (CX-NAV) | 008 |
| 5.9 Interaction rules (CX-INT) | 008 |
| 5.10 Accessibility (CX-ACC) | 007, applied in 032 and 033 |
| 5.11 Localization and RTL (CX-LOC) | 007; localized branding CX-LOC-006 in 033 |
| 5.12 Component contract (CX-EXT) | 032 |
| 5.13 Experience governance (CX-GOV) | 032, roles in 004 |
| 5.14 Experience analytics (CX-MET) | 021 |
| 5.15 Performance and resilience (CX-NFR) | 032; secure rendering also in 026 |

Release rule: every Must and Should is in by R2 GA; the two Could items (template recommendations, experiments) are R3. R1 carries the foundation of the layer (wave 1 of the source), the 12 templates and the visual page builder (decided by Mohamed 2026-11-05), plus home personalization.

## Competitor feature coverage

Capabilities from two competing intranet products (a modular intranet for regulated industries and an enterprise employee experience platform) were compared with these specs on 2026-11-05. Every capability now has a home: see [competitor feature coverage](../docs/product/competitor-feature-coverage.md).

## Architecture patterns review

Source: architecture patterns recommendation (modular monolith, hexagonal, DDD, component-driven and schema-driven UI, MVVM), 2026-11-05. Reasoned item by item in [docs/review/architecture-patterns-review.md](../docs/review/architecture-patterns-review.md). Outcome, approved by Mohamed 2026-11-05: constitution 1.1.0, ADR-0013 module internal architecture, ADR-0014 frontend architecture, the [module map](../docs/architecture/module-map.md), spec 004 attribute conditions and spec 032 component interaction rules. NestJS and Temporal are not adopted (ADR-0013 option 4, ADR-0010 option 6).
