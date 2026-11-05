# ADR-0010: Workflow engine for services and process automation

| Field | Value |
|---|---|
| Status | Proposed |
| Date | 2026-11-05 |
| Owners | Mohamed |
| Related specs | 016, 017, 021, 024 |

## Context and constraints
Spec 016 needs a workflow engine that reaches the level of Camunda-class products: BPMN 2.0 models, DMN decision tables, timers, message correlation, error and compensation handling, incidents with retry, process monitoring and version migration. It must also respect the constitution: one modular monolith with workers (ADR-0001), PostgreSQL with row level security per tenant (ADR-0002), the in-house outbox on RabbitMQ (ADR-0006), explicit SQL with Npgsql (ADR-0009), central authorization, audit and entitlements, no non-OSI licence without an ADR, and the same code line on SaaS, private cloud and air-gapped on-premises installs.

## Options considered
1. **Camunda 8 (Zeebe) as an external engine.** Most complete, with mature BPMN, DMN, Operate and Optimize. Production self-managed use needs a commercial Camunda licence (the Camunda License is source-available, not OSI). It adds a Java cluster plus Elasticsearch or OpenSearch to every install, including on-premises. Tenant isolation would live outside our PostgreSQL RLS, and tasks, audit and authorization would have to be mirrored back.
2. **Operaton or Flowable as an embedded Java sidecar.** Apache 2.0, full BPMN and DMN. Adds a JVM service and its own database schema with no RLS; every user task and permission check would cross a service boundary. Camunda 7 Community Edition itself has reached end of life.
3. **Elsa Workflows 3 inside the .NET monolith.** MIT, .NET native, durable and multi-tenant aware. Its model is its own JSON format, not BPMN, it persists through EF Core (against ADR-0009), and DMN is not included. We would still build BPMN import, DMN and most of the operator views.
4. **Own engine in a `Workflow` module that executes a defined BPMN 2.0 subset**, with DMN decision tables evaluated in process, state in PostgreSQL under RLS, jobs and timers on the existing outbox and workers, and the browser modeler built on an open source BPMN editor, subject to the licence scan.
5. **Option 4 plus an external engine adapter.** Customers who already run Camunda or another BPMN engine connect it through the connector framework (spec 017). Its user tasks appear in the TeamBeats task inbox and completing them calls back into that engine.

## Decision
Option 5. Own engine for the BPMN 2.0 and DMN subset listed in spec 024, built in stages (R2 core, R3 advanced). External engines are integrated, not embedded, so enterprises that standardized on Camunda keep it as the system of record for their processes.

## Consequences and risks
- Highest build effort of the options. Mitigated by a fixed, documented BPMN subset, conformance tests against sample models, and no custom script tasks.
- Models outside the subset are rejected on import with a list of the unsupported elements, so nothing runs half-understood.
- We do not compete with Camunda on every edge of BPMN. The target is employee services and back-office approvals, not high-throughput straight-through processing.
- The modeler editor licence must pass the CI licence gate. If it does not, a separate ADR is needed before it is used.

## Security and multi-tenancy implications
Process definitions, instances, jobs, incidents and history are tenant-owned with RLS. Service tasks only call connectors or allowlisted webhooks, never arbitrary code or scripts. Every user task goes through the central authorization service and the audit writer.

## Rollout and migration plan
R1: approval-only routes on the same engine core. R2: BPMN subset core, DMN, timers, messages, incidents, monitoring. R3: compensation, instance migration, BPMN import from other tools, external engine adapter. Revisit this ADR if more than two design-partner tenants need BPMN elements outside the subset.
