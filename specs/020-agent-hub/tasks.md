# Tasks: Agent Hub (Registry, Templates, Studio and Marketplace)

Depends on 004, 005, 006, 013, 016, 027 (same module), 034; 012 (search source), 014 (assistant). R1 covers User Stories 1 to 3, 5, 7 and 8 (tasks).

## Phase 1: Foundations
- [x] T001 `Agents` and `Agents.Contracts` projects; migration for agents, versions, templates (platform, RLS exception) and checkpoints (forced RLS)
- [x] T002 Permissions `agent.design`, `agent.publish`; audit actions of section 13 for R1; capability keys for templates
- [x] T003 Definition schema and validator with unit tests

## Phase 2: Runtime
- [ ] T004 [P] Tests: run loop with the fake OpenRouter; tool calls through the gateway; confirmation checkpoint; restart resumes; streaming
- [ ] T005 Run loop, checkpoints, worker resumption and server-sent events

## Phase 3: User Stories 1 to 3 and 5, templates and lifecycle (P1)
- [x] T006 [P] [US1] Tests: template library by package with "not ready"; edits within policy limits; sources limited to what the designer can read; test panel executes no writes; submit locks and creates an inbox task
- [x] T007 [US1] Six launch templates with Arabic and English instructions; install and edit
- [x] T008 [P] [US2] [US5] Tests: review diff with risk class and gate results; publish blocked by the gate; last editor cannot approve Medium or High; `agent.design` alone cannot publish; new version used within 60 seconds
- [x] T009 [US2] Submit, review, gate request and publish
- [x] T010 [P] [US3] Tests: rollback re-checks policy; suspend hides and stops; retire keeps history
- [x] T011 [US3] Rollback, suspend, retire

## Phase 4: User Stories 7 and 8, reach (P1)
- [x] T012 [US7] Routing endpoint for the assistant and contextual actions with item permissions; routing in the execution record
- [x] T013 [US8] Prepared drafts as inbox tasks marked "prepared by AI"

## Phase 5: Experience
- [x] T014 Registry, template library, agent editor with test panel, review page and the conversation component, in English and Arabic, RTL; e2e with axe

## Final phase
- [ ] T015 OpenAPI; R1 implementation notes; spec evidence

## R2
- [x] T016 [US4] Studio: blank agents within `ai.agents.custom` and `ai.agents.max_custom`, clone with confirmed copies, tool picker by policy and scope, Studio sections and read-only JSON view; tests
- [x] T017 [US6] Marketplace: listing by audience mode, request access through the inbox, favourites, ratings with private comments; tests
- [x] T018 [US9] Persistent memory: policy switch, consent, `memory.remember`, My agents page, retention, leaver and withdrawal deletion; tests
- [x] T019 [US8] Briefings: policy switch with risk classes, subscription, worker running as the user, notification delivery; tests
- [x] T020 Department templates 7 to 12 with suites, L3 ones "not ready" until spec 027 L3
- [ ] T021 Runtime: streamed agent answers and resumption after restart (closes T004, T005)
- [ ] T022 Detailed agent analytics through spec 021 under `analytics.advanced`
- [ ] T023 OpenAPI; R2 implementation notes; spec evidence
