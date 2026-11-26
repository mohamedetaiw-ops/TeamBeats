# Tasks: AI Gateway and Model Governance

Depends on 004, 005, 006, 007; 034 for the release gate on route changes. R1 covers User Stories 1 to 3, 4 (basic) and 8, FR-017 and FR-019.

## Phase 1: Foundations
- [x] T001 `AiGateway`, `AiGateway.Contracts` and `AiGateway.OpenRouter` projects; migration for catalogue (platform, RLS exception), policy, route changes, usage, debug windows (forced RLS)
- [x] T002 Permissions `ai.policy_view` with `ai.policy_manage`; audit actions of section 13; capability keys `ai.assistant`, `ai.assistant.monthly_requests`, `ai.model_choice`
- [x] T003 `IAiGateway` contract, typed results, stub adapter, `FakeOpenRouter` in `DevFakes`
- [x] T004 Architecture test: provider URLs and SDKs only inside the adapter project

## Phase 2: User Stories 1 and 2, policy and classification routing (P1)
- [x] T005 [P] [US1] Tests: catalogue view; allowlist denial sends nothing; defaults per use case; read-only without `ai.model_choice`; change effective within 60 seconds and audited; route change held for the gate
- [x] T006 [P] [US2] Tests: Confidential blocked by default and allowed only for zero retention models; Restricted never to OpenRouter; highest classification decides; missing classification rejected; localized denial keys
- [x] T007 [US1] Pipeline steps 1 to 7 and 9; policy cache; policy endpoints with `If-Match`
- [x] T008 [US1] Route change proposals, `AiModelRouteChangeProposed`, apply on `EvaluationGateDecided`, audited override

## Phase 3: User Story 3, fallback (P1)
- [x] T009 [P] [US3] Tests: retry then fallback; weaker fallback skipped; all fail gives `provider_unavailable`; circuit opens and closes with events
- [x] T010 [US3] OpenRouter adapter (chat, tools, structured output, streaming, embeddings), retries and circuit breaker

## Phase 4: User Stories 4 and 8, usage and logging (P1, P2)
- [x] T011 [P] [US4] Tests: usage by the six dimensions and denied calls; org scope; 5 user minimum for named users; no call lost on worker restart
- [x] T012 [US4] Call records through the outbox, daily aggregates, usage endpoint
- [x] T013 [P] [US8] Tests: metadata only by default; debug window for Public and Internal only, encrypted, 30 days; never for Restricted
- [x] T014 [US8] Debug windows and encrypted logs

## Phase 5: Evaluation use case and pages
- [x] T015 `evaluation` use case with the judge rule of FR-017 and platform or tenant charging
- [x] T016 Model policy, routing rules and AI usage pages with table views, in English and Arabic, RTL; e2e with axe

## Final phase
- [x] T017 SC-001 suite in CI; overhead performance test; OpenAPI; R1 implementation notes; staging allowed host and secret steps; spec evidence

## R2

Depends on 005 (quota tolerance), 015 (budget notifications), 017 (address checks), 034 (evaluator workers). R2 covers User Stories 5, 6 (adapter), 7, 4 (export and analytics) and FR-018.

### Phase 6: User Story 5, budgets and quotas (P1)
- [x] T018 [P] [US5] Tests: block, downgrade and alert actions; tenant, org unit, agent and user budgets; per-user daily limit with reset time; 80% and 100% notified once; scoped budgets need `ai.budgets.scoped`; concurrent calls within the spec 005 tolerance; cross-tenant
- [x] T019 [US5] `ai.budget` and `ai.budget_alert`, pipeline step 2, threshold notifications, audit and `AiBudgetThresholdReached`, budget endpoints
- [x] T020 [US5] Budgets page with table view, in English and Arabic, RTL; e2e with axe

### Phase 7: User Story 7, org unit policies (P2)
- [x] T021 [P] [US7] Tests: unit restricted to one model; loosening a parent rule refused with the rule named; acting unit resolved on the server; org administrators limited to their subtree
- [x] T022 [US7] `ai.org_policy`, intersection in steps 3 to 6, endpoints, Unit policies page (a unit administrator cannot open the tenant Model policy page)

### Phase 8: User Story 6, private endpoints (P1)
- [x] T023 [P] [US6] Tests: test before enable and within 24 hours; sovereign classifications never fall back to a shared provider; Restricted only on a Restricted private endpoint; Confidential embeddings only on the private endpoint; unreachable provider hidden; secret never returned; cross-tenant
- [x] T024 [US6] `AiGateway.OpenAiCompatible` adapter, shared address checks, `ai.private_endpoint`, sovereign rule, fake server in `DevFakes`
- [x] T025 [US6] Private endpoints page, in English and Arabic, RTL; e2e with axe

### Phase 9: User Story 4 R2, cost analytics and export
- [x] T026 [P] [US4] Tests: CSV export audited and scoped; forecast; display currency; 25 month aggregation
- [x] T027 [US4] Export, forecast, display currency, retention job, AI usage page additions

### Phase 10: FR-018, evaluator facade
- [x] T028 [P] Tests: token for one tenant, run and use case; expired, tampered and other tenant tokens refused; full pipeline applied; off unless enabled
- [x] T029 Internal OpenAI-compatible facade and token minting for evaluator workers

### Final phase R2
- [x] T030 OpenAPI; R2 implementation notes; spec evidence

## R3

Depends on 026 (posture, install hardening pack). R3 covers FR-020 and FR-021 (User Story 6 scenarios 5 and 6).

### Phase 11: FR-020, tenant provider keys
- [ ] T031 [P] Tests: refused in shared installs; permission; key never returned; untested key unused; tenant key used after a passed test; usage `key_owner`; removal; cross-tenant; audit
- [ ] T032 `ai.provider_key`, key endpoints and test, gateway provider target with the tenant key, `key_owner` on usage, Provider key section on the Model policy page in English and Arabic, RTL; e2e with axe

### Phase 12: FR-021, air-gapped model hosting
- [ ] T033 [P] Tests: shared providers unreachable and hidden; hosted models route and meter; Restricted only on hosted models; sovereign rule met by hosted models; hosted server down gives `provider_unavailable`; cross-tenant
- [ ] T034 `Ai:AirGapped`, `local` provider adapter, catalogue provider `local`, posture check, page notes

### Final phase R3
- [ ] T035 OpenAPI; R3 implementation notes; spec evidence
