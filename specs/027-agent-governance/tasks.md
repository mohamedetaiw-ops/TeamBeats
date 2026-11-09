# Tasks: Agent Governance, Tools and Safety

Depends on 004, 005, 006, 013 (AI Gateway), 016 (inbox), 020 (definitions and runtime, same module), 034 (gate and mandatory suites). R1 covers L0 to L2 and the R1 parts of User Stories 1 to 9.

## Phase 1: Foundations
- [x] T001 Migration in `Agents` for tool definitions, agent policy, runs and run steps (forced RLS)
- [x] T002 Permissions `agent.security_view`, `agent.suspend` for security officers alongside `ai.policy_manage`; audit actions of section 13 for R1; capability `ai.agents.basic`
- [x] T003 `IToolRegistration` contract and registry with versioned parameter schemas

## Phase 2: User Stories 1 to 3, autonomy, permissions and the tool gateway (P1)
- [x] T004 [P] Tests: L0 refuses write tools; L2 waits for confirmation and submits exactly the confirmed parameters; autonomy above the tenant cap refused; intersection of user and agent permissions; revoked permission refused at execution; cross-org without a grant refused; schema errors give a short message; idempotent retry; unknown tool recorded; 20 call limit
- [x] T005 Tool gateway with the ordered checks and execution record entries
- [x] T006 Confirmation cards, checkpoints and the parameter hash

## Phase 3: User Story 5, untrusted content (P1)
- [x] T007 [P] Tests: injected document does not cause a tool call and records a detection; tool-like connector text cannot trigger a call; external images not loaded and unknown links shown as text; values from documents highlighted on the card
- [x] T008 Data envelope, detector and safe answer rendering

## Phase 4: User Stories 6 to 9, records, controls, policy and risk (P1)
- [x] T009 [P] Tests: execution record fields and masking; scoped run search; suspend and kill switch timings; rollback within 60 seconds; policy changes suspend breaking agents; risk class computed and not lowered
- [x] T010 Execution records and search; suspend, resume, kill switch and the platform switch
- [x] T011 Tenant agent policy with cache and auto-suspension; risk class computation
- [x] T012 Mandatory safety suites wired into the spec 034 gate as critical

## Phase 5: Experience
- [x] T013 Agent policy page, runs search and run timeline, confirmation card component, kill switch control, in English and Arabic, RTL; e2e with axe

## Final phase
- [ ] T014 Kill tests (worker restart mid-run); OpenAPI; R1 implementation notes; spec evidence
