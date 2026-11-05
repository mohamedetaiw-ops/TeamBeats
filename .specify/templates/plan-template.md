# Implementation Plan: [FEATURE NAME]

**Spec:** `specs/[NNN-name]/spec.md` (version [x]) | **Branch:** `[NNN-name]` | **Date:** [YYYY-MM-DD]

## Summary
[One paragraph: what is being built and the technical approach]

## Technical context

| Item | Value |
|---|---|
| Web | React + Next.js 16.x (Active LTS) |
| API | ASP.NET Core on .NET 10 LTS |
| Database | PostgreSQL 18, RLS, pgvector where needed |
| Messaging | RabbitMQ + transactional outbox |
| Storage | S3-compatible adapter |
| Modules touched | [list] |
| New dependencies | [none, or list with licence and reason] |

## Constitution check (gate)

Answer every line. Any "No" needs an entry in Complexity Tracking or the plan is rejected.

| Principle | Pass? | Evidence |
|---|---|---|
| I Tenant boundary: tenant_id, RLS, context from session only | | |
| II Org scope: server-side, no implicit inheritance | | |
| III Central authorization, no role-name checks | | |
| IV / V Secrets and dev-auth isolation | | |
| VI Audit events defined | | |
| VII to XIII AI rules (if AI is used) | | |
| XIV Module boundaries, no cross-module tables | | |
| XV No new infrastructure without ADR | | |
| XVI OpenAPI, versioning, idempotency | | |
| XVII Connector adapters only | | |
| XVIII / XIX Durable jobs, telemetry, correlation IDs | | |
| XX One code line | | |
| XXI Entitlements via capability keys | | |
| XXII Dependency licences checked | | |
| XXIII / XXIV Localization and accessibility | | |
| XXV Classification and privacy | | |

## Data model
[Entities, tenant_id, keys, RLS policy, indexes, migration notes. Detail in data-model.md if large.]

## API contracts
[Endpoints and events. OpenAPI in contracts/.]

## Authorization
[Policies used, scopes, which decisions (CanView, CanEdit...)]

## Test strategy
- Unit:
- Integration (real PostgreSQL with RLS):
- Cross-tenant negative tests:
- Org boundary tests:
- Entitlement tests:
- Accessibility / RTL:

## Project structure
[Which folders under src/ and tests/ change]

## Risks and open items

## Complexity tracking

| Deviation | Why needed | Simpler option rejected because |
|---|---|---|
