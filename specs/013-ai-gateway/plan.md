# Implementation Plan: AI Gateway and Model Governance

**Spec:** `specs/013-ai-gateway/spec.md` (v1.0, Approved) | **Date:** 2026-11-07
**Shared base:** ADR-0003 (AI Gateway, OpenRouter first), ADR-0013, ADR-0015 (evaluation architecture), module map (`AiGateway`)

## Summary
R1 builds the gateway pipeline of FR-002 for chat, structured output, tool calling, streaming and embeddings; the OpenRouter adapter and a stub adapter; the platform model catalogue; the tenant model policy with allowlist, defaults per use case, fallback chain and parameter limits (User Story 1); classification routing (User Story 2); fallback and circuit breakers (User Story 3); metering and the basic AI usage view (User Story 4, R1 part); metadata-only logging with time-boxed debug windows (User Story 8); the `evaluation` use case (FR-017); and route changes held for the spec 034 release gate (FR-019). Budgets, org unit policies, private endpoints, CSV export and the evaluator facade are R2.

R1 slices, in order:
1. **Contract and pipeline:** `IAiGateway` in `AiGateway.Contracts`, the ordered pipeline, typed results (FR-016), the stub adapter.
2. **Catalogue and policy:** platform catalogue, tenant policy, policy cache with 60 second effect.
3. **OpenRouter adapter:** chat, tools, structured output, streaming, embeddings, retries, circuit breaker, health.
4. **Metering and usage:** call records through the outbox, the usage view with org scope and the 5 user minimum.
5. **Logging windows and route gate:** debug windows, `AiModelRouteChangeProposed` and applying on `EvaluationGateDecided`.
6. **Admin pages:** Model policy, Routing rules, AI usage, in English and Arabic, RTL.

## Constitution check
| Principle | Pass | Evidence |
|---|---|---|
| I tenant isolation | Yes | Policy, usage and logs tenant-owned with forced RLS; tenant and classification come from server context (FR-003), never from the client or model output |
| III authorization | Yes | `ai.policy_manage`, `ai.policy_view` through `IAuthorizationService`; usage view scoped to the viewer's org scope |
| VIII single gateway | Yes | Architecture test: no provider SDK or provider URL outside the adapter project; every module calls `IAiGateway` |
| IX classification | Yes | Restricted never to shared providers (platform baseline, not configurable); Confidential blocked by default; missing classification rejected |
| XII degraded states | Yes | Typed results let callers show degraded states; circuit breaker per provider |
| XXI entitlements | Yes | `ai.assistant`, `ai.assistant.monthly_requests` quota, `ai.model_choice` checked in the pipeline |
| Rule 9 secrets | Yes | The OpenRouter key is platform configuration from the secret store, never in logs, prompts or exports |

## Contract (`AiGateway.Contracts`)
- `IAiGateway.ChatAsync(AiRequest)`, `StreamChatAsync(AiRequest)`, `EmbedAsync(EmbeddingRequest)`.
- `AiRequest`: use case (`assistant`, `summarize`, `translate`, `draft`, `embed`, `agent`, `evaluation`), messages, tools, response schema, optional model name, max tokens, temperature, classification (required), agent and agent version, model under test (for evaluation), correlation ID. Tenant, acting unit and user come from `ITenantContext`, never from the request object.
- `AiResult`: `Ok(content, toolCalls, usage, model, provider, fallbackUsed, partial)` or `Denied(reason)` with the reasons of FR-016.
- Tool calls for tools not offered are dropped and recorded (edge case).

## Pipeline (FR-002, in order)
1. Entitlement and quota (`ai.assistant`, `ai.assistant.monthly_requests` through `IEntitlementService`).
2. Budget check: a pass-through step in R1 (budgets are R2), kept so the order does not change.
3. Classification policy: `classification_missing` is a programming error (logged for engineering); highest classification of the request decides; Restricted only to approved private endpoints (none in R1, so denied); Confidential only where the tenant allowed a catalogue model marked zero retention and no training.
4. Allowlist and defaults: model resolved against the catalogue and tenant policy server side; unknown names rejected.
5. Region and egress: catalogue regions against the tenant's rule; installs with egress blocked report OpenRouter unreachable by design.
6. Context limits: `context_too_large` from the catalogue context size (token estimate from characters, with the adapter's count when returned); parameter limits clamp max tokens and temperature.
7. Routing and fallback: one retry on retryable errors, then the next eligible model in the chain that satisfies the same policy (FR-008).
8. Provider call through the adapter.
9. Output checks: drop unoffered tool calls; validate structured output against the schema.
10. Metering and telemetry: one call record per request, written in the same transaction as an outbox message so worker restarts lose nothing.

## Data model (schema `ai`)
- Platform (RLS exception, listed in `rls-exceptions.md`): `model_catalogue` (model, provider, capabilities, context size, regions, retention, training, max classification, prices per million tokens, status, deprecation date).
- Tenant: `model_policy` (version, allowlist, defaults per use case, fallback chains, Confidential allowances, parameter limits, region rule, logging setting), `route_change` (proposed policy, gate request ID, status, override reason), `usage_record` (time, unit, user, use case, agent, model, provider, tokens, latency, cost, decision, classification, correlation ID; 25 months then aggregated), `usage_daily` (aggregates), `debug_window` and `debug_log` (encrypted with the platform data key, Public and Internal only, 30 days).

## OpenRouter adapter
- HTTP client to `https://openrouter.ai/api/v1` with the platform key `Ai:OpenRouter:ApiKey` from the secret store (open question 1 default: platform key in shared installs). Staging needs `openrouter.ai` on its allowed hosts and the key set as a Fly secret.
- A fake OpenRouter (`FakeOpenRouter` handler in `DevFakes`) for tests and dev, as for Microsoft Graph, with faults (timeouts, 429, 5xx, cut streams) and deterministic replies.
- Circuit breaker per provider: opens after 50% errors over 20 calls in 1 minute, half-open after 30 seconds; publishes `AiProviderCircuitOpened` and `AiProviderCircuitClosed`.

## Policy changes and the release gate (US1 scenario 6, FR-019)
Saving a policy computes, per use case, the effective route before and after. If any route used by published agents or the assistant changes, the change is stored as `route_change` with status `proposed`, `AiModelRouteChangeProposed` is published, and the current policy stays in force. `EvaluationGateDecided` from spec 034 applies it (within 60 seconds) when no critical failure is reported, or keeps it with the blocking failures listed for an audited override with a reason. Changes that do not alter any route apply at once. Until the spec 034 gate is built, proposed changes wait and the page says the gate is not available; the AI Policy Administrator can apply with an audited override.

## Usage view (US4, R1 part)
`GET /api/v1/ai/usage?from=&to=&groupBy=` reads `usage_daily` for requests, tokens and estimated cost by day, use case, model, provider, org unit and agent, plus denied calls by reason. Org-scoped viewers see their subtree only. Named users only for `ai.policy_manage` and security officers; others get groups with at least 5 users. Table view for every chart.

## API (v1)
- `GET /api/v1/ai/catalogue`, `GET/PUT /api/v1/ai/policy` (`If-Match`), `GET /api/v1/ai/route-changes`, `POST /api/v1/ai/route-changes/{id}/override {reason}`.
- `GET /api/v1/ai/usage`, `POST /api/v1/ai/debug-windows`, `DELETE /api/v1/ai/debug-windows/{id}`.
- Platform: `GET/PUT /api/v1/platform/ai/catalogue` for the operator.

## Tests
- SC-001 suite on every build: no Confidential or Restricted fixture reaches the OpenRouter adapter under default policy.
- Integration: US1 scenarios 1 to 6, US2 scenarios 1 to 5, US3 scenarios 1 to 4, US4 scenarios 1 to 3, US8 scenarios 1 to 3; FR-017 judge differs from the model under test.
- Cross-tenant: tenant A's policy, usage and logs never visible or applied in tenant B.
- Architecture: no provider URL or SDK outside the adapter; every module that calls a model references `AiGateway.Contracts` only.
- Performance: gateway overhead under 50 ms p95 excluding the provider call, against the stub adapter.
- Acceptance (e2e): edit model policy, see a held route change, read AI usage, in English and Arabic with axe.
