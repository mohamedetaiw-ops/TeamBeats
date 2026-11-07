# Module map: bounded contexts

Status: Accepted (with ADR-0013)
Date: 2026-11-05

This is the authoritative list of backend modules (constitution XIV). Each module is one bounded context: it owns its data in its own PostgreSQL schema, exposes a `Contracts` project for synchronous calls, and publishes events through the outbox. No module reads another module's tables. A new module, or moving data between modules, updates this file in the same change.

Planned modules are listed so that specs use the same names before the code exists. Their event names are indicative until their spec is written.

## 1. Modules

| Module | Specs | Owns (system of record) | Exposes (Contracts) | Publishes (main events; the spec's events section is complete) | Release |
|---|---|---|---|---|---|
| `Tenancy` | 001, 002 | Tenants, tenant settings, organization units and closure tree, memberships | Tenant lookup, org tree queries, membership checks | `TenantCreated`, `TenantSuspended`, `OrganizationUnitMoved`, `OrganizationMembershipChanged` | R0 |
| `Identity` | 003 | Identity provider configuration, external identity links, sessions, directory sync state | Session validation, user lookup | `UserSignedIn`, `UserProvisioned`, `UserDeprovisioned`, `SessionRevoked` | R0 |
| `Identity.DevAuth` | 003 US1 | Development-only local credentials | none (dev host only, ADR-0004) | none | R0 |
| `Authorization` | 004 | Roles, permissions, assignments, conditions, resource grants, inheritance rules | `IAuthorizationService` (decide, batch filter, explain) | `RoleAssignmentChanged`, `ResourcePermissionChanged` | R0, R1 conditions |
| `Entitlements` | 005 | Packages, capability keys, add-ons, quotas, usage counters | `IEntitlementService` | `EntitlementsChanged` | R0 |
| `Audit` | 006 | Audit records, seals, exports | `IAuditWriter`, audit search | `AuditIntegrityCheckFailed`, `AuditExportCompleted` | R0 |
| `Administration` | 006 | Admin shell read models only (no business data of its own) | none | none | R0 |
| `Experience` | 008, 032, 033 | Shell and navigation configuration, pages, templates, layouts, fragments, themes and tokens, personalization policy and preferences, component registry | Effective configuration, component registration | `ShellPublished`, `PagePublished`, `TemplateReleased`, `ThemePublished`, `ComponentRegistered` | R1 |
| `Content` | 009 | News, announcements, campaigns, content pages and their workflow state | Content queries for components and search | `ContentPublished`, `ContentExpired`, `CampaignStepSent` | R1 |
| `Documents` | 011 | Libraries, documents, versions, records holds, knowledge articles, connected library links (document source port, ADR-0016) | Document queries, file access tokens, legal hold contract (held audit selections for `Audit`) | `DocumentVersionPublished`, `DocumentDeleted`, `LegalHoldApplied`, `ConnectedLibraryAdded` | R1, R2 connected libraries |
| `Policies` | 025 | Policies, attestations, policy versions | Policy queries, attestation status | `PolicyPublished`, `PolicyAttested`, `AttestationOverdue` | R1 |
| `People` | 010 | Employee profiles and org chart projections (identity data stays in `Identity`) | Profile queries | `ProfileUpdated` | R1 |
| `Search` | 012 | Search and vector index records (derived, rebuildable) | Query API with permission filtering | none | R1 |
| `AiGateway` | 013 | Model policy, routes, budgets, private endpoints, usage records | `IAiGateway` (chat, tools, embeddings) | `AiPolicyChanged`, `AiBudgetThresholdReached` | R1 |
| `Agents` | 020, 027, 014 | Agent definitions, versions, runs and checkpoints, tool registry, execution records, assistant conversations | Agent invocation, tool gateway | `AgentRunCompleted`, `AgentActionAwaitingApproval`, `AgentKillSwitchActivated` | R1 |
| `Evaluation` | 034 | Evaluation suites, datasets, rubrics, thresholds, runs, results, gate decisions and evidence (ADR-0015) | Gate requests and results, run queries | `EvaluationRunCompleted`, `EvaluationGateDecided` | R1 |
| `Notifications` | 015 | Notifications, channel preferences, deliveries | `INotificationSender` | `NotificationDelivered` | R1 |
| `Services` | 016 | Service catalogue, forms, requests, task inbox, delegations | Task inbox API, request submission | `RequestSubmitted`, `RequestCompleted`, `TaskCompleted` | R1 |
| `Workflow` | 024 | Process and decision definitions, instances, jobs, timers, incidents | Start process, correlate message, complete task | `ProcessInstanceCompleted`, `UserTaskCreated`, `IncidentRaised` | R1 core, R2 BPMN |
| `Integrations` | 018 (mail, calendar, rooms), 017, 019, 006 (Connections area), 011 (SharePoint adapter) | Connections and their features, consent and secret references, sync state, field mappings, external IDs | Canonical connector contracts per capability (for example the document repository contract of ADR-0016), connection list and actions | `ConnectionCreated`, `ConnectionConsentRevoked`, `ExternalRecordSynced`, `ConnectorUnavailable`, `ConnectorRestored` | R2 (Microsoft 365 connection for directory sync from R1) |
| `Hr` | 022 | Leave, attendance, HR journeys, expense claims | Balances, team calendar, working-day calculation (from R1) | `LeaveApproved`, `JourneyStarted`, `ExpenseApproved`, `WorkingCalendarChanged` | R1 (holiday and working week calendars), R2 (rest) |
| `Analytics` | 021 | Aggregated metrics and reports (derived from events) | Report queries with minimum group size | none | R1 |
| `Security` | 026 | Guest access, security controls and settings of spec 026 | Classification lookups, security settings | `SecurityPolicyChanged`, `GuestExpired`, `SecurityAlertRaised` | R1 |
| `Engagement` | 023 | Communities, channels, posts, comments, polls, surveys, recognition, points, celebrations, galleries | Feed queries, widget queries | `PostPublished`, `PostRemoved`, `RecognitionGiven`, `SurveyClosed` | R2 |
| `Learning` | 028 | Courses, quizzes, paths, assignments, progress, certificates | Assign course, completion status | `CourseAssigned`, `CourseCompleted`, `CourseOverdue` | R2 |
| `Spaces` | 029 | Project and team spaces, members, tasks, milestones, status | Space queries, My tasks source for the 016 inbox | `SpaceCreated`, `TaskAssigned`, `TaskCompleted`, `SpaceStatusChanged` | R2 |
| `Booking` | 031 | Floors, zones, bookable resources, rules, bookings, check-ins | Availability | `BookingConfirmed`, `BookingReleased`, `CheckedIn` | R2 |

030 (mobile app and PWA) is a client of the same APIs, not a module. Localization (007) is a building block, not a module.

## 2. Relationships

- **Upstream to everyone:** `Tenancy`, `Authorization`, `Entitlements` and `Audit`. Other modules call their contracts and follow their rules; they never copy their data except as caches invalidated by their events.
- **Derived read models:** `Search`, `Analytics` and `Administration` own only data rebuilt from other modules' events and contracts. Deleting and rebuilding them loses nothing.
- **Translation at the edge:** `Integrations` turns external models (Microsoft Graph, SAP, Oracle, Google) into canonical contracts (constitution XVII). No product module sees an external DTO.
- **AI as a platform service:** features call `Agents` or `AiGateway` contracts. No module embeds its own prompts against a provider (constitution VIII).
- **Experience composes, never owns business data:** components placed by `Experience` read data from the owning module's API (spec 032 FR-027).

## 3. Event contract rules

- Names are PascalCase, past tense, owned by the publishing module (for example `PagePublished`).
- Every event carries `tenant_id`, `message_id`, `correlation_id`, schema version and occurred-at time; events about organization-owned data also carry `org_unit_id` (constitution I, II).
- Schemas live in the publisher's `Contracts` project. Changes within a major version are additive; consumers ignore unknown fields (constitution XXIX).
- Events carry identifiers and the facts consumers need, not whole records with personal data. Consumers that need more call the publisher's contract under the consumer's own authorization context.
