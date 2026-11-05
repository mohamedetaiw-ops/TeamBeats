# ADR-0011: Agent runtime inside the modular monolith

| Field | Value |
|---|---|
| Status | Proposed |
| Date | 2026-11-05 |
| Owners | Mohamed |
| Related specs | 013, 014, 020, 024, 027 |

## Context and constraints
Specs 020 and 027 need one runtime that executes every agent as a versioned configuration: build context, call models through the AI Gateway (spec 013), call tools through the tool gateway (spec 027), wait for confirmation or approval, resume after hours or days, hand off between agents, and record every step. The AI Agent Governance Plan recommends a Python runtime on LangGraph and Temporal for durable workflows.

The constitution and earlier ADRs set the frame: one ASP.NET Core application plus workers from one repository (ADR-0001), PostgreSQL with row level security (ADR-0002), all model calls through the gateway (ADR-0003, constitution VIII), the in-house outbox on RabbitMQ (ADR-0006), explicit SQL with Npgsql (ADR-0009), our own BPMN engine for long processes (ADR-0010), no non-OSI licence without an ADR, and the same code line on SaaS, private cloud and air-gapped on-premises installs.

## Options considered
1. **Python service on LangGraph, with Temporal for durable execution (governance plan).** Rich agent ecosystem and examples. Adds a second language, a second service with its own tenancy, authorization, audit and entitlement clients, and a Temporal cluster (plus its database) on every install, including on-premises. Every permission and policy check would cross a service boundary, which is where constitution VII and X are easiest to get wrong.
2. **Microsoft Agent Framework or Semantic Kernel in the .NET backend.** MIT, .NET native. Pulls in its own model connectors, which would sit outside the gateway unless wrapped, and its orchestration and memory abstractions are still changing quickly. Useful ideas, heavy coupling.
3. **Own runtime in an `Agents` module.** A small, explicit run loop (build context, call gateway, validate tool calls, call tool gateway, record step) with run state checkpointed in PostgreSQL under RLS after every step. Waiting for confirmation or approval parks the run and resumes it from the task inbox event. Background, scheduled and handoff work runs on the existing workers through the outbox. Long multi-step L4 work runs as agent tasks inside BPMN processes on the engine of ADR-0010, which already gives timers, retries, incidents and monitoring.
4. **Option 3 plus an external runtime adapter.** A customer or a later TeamBeats release can run some agents on an external runtime (for example LangGraph), but only through a contract where every model call goes through the AI Gateway and every tool call through the tool gateway, so governance does not move.

## Decision
Option 4. Build the runtime in the `Agents` module of the monolith. Agents are versioned definitions executed by pooled workers; there is no runtime, database or deployment per agent. Durable waits use PostgreSQL checkpoints and outbox events, and long workflows use the process engine of ADR-0010, so no Temporal cluster is added. No agent framework is adopted in R1; the gateway contract (spec 013) provides chat, tool calling and structured output. The external runtime adapter contract is defined in R2 and is optional.

## Consequences and risks
- We write and test the run loop, checkpointing and handoff logic ourselves. The loop is small, but it must be covered by the mandatory safety suites of spec 027 and by kill tests (worker restart mid-run).
- We do not get LangGraph's ready-made patterns. Patterns we need (routing, tool loop, human in the loop, handoff) are listed in spec 020 and are few.
- One language and one deployment keep tenant isolation, authorization, audit and entitlements in process, as for every other module.
- Python-only model tooling (for example some evaluation libraries) can still be used offline in CI for evaluation suites, outside the product runtime.

## Security and multi-tenancy implications
Run state, checkpoints and execution records carry `tenant_id` under RLS. Workers set the tenant context before resuming a run. The runtime holds no provider or connector credentials: models are reached through the AI Gateway and tools through the tool gateway, both of which check policy on every call. An external runtime, if connected later, gets no database access and no credentials beyond a scoped token to those two gateways.

## Rollout and migration plan
R1: run loop, checkpoints, confirmation park and resume, routing from the assistant, execution records. R2: scheduled runs, approvals for L3, external runtime adapter contract. R3: handoffs between agents and L4 agent tasks inside BPMN processes.
