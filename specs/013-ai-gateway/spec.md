# Feature Specification: AI Gateway and Model Governance

| Field | Value |
|---|---|
| Spec ID | 013-ai-gateway |
| Area | ai |
| Status | Proposed |
| Version | 0.1 |
| Owner | Mohamed |
| Approver | Mohamed |
| Target release | R1 (gateway pipeline, OpenRouter adapter, tenant model policy, classification routing, fallback, metering, usage view, `evaluation` use case and release gate on model route changes), R2 (budgets and alerts, org unit policies, private model endpoint adapter, cost analytics, evaluator worker facade), R3 (air-gapped model hosting, per-tenant provider keys for dedicated installs) |
| Last updated | 2026-11-05 |
| Requirement IDs | AI-002, AI-003, AI-004, AI-006, AI-011, AI-012, AI-013, AIG-001 to AIG-019 (new) |
| Use cases | UC-06, UC-07, UC-12, UC-13 |
| Depends on | ADR-0003 (AI Gateway), 004 (authorization), 005 (entitlements), 006 (audit), 002 (hierarchy, for org unit policies), 021 (analytics), 034 (release gates for model route changes) |
| Used by | 014 (assistant and RAG), 020 (Agent Hub), 027 (agent governance), 034 (AI evaluation judge calls), every module that calls a model |

## 1. Problem and outcome

**Problem:** Every AI feature and every agent needs a model. If modules call providers on their own, nobody can say which provider saw which data, what it cost, or how to switch a government tenant to a private model. Customers also differ: one is happy with OpenRouter for internal content, another must keep confidential data on its own servers, and a third has a fixed monthly AI budget per department.

**Outcome:** One internal AI Gateway handles every chat, completion, tool calling and embedding request in the product. Tenant administrators choose which models may be used, for which data classifications and use cases, with which fallbacks and budgets. The gateway refuses any call that breaks the policy before it leaves the product, meters every call by tenant, org unit, user, use case, agent, model and provider, and keeps working in a clear degraded mode when a provider fails. Sovereign customers route sensitive work only to model endpoints they host or approve.

## 2. Scope

**In scope:**
- Gateway pipeline for chat, completion, structured output, tool calling, streaming and embeddings.
- Provider adapters: OpenRouter (R1), generic OpenAI-compatible private endpoint for customer-hosted models such as vLLM or Azure OpenAI in a customer subscription (R2), and a stub adapter for tests (R0 onward).
- Platform model catalogue: model, provider, capabilities, regions, retention and training terms, maximum classification, price.
- Tenant model policy: allowlist, default model per use case, fallback chain, classification routing, region and egress rules, inference parameter limits.
- Policy inheritance from tenant to organization units, where children may only restrict.
- Prompt and response logging policy, redaction and context limits.
- Metering, quotas, budgets, alerts and the AI usage and cost view (UC-13).
- Degraded mode when providers are blocked or down.

**Out of scope:**
- Retrieval, indexing and citations (spec 014).
- Agent definitions, tools and approvals (specs 020 and 027). Agents call models only through this gateway.
- Training or fine-tuning models.
- Measuring model and agent quality (spec 034). The gateway serves judge calls for it and waits for its gate before a model route change applies.
- Hosting model weights in the TeamBeats cluster. Customers host their own inference; the gateway connects to it.

## 3. Actors and permissions

| Actor | What they can do | Permission / scope |
|---|---|---|
| Platform operator | Maintain the model catalogue and provider adapters, set platform baseline rules, block a model for all tenants | Platform |
| Tenant administrator | Enable providers, set tenant credentials where allowed, assign the AI Policy Administrator role | Tenant |
| AI Policy Administrator | Edit the tenant model policy, routing rules, budgets and logging policy; view usage and cost | `ai.policy.manage` at tenant scope |
| Organization administrator | Restrict the model policy and set budgets for their unit and below | `ai.policy.manage` at org scope, restrict only |
| Security officer | View policy, denied calls and usage; export evidence | `ai.policy.view`, `audit.read` |
| Module or agent runtime | Send model requests with tenant, user, use case, classification and agent context | System, through the gateway contract only |

## 4. User scenarios

### User Story 1: Set the tenant model policy (Priority: P1, R1)

Layla, AI Policy Administrator at Acme, allows two models through OpenRouter for Public and Internal content, sets one as the default for the assistant and a cheaper one for summaries, and blocks everything else.

**Independent test:** save a policy with an allowlist and defaults, send requests for each use case, and check which model served each one.

**Acceptance scenarios:**
1. **Given** the platform catalogue, **When** Layla opens Model policy, **Then** she sees each model with provider, regions, retention terms, the highest classification the platform allows for it, and price per million tokens.
2. **Given** an allowlist of two models, **When** a module requests a model outside it, **Then** the gateway denies the call with reason `model_not_allowed` and nothing is sent to a provider.
3. **Given** default models per use case (assistant, summarize, translate, draft, embed, agent, evaluation), **When** a module sends a request without naming a model, **Then** the use case default is used.
4. **Given** a package without `ai.model_choice`, **When** Layla opens Model policy, **Then** she can view the platform default model set but cannot change it, and the page says why.
5. **Given** a policy change, **When** she saves it, **Then** it applies to new calls within 60 seconds and an audit event records old and new values.
6. **Given** a change that alters the model or fallback chain behind a use case used by published agents or the assistant, **When** she saves it, **Then** it is held as proposed while the release gate of spec 034 runs, and applies within 60 seconds of a gate with no critical failure; blocking failures are listed and she can apply with a reason. Changes that only restrict (removing a model from the allowlist for a use case that has another eligible model) also run the gate, because the fallback model changes.

### User Story 2: Route by data classification (Priority: P1, R1)

**Acceptance scenarios:**
1. **Given** the default policy, **When** a request carries Confidential content, **Then** it is denied for external providers with reason `classification_blocked`, unless the administrator has allowed Confidential for a specific provider and model that the catalogue marks as zero retention and no training.
2. **Given** any policy, **When** a request carries Restricted content, **Then** it can only go to a private endpoint the tenant has marked as approved for Restricted; OpenRouter is never eligible, and this cannot be changed by tenant policy.
3. **Given** a request whose context mixes Internal and Confidential items, **When** it is routed, **Then** the highest classification in the request decides the route.
4. **Given** a request with no classification, **When** it reaches the gateway, **Then** it is rejected as a programming error (`classification_missing`) and logged for engineering, never defaulted to Public.
5. **Given** a denial, **When** the calling feature shows it, **Then** the user sees a plain message that the content cannot be processed by AI under the organization's policy, in their language.

### User Story 3: Fall back when a provider fails (Priority: P1, R1)

**Acceptance scenarios:**
1. **Given** a fallback chain of model A then model B, **When** model A times out or returns a provider error, **Then** the gateway retries once on A for retryable errors and then calls B, and the response records that a fallback was used.
2. **Given** model B is not allowed for the request's classification, **When** A fails, **Then** B is skipped. A fallback never goes to a provider with weaker policy than the request needs.
3. **Given** all eligible models fail, **When** the request ends, **Then** the caller receives `provider_unavailable` and shows its degraded state (constitution XII); the rest of the page keeps working.
4. **Given** repeated failures on a provider, **When** the error rate passes the threshold, **Then** a circuit breaker opens for that provider for the tenant set it serves, and operators get an alert.

### User Story 4: See AI usage and cost (Priority: P1, R1 basic, R2 full)

**Acceptance scenarios:**
1. **Given** AI use in the last 30 days, **When** Layla opens AI usage, **Then** she sees requests, tokens and estimated cost by day, use case, model, provider, org unit and agent, plus denied calls by reason. *(AI-013, UC-13)*
2. **Given** an organization administrator for Acme Gulf, **When** they open AI usage, **Then** they see only their unit and its children.
3. **Given** per-user figures, **When** they are shown, **Then** only the AI Policy Administrator and security officer can see named users; others see aggregates with at least 5 users per group.
4. **Given** the R2 export, **When** Layla exports usage, **Then** she gets a CSV for the chosen period and the export is audited.

### User Story 5: Budgets and quotas (Priority: P1, R2)

**Acceptance scenarios:**
1. **Given** the package quota `ai.assistant.monthly_requests` and a tenant budget of 2,000 USD per month, **When** either is reached, **Then** the configured action applies: block, or switch to a cheaper allowed model, or allow and alert.
2. **Given** a budget for the HR org unit or for one agent, **When** 80% and 100% are reached, **Then** the AI Policy Administrator and the budget owner are notified (spec 015).
3. **Given** a per-user daily limit, **When** a user reaches it, **Then** their next request is denied with `quota_exhausted` and a reset time, and other users are not affected.
4. **Given** concurrent requests near a limit, **When** they are counted, **Then** overshoot stays within the tolerance set in spec 005.

### User Story 6: Use a private model endpoint (Priority: P1, R2 adapter, R3 air-gapped)

A government tenant runs its own inference servers and must never send Confidential or Restricted content outside its network.

**Acceptance scenarios:**
1. **Given** `ai.models.private_endpoint`, **When** the tenant administrator registers an OpenAI-compatible endpoint with its URL, credentials in the secret manager, models, region and maximum classification, **Then** the gateway runs a connection and capability test before it can be enabled.
2. **Given** a sovereign routing rule "Confidential and Restricted only to private endpoints", **When** an agent or the assistant sends such content, **Then** only the private endpoint is eligible, and if it is down the call fails instead of falling back to OpenRouter.
3. **Given** a dedicated or on-premises install with egress blocked, **When** OpenRouter is configured, **Then** the gateway reports it as unreachable by design and the policy page hides it.
4. **Given** embeddings for search, **When** a sovereign tenant indexes Confidential content, **Then** embeddings are produced only by an allowed private endpoint.

### User Story 7: Restrict policy for an organization unit (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** the tenant allows models A, B and C, **When** the Acme Gulf administrator restricts their unit to A, **Then** requests from users acting in Acme Gulf can use only A.
2. **Given** a child unit policy, **When** its administrator tries to add a model the parent does not allow, or allow a classification the parent blocks, **Then** the save is rejected with the parent rule named.
3. **Given** a user who belongs to two units with different policies, **When** they make a request, **Then** the acting unit is resolved on the server: the unit that owns the resource or service the request is about, else the user's primary membership (spec 002). The unit picked in the UI is never trusted on its own (constitution II). If the request concerns resources of several units, the strictest of their policies applies.

### User Story 8: Control prompt and response logging (Priority: P2, R1)

**Acceptance scenarios:**
1. **Given** a new tenant, **When** calls run, **Then** only metadata is logged: time, tenant, user, use case, agent, model, provider, token counts, latency, cost, policy decision, classification and correlation ID. *(AI-012)*
2. **Given** a time-limited debug window enabled by the AI Policy Administrator for one use case, **When** calls run, **Then** full prompts and responses are stored encrypted for that window only, for Public and Internal content only, visible to the security officer, and deleted after 30 days.
3. **Given** Restricted content, **When** any logging setting is on, **Then** the full text is never stored.

### Edge cases
- A model is removed from the catalogue or deprecated by the provider: tenants using it get a warning 30 days before, the release gate of spec 034 runs on the next model in the fallback chain during that period, and calls move to it on the removal date.
- Provider changes its retention terms: the platform operator updates the catalogue entry, which immediately re-evaluates which classifications may use it.
- Streaming response cut off mid-way: the caller gets a partial flag and usage is metered for the tokens received.
- Clock or currency: cost is estimated in USD from catalogue prices; tenants can display another currency at a fixed rate they set.
- A request bigger than the model's context window: the gateway rejects it with `context_too_large`; trimming is the caller's job (spec 014 decides what to drop).
- A provider returns tool calls for a tool that was not offered: the gateway drops them and records a policy event.

## 5. Functional requirements

- **AIG-001 / FR-001**: Every model and embedding call in the product MUST go through the gateway contract. Provider SDKs and DTOs MUST stay inside adapter projects. *(AI-002, constitution VIII)*
- **AIG-002 / FR-002**: The gateway MUST apply, in this order: entitlement and quota check, budget check, classification policy, provider and model allowlist, region and egress rules, redaction and context limits, routing and fallback, provider call, output checks, metering and telemetry. A failed step MUST stop the call with a machine-readable reason. *(ADR-0003)*
- **AIG-003 / FR-003**: Each request MUST carry tenant, acting org unit (resolved as in US7 scenario 3), user or service identity, use case, agent and agent version (if any), classification and correlation ID. Tenant and classification MUST come from server context, never from model output or the client. *(constitution I)*
- **AIG-004 / FR-004**: The OpenRouter adapter MUST be available in R1, and the adapter contract MUST support a private OpenAI-compatible endpoint without changes to calling modules. *(AI-003, AI-011)*
- **AIG-005 / FR-005**: The platform MUST keep a model catalogue with capabilities (chat, tools, structured output, vision, embeddings), context size, regions, retention and training terms, maximum classification and price.
- **AIG-006 / FR-006**: Tenants MUST be able to set an allowlist, a default model per use case, a fallback chain and inference parameter limits (maximum output tokens, temperature range). *(AI-004)*
- **AIG-007 / FR-007**: Restricted content MUST NOT be sent to an external shared provider under any tenant setting. Confidential content MUST be blocked from external providers by default. *(constitution IX)*
- **AIG-008 / FR-008**: Fallback MUST only choose models that satisfy the same policy as the original route. *(new)*
- **AIG-009 / FR-009**: Organization unit policies MUST only restrict the parent policy. A save that loosens a parent rule MUST be rejected.
- **AIG-010 / FR-010**: Full prompt and response logging MUST be off by default, limited to time-boxed windows for Public and Internal content, and never available for Restricted content. *(AI-012)*
- **AIG-011 / FR-011**: Every call MUST be metered by tenant, org unit, user, use case, agent, model and provider, with input tokens, output tokens, latency and estimated cost. *(AI-006)*
- **AIG-012 / FR-012**: Budgets MUST be settable per tenant, org unit, agent and user, with actions block, downgrade to a named cheaper model, or alert only, and notifications at 80% and 100%.
- **AIG-013 / FR-013**: The AI usage view MUST show usage, cost and denied calls with the filters in US4, scoped to the viewer's org scope. *(AI-013)*
- **AIG-014 / FR-014**: Provider credentials MUST live in the secret manager and MUST NOT appear in configuration screens after saving, logs, prompts or exports. *(constitution IV)*
- **AIG-015 / FR-015**: The gateway MUST keep a circuit breaker per provider and adapter, and expose provider health to operators.
- **AIG-016 / FR-016**: Callers MUST receive a typed result that distinguishes `not_in_package`, `quota_exhausted`, `budget_exhausted`, `classification_blocked`, `model_not_allowed`, `region_blocked`, `provider_unavailable`, `context_too_large`, so each feature can show the right degraded state.
- **AIG-017 / FR-017**: The gateway MUST support the use case `evaluation` for judge calls from spec 034, with no tools, a tenant default judge model per classification, and a check that the judge differs from the model under test named in the request. Calls MUST be metered as evaluation, charged to the platform for platform and mandatory suites run by a release gate, and to the tenant otherwise.
- **AIG-018 / FR-018**: The gateway MUST offer an internal OpenAI-compatible facade for sandboxed evaluator workers (ADR-0015), reachable only inside the deployment, accepting only short-lived tokens scoped to one tenant and the `evaluation` use case, and applying the full pipeline of FR-002. *(R2)*
- **AIG-019 / FR-019**: Changes to the model or fallback chain behind a use case MUST publish `AiModelRouteChangeProposed` and apply only after `EvaluationGateDecided` from spec 034 reports no critical failure, or after an audited override for blocking failures. Catalogue removals and provider deprecations MUST run the same gate before the removal date.

## 6. Business and validation rules
- Platform baseline rules (Restricted never to shared providers, metadata-only logging default, classification required) cannot be changed by any tenant.
- A tenant must keep at least one allowed model per enabled use case, or that use case shows its disabled state.
- Private endpoint registration needs a successful test in the last 24 hours before it can be set as the only route for a classification.
- Price changes in the catalogue apply to new calls only; past cost figures are not recalculated.

## 7. Tenant and organization scope
- Model policy, budgets, private endpoints, usage records and logs are tenant-owned with RLS. The catalogue and adapters are platform-level.
- Cross-tenant: a tenant's private endpoint can never serve another tenant's request, and usage of tenant A is never visible to tenant B.
- Org boundary: org unit policies and budgets apply to the acting unit; usage views follow the viewer's scope.

## 8. Data, ownership and classification

| Entity | System of record | Classification | Retention | Personal data? |
|---|---|---|---|---|
| Model catalogue, adapters | Product (platform) | Internal | Versions kept | No |
| Tenant model policy, org unit policy, routing rules | Product | Internal | Life of tenant, history in audit | No |
| Private endpoint registration | Product, credentials in secret manager | Confidential | Life of tenant | No |
| Usage records | Product | Internal | 25 months, then aggregated | Yes (user ID) |
| Debug prompt logs | Product (encrypted) | Same as content, max Internal | 30 days | Yes |

## 9. Package entitlements

| Capability key | Behavior |
|---|---|
| `ai.assistant` | All packages. Gateway available for all AI features |
| `ai.assistant.monthly_requests` | Quota per package (commercial data) |
| `ai.model_choice` | Workplace Plus and above: tenant can change allowlist, defaults and fallback. Workplace uses the platform default set |
| `ai.models.private_endpoint` | Workplace Plus and above: register private model endpoints and sovereign routing |
| `ai.budgets` | All packages: tenant budget |
| `ai.budgets.scoped` | Workplace Plus and above: org unit, agent and user budgets |

## 10. UX, localization and accessibility
- Pages: Model policy, Routing rules, Private endpoints, Budgets, AI usage. All under the tenant admin shell (spec 006).
- Denial and degraded messages are localized strings keyed by reason, never raw provider errors.
- Charts in AI usage have a table view and meet WCAG 2.2 AA; numbers use the tenant digit setting (spec 007).

## 11. AI behavior
The gateway is the policy point for all AI behavior. It does not decide what context to send (callers do, after authorization per constitution VII); it decides whether and where that context may go.

## 12. Integrations and events
Publishes `AiPolicyChanged`, `AiModelRouteChangeProposed`, `AiBudgetThresholdReached`, `AiProviderCircuitOpened`, `AiProviderCircuitClosed`. Consumes `EntitlementsChanged` (spec 005) and `EvaluationGateDecided` (spec 034). The gateway never retrieves content, so it does not consume permission events.

## 13. Audit

| Event | Type | Key fields |
|---|---|---|
| ai.policy_changed / org_policy_changed | audit | scope, old and new values, actor |
| ai.endpoint_registered / tested / enabled / disabled | audit | endpoint, actor, result |
| ai.budget_changed / budget_reached | audit | scope, amount, action |
| ai.logging_window_opened / closed | audit | use case, duration, actor |
| ai.call_denied | operational | reason, use case, agent, model requested |
| ai.call | operational (metadata) | fields in US8 scenario 1 |

## 14. Security and abuse cases
- Model or provider spoofing through request parameters: model names are resolved against the catalogue and tenant policy server-side; unknown names are rejected.
- Data exfiltration by choosing a weak provider: classification routing and fallback rules (FR-007, FR-008).
- Cost abuse by one user or a looping agent: per-user and per-agent budgets and rate limits; agent loop limits in spec 027.
- Credential leakage: secret manager only, adapters receive short-lived handles where the provider supports it.
- Provider response carrying injected instructions: treated as untrusted output; callers apply spec 027 output handling before acting on it.

## 15. Non-functional requirements
- Gateway overhead under 50 ms at p95 excluding the provider call.
- Policy changes effective within 60 seconds.
- Metering loses no call records on worker restart (outbox).
- Design-to scale: 200 model calls per second per installation in R2.

## 16. Success criteria
- **SC-001**: A test suite proves that no Confidential or Restricted fixture reaches the OpenRouter adapter under default policy, on every build.
- **SC-002**: An AI Policy Administrator can answer "which provider saw HR content last month and what did it cost" from the AI usage view in under 5 minutes.
- **SC-003**: Switching a tenant from OpenRouter to a private endpoint requires no code change and no restart.

## 17. Open questions
1. AI credentials model (foundation open decision): does the platform hold one OpenRouter key for shared installs, or can tenants bring their own key? Default: platform key in shared installs, tenant key allowed in dedicated and on-premises installs (R3).
2. Should Workplace tenants be able to set org unit budgets? Default: no, tenant budget only.

## 18. Traceability

| Requirement ID | Covered by |
|---|---|
| AI-002 | AIG-001, AIG-002 |
| AI-003, AI-011 | AIG-004, US6 |
| AI-004 | AIG-006, AIG-009, US1, US7 |
| AI-006 | AIG-011 |
| AI-012 | AIG-010, US8 |
| AI-013 | AIG-013, US4 |
| UC-12 | AIG-002, US5 |
| UC-13 | US4, section 13 |
| Spec 034 release gates and judge calls | AIG-017 to AIG-019, US1 scenario 6 |
