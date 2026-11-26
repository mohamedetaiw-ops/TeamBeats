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

## R2 plan (budgets, org unit policies, private endpoints, cost analytics, evaluator facade)
**Date:** 2026-11-14. R2 adds User Story 5, User Story 6 (adapter part), User Story 7, the R2 part of User Story 4 and FR-018. The pipeline order of FR-002 does not change: step 2 (budget) stops being a pass-through, and steps 3 to 5 also read the org unit policy and the tenant's private models.

R2 slices, in order:
1. **Budgets and quotas (US5, FR-012):** `ai.budget` rows per tenant, org unit, agent and user (`ai.budgets` for the tenant budget, `ai.budgets.scoped` for the others). A budget is a USD amount per month, or a request count per day for a person (the per-user daily limit of scenario 3). Step 2 sums the period's spend or requests from `ai.usage_record` for every budget that matches the call (tenant, the acting unit and its ancestors, the agent, the user) and applies the strictest action: `block` denies with `budget_exhausted` (a request limit denies with `quota_exhausted` and the reset time), `downgrade` swaps the route for the named cheaper model when that model passes the same policy, `alert` lets the call through. After metering, crossing 80% and 100% sends one notification per budget, period and threshold (spec 015, category `ai_budgets`) to the AI Policy Administrators and the budget owner, writes `ai.budget_reached` and publishes `AiBudgetThresholdReached`. Concurrent calls can overshoot by at most the calls already in flight; the spec 005 tolerance (1% or 10 units) is met because the sum is read per call inside the tenant transaction and the per-user limit takes a row lock on the person's counter.
2. **Org unit policies (US7, FR-009):** `ai.org_policy` per unit with an allowlist subset, the Confidential subset, a classification ceiling, a lower output token limit and a narrower temperature range. A save is refused with the parent rule named when it loosens the tenant policy or any ancestor's policy. The gateway resolves the acting unit on the server (already done for metering) and intersects the tenant policy with every policy on the path from the root to that unit. An org administrator manages the policy of their own subtree (`ai.policy_manage` on the unit through `IAuthorizationService`).
3. **Private endpoints (US6, FR-004):** a second adapter project `AiGateway.OpenAiCompatible` for any OpenAI-compatible server (vLLM, Azure OpenAI in a customer subscription). `ai.private_endpoint` keeps the name, https base address, a secret manager reference for the key, the models with their capabilities and context size, the region and the maximum classification (up to Restricted). Registration needs `ai.models.private_endpoint`; an endpoint starts disabled and can be enabled only after a connection and capability test passed in the last 24 hours. Calls go through the same address checks as connectors (`SafeHttp`, moved to a building block so the adapter and the Integrations module share it). Private models join the planning snapshot as `private/{endpoint}/{model}` for the tenant only, so no other tenant can ever route to them. A sovereign routing rule on the tenant policy names the classifications that may go only to private endpoints: for those, shared providers are not candidates at all, so a failure ends in `provider_unavailable` and never falls back to OpenRouter. Restricted content is allowed only on a private endpoint whose ceiling is Restricted. Embeddings follow the same rule. A fake OpenAI-compatible server in `DevFakes` serves tests and development.
4. **Cost analytics and export (US4 R2):** the usage view gains month to date spend against each budget, the projected month end at the current daily rate, and cost by provider and classification (SC-002). `GET /api/v1/ai/usage/export?from=&to=` returns a CSV of the daily aggregates for the period (named users only for viewers who may see them), and the export is audited as `ai.usage_exported`. A display currency and fixed rate on the tenant policy change the shown figures only; records stay in USD. A daily job aggregates call records older than 25 months into `usage_daily` and deletes them.
5. **Evaluator facade (FR-018, ADR-0015):** `POST /internal/ai/v1/chat/completions` on the API host in OpenAI wire format, mapped only when `Ai:EvaluatorFacade:Enabled` is set and answering only on the internal host name of the deployment. It accepts a bearer token minted by the Evaluation module for one tenant, one run and the `evaluation` use case, valid for 15 minutes and signed with a key derived from the platform data key. The facade sets the tenant context from the token and runs the request through `IAiGateway` with the full pipeline, so classification, allowlist, budgets and metering apply as for any judge call.

### R2 data model (schema `ai`)
- `budget` (scope `tenant`, `org_unit`, `agent`, `user` or `each_user`, scope ID, period `month` or `day`, USD amount or request count, action `block`, `downgrade` or `alert`, downgrade model, owner, version) and `budget_alert` (budget, period start, threshold 80 or 100, unique, so each alert is sent once).
- `org_policy` (unit, document, version, updated by and at).
- `private_endpoint` (name, base address, secret reference, models with capabilities and context size, region, ceiling, status `disabled` or `enabled`, last test time and result).
- `model_policy` document gains `sovereign` (classifications kept on private endpoints) and `display_currency` with `display_rate`.

### R2 API
- `GET/POST /api/v1/ai/budgets`, `PUT/DELETE /api/v1/ai/budgets/{id}`.
- `GET/PUT /api/v1/ai/org-policies/{unitId}` (`If-Match`), `DELETE` to remove a unit's restrictions.
- `GET/POST /api/v1/ai/private-endpoints`, `PUT /{id}`, `POST /{id}/test`, `POST /{id}/enable`, `POST /{id}/disable`, `DELETE /{id}`.
- `GET /api/v1/ai/usage/export`, `GET /api/v1/ai/usage/forecast`.
- Internal only: `POST /internal/ai/v1/chat/completions`.

### R2 tests
- Integration: US5 scenarios 1 to 4 (block, downgrade, alert; 80% and 100% notifications once; per-user limit with reset time and other users unaffected; concurrent calls within tolerance); US6 scenarios 1 to 4 (test before enable, sovereign rule without fallback, unreachable provider hidden, Confidential embeddings only on the private endpoint); US7 scenarios 1 to 3 (restrict, loosening refused with the parent rule, acting unit resolved on the server); US4 scenario 4 (CSV export audited, named users only for allowed viewers); FR-018 (token scoped to one tenant and use case, expired or other tenant refused, full pipeline applied).
- Cross-tenant: tenant A's private endpoint, budgets and org policies never apply to tenant B; a facade token of tenant A cannot act in tenant B.
- Entitlement: `ai.budgets.scoped` and `ai.models.private_endpoint` off refuse the scoped budget and endpoint saves.
- Acceptance (e2e): Budgets, Private endpoints and the org unit policy panel, in English and Arabic with axe.

## R3 plan (tenant provider keys, air-gapped model hosting)
**Date:** 2026-11-26. R3 adds FR-020 and FR-021 (User Story 6 scenarios 5 and 6). The pipeline order of FR-002 does not change; step 5 (region and egress) learns which providers an install can reach and which key a tenant's call uses.

R3 slices, in order:
1. **Tenant provider keys (FR-020):** the install setting `Ai:TenantProviderKeys` (off by default, so shared installs never offer it). `ai.provider_key` keeps, per tenant and provider (`openrouter` only), the secret manager reference, the last test result and time, and who set it; the key stays in the secret manager. `GET`, `PUT` and `DELETE /api/v1/ai/provider-keys/openrouter` and `POST .../openrouter/test` change it with `ai.policy_manage` (reading needs `ai.policy_view` too); a save starts untested, and calls use the key only after a test passed (the test lists models with the key). While a tested tenant key exists, OpenRouter is reachable for that tenant even when the install has no platform key, and every call carries the tenant key in its provider target. `ai.usage_record.key_owner` records `platform` or `tenant`. Audited as `ai.provider_key_set`, `ai.provider_key_tested` and `ai.provider_key_removed`. The Private endpoints page gains an "own OpenRouter key" section shown only where the install allows it.
2. **Air-gapped model hosting (FR-021):** the install setting `Ai:AirGapped` implies `Ai:EgressBlocked` for every shared provider. A `local` provider adapter (`AiGateway.OpenAiCompatible`, `Ai:Local:BaseUrl`, optional `Ai:Local:ApiKey` from the install secret store) serves models the platform operator adds to the catalogue with provider `local`; only `local` models may have a Restricted ceiling, because they never leave the install network. Calls to hosted models use the install's private address rules (`SafeHttp`). The tenant sovereign rule is met by hosted models as well as private endpoints. The security posture of a customer-hosted install (spec 026) gains `install_ai_air_gapped`, shown as passed only on air-gapped installs, and the Model policy page shows an "air-gapped install" note. An air-gapped tenant needs no sovereign rule, since no shared provider is a candidate; where one is set, hosted models cover it as private endpoints do.

### R3 tests
- Tenant keys: refused in a shared install; set needs `ai.policy_manage`; the key is never returned; calls use the platform key until a test passes, then the tenant key; usage says who paid; removal goes back to the platform key; another tenant's calls never use it; audit entries.
- Air-gapped: OpenRouter never called and hidden from the catalogue; hosted models route and meter; Restricted allowed on hosted models only; the sovereign rule satisfied by hosted models; a hosted server down gives `provider_unavailable` with no fallback to a shared provider; cross-tenant.
