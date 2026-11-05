# ADR-0003: Internal AI Gateway with OpenRouter as the first provider adapter

| Field | Value |
|---|---|
| Status | Proposed |
| Date | 2026-11-04 |
| Owners | Mohamed |
| Related specs | 013 (AI Gateway), 014, 020, 027, 005 |

## Context and constraints
AI is part of every package. Tenants need control over which models and providers see which data classifications. OpenRouter offers access to many models through one API. Government and regulated customers may require in-country or local inference.

## Decision drivers
Provider portability, tenant policy enforcement, metering, observability, data minimization.

## Options considered
1. **Call providers directly from modules**: fast to start, impossible to govern.
2. **Single provider SDK everywhere**: lock-in, no policy layer.
3. **Internal AI Gateway with provider adapters**, OpenRouter first.

## Decision
Option 3. All chat, completion and embedding calls go through the gateway, which applies in order: entitlement and quota check, classification policy, provider and model allowlist, redaction and context limits, routing, call, metering and telemetry. The adapter contract must be generic enough for a local or in-region provider without changes to calling modules.

## Consequences and risks
- Single gateway is a critical path. AI unavailability must degrade gracefully (constitution XII).
- OpenRouter cannot serve air-gapped installs, and provider retention terms vary by model. Restricted data is blocked by default.
- Review finding I2 recommends planning a second adapter for the first government pilot.

## Security and multi-tenancy implications
The gateway receives already-authorized context only (constitution VII). Prompts and responses are not logged in full by default. Provider credentials live in the secret manager.

## Rollout and migration plan
Gateway contract and OpenRouter adapter in the AI foundation phase. A stub adapter is available from Phase 1 for tests.
