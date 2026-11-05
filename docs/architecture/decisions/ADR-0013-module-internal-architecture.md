# ADR-0013: Module internal architecture and bounded contexts

| Field | Value |
|---|---|
| Status | Accepted (2026-11-05 by Mohamed) |
| Date | 2026-11-05 |
| Owners | Mohamed |
| Related specs | all backend specs; constitution XIV, XXIX |

## Context and constraints
ADR-0001 fixes a modular monolith with hard boundaries between modules, but says nothing about structure inside a module. The R0 modules (Tenancy, Identity, Authorization, Entitlements, Audit) are flat folders of services with SQL next to rules. That is fine for CRUD, but the R1 and R2 modules (Experience resolver, Workflow engine, Agents run loop, Integrations) hold real domain logic that must be tested without a database and must swap adapters (provider, connector, storage) without touching rules.

An architecture recommendation shared on 2026-11-05 proposes hexagonal (ports and adapters) structure and DDD bounded contexts, on a NestJS backend. The constitution names ASP.NET Core (XIV) and R0 is written in .NET 10.

## Decision drivers
Testable domain logic, swappable infrastructure, boundaries a reviewer and an architecture test can check, no ceremony that slows a small team, one backend language.

## Options considered
1. **Keep flat modules.** Least code, but rules and SQL mix, and every adapter swap touches business code.
2. **Full clean architecture template per module** (separate projects for Domain, Application, Infrastructure, Api; mediator, generic repositories, mapping library). Clear, but heavy for small modules and against r0-solution-structure section 5 (no mediator, explicit mapping).
3. **Light ports and adapters per module (chosen).** Folders, and projects only where a module is large, with dependencies pointing inward and the module map as the list of bounded contexts.
4. **Rewrite the backend in NestJS (TypeScript) to follow the recommendation literally.** One language across frontend and backend. Discards R0 and ADR-0006 to 0012, and the patterns themselves do not need it. Rejected.

## Decision
Option 3.

1. **Bounded contexts.** Each backend module is one bounded context, listed in `docs/architecture/module-map.md` with owned data, contracts and events. Each spec names its module from that list.
2. **Inside a module:**
   - `Domain`: entities, value objects, domain rules and domain events. Plain C#; no ASP.NET Core, Npgsql, RabbitMQ, HTTP or SDK references.
   - `Application`: use case handlers (plain classes, as in R0), and the **ports** they need: persistence, messaging, clock, other modules' contracts, external systems.
   - `Adapters`: the API endpoints (driving side), and Npgsql persistence, outbox publishing, connector or provider clients (driven side). Adapters implement ports and contain no business rules.
   - `Contracts` (separate project, as today): the only thing other modules may reference.
3. **Small modules** may keep `Domain` and `Application` in one folder when rules are trivial; the inward-only rule still applies.
4. **Aggregates and transactions.** One use case changes one module's data in one transaction (ADR-0009 `IDbSession`). Changes in other modules follow from events, never from a cross-module transaction.
5. **Ubiquitous language.** Entity names in code match the spec glossary and data section of the owning spec.
6. **Enforcement.** Architecture tests (NetArchTest or ArchUnitNET, already planned in r0-solution-structure section 7) fail if `Domain` references infrastructure namespaces, if `Application` references an adapter, if a module references another module outside its `Contracts`, or if a module project is missing from the module map.

## Consequences and risks
- More folders and some interfaces per module. Mitigated by not requiring generic repositories, mediators or mapping libraries.
- R0 modules do not follow the layering yet. They are brought in line when next changed in R1, starting with Authorization (conditions, spec 004 FR-014), and the architecture tests for layering are enabled per module as each is converted.
- DDD tactical patterns are used where a module has real invariants (Workflow, Agents, Experience, Services). CRUD-heavy modules stay simple.

## Security and multi-tenancy implications
Tenant context remains a building block (ADR-0002, ADR-0009) set by the persistence adapter when the transaction opens; domain code never handles tenant IDs from input. Authorization stays a port to the `Authorization` contract, so no module implements its own access rules.

## Rollout and migration plan
R1: module map in place, layering for new modules from their first commit, Authorization converted with FR-014, layering tests on for converted modules. R2: all R0 modules converted. Revisit if a module needs extraction (ADR-0001, constitution XV).
