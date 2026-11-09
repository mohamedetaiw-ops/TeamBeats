# Tasks: AI Gateway and Model Governance

Depends on 004, 005, 006, 007; 034 for the release gate on route changes. R1 covers User Stories 1 to 3, 4 (basic) and 8, FR-017 and FR-019.

## Phase 1: Foundations
- [ ] T001 `AiGateway`, `AiGateway.Contracts` and `AiGateway.OpenRouter` projects; migration for catalogue (platform, RLS exception), policy, route changes, usage, debug windows (forced RLS)
- [x] T002 Permissions `ai.policy_view` with `ai.policy_manage`; audit actions of section 13; capability keys `ai.assistant`, `ai.assistant.monthly_requests`, `ai.model_choice`
- [ ] T003 `IAiGateway` contract, typed results, stub adapter, `FakeOpenRouter` in `DevFakes`
- [x] T004 Architecture test: provider URLs and SDKs only inside the adapter project

## Phase 2: User Stories 1 and 2, policy and classification routing (P1)
- [x] T005 [P] [US1] Tests: catalogue view; allowlist denial sends nothing; defaults per use case; read-only without `ai.model_choice`; change effective within 60 seconds and audited; route change held for the gate
- [x] T006 [P] [US2] Tests: Confidential blocked by default and allowed only for zero retention models; Restricted never to OpenRouter; highest classification decides; missing classification rejected; localized denial keys
- [x] T007 [US1] Pipeline steps 1 to 7 and 9; policy cache; policy endpoints with `If-Match`
- [ ] T008 [US1] Route change proposals, `AiModelRouteChangeProposed`, apply on `EvaluationGateDecided`, audited override

## Phase 3: User Story 3, fallback (P1)
- [x] T009 [P] [US3] Tests: retry then fallback; weaker fallback skipped; all fail gives `provider_unavailable`; circuit opens and closes with events
- [x] T010 [US3] OpenRouter adapter (chat, tools, structured output, streaming, embeddings), retries and circuit breaker

## Phase 4: User Stories 4 and 8, usage and logging (P1, P2)
- [x] T011 [P] [US4] Tests: usage by the six dimensions and denied calls; org scope; 5 user minimum for named users; no call lost on worker restart
- [x] T012 [US4] Call records through the outbox, daily aggregates, usage endpoint
- [x] T013 [P] [US8] Tests: metadata only by default; debug window for Public and Internal only, encrypted, 30 days; never for Restricted
- [x] T014 [US8] Debug windows and encrypted logs

## Phase 5: Evaluation use case and pages
- [ ] T015 `evaluation` use case with the judge rule of FR-017 and platform or tenant charging
- [x] T016 Model policy, routing rules and AI usage pages with table views, in English and Arabic, RTL; e2e with axe

## Final phase
- [ ] T017 SC-001 suite in CI; overhead performance test; OpenAPI; R1 implementation notes; staging allowed host and secret steps; spec evidence
