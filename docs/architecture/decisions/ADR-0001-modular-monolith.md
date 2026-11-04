# ADR-0001: Modular monolith as the initial architecture

| Field | Value |
|---|---|
| Status | Proposed |
| Date | 2026-11-04 |
| Owners | Mohamed |
| Related specs | all |

## Context and constraints
The product must evolve quickly, run in private cloud and customer on-prem environments, and be operable by customer IT teams. The team is small at the start. Domain boundaries (tenancy, identity, intranet, knowledge, workflows, AI, integrations, analytics) are known but will change.

## Decision drivers
Speed of change, operational simplicity for on-prem, strong domain boundaries, the ability to extract a module later.

## Options considered
1. **Microservices from day one**: independent scaling and deployment, but high operational cost on customer infrastructure, distributed transactions, and slower delivery.
2. **Unstructured monolith**: fastest at first, but boundaries erode and extraction becomes impossible.
3. **Modular monolith**: one ASP.NET Core API deployable plus worker deployables from one repository, with enforced module boundaries.

## Decision
Option 3. Modules own their persistence (separate PostgreSQL schemas), expose public contracts and publish events through a transactional outbox. Architecture tests enforce that no module references another module's internals or tables. Extraction to a separate service requires an ADR with measured evidence (constitution XV).

## Consequences and risks
- One release train for all modules.
- Risk of boundary erosion, mitigated by architecture tests in CI.
- Scaling is horizontal for the whole API until a module is extracted.

## Security and multi-tenancy implications
Tenant context handling is implemented once in shared building blocks and reused by all modules.

## Rollout and migration plan
Set up the solution with module projects and architecture tests in Phase 1 before any feature code.
