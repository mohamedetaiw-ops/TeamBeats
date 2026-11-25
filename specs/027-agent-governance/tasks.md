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
- [x] T014 Kill tests (worker restart mid-run, `AgentRunResumeTests` from spec 020 R2); OpenAPI; R1 implementation notes; spec evidence

## R2: L3, org unit policies and the Agent Security Center

- [x] T015 [US1, US4] L3 governed execution: the "approval" rule creates an approval task in the spec 016 inbox through the spec 024 engine, with the exact parameters bound to their hash, the requesting person, the agent and its reasoning summary, shown as requested by an AI agent on behalf of the person; separation of duties; 3 working day expiry that cancels the action and tells the person; the action runs as the person only after approval; suspension and the kill switch cancel it; L3 needs `ai.agents.write_tools`; tests including cross-tenant, entitlement and expiry
- [x] T016 [US8 scenario 3] Org unit agent policies inheriting the tenant policy, only stricter, rejected with the parent rule named; tests
- [x] T017 [US6 scenarios 4 and 5, US9 scenario 3, FR-023] Agent Security Center: anomaly rules and alerts to the owner and security officer, incidents with severity, linked runs, actions and closure note, evidence export, and the watch list for High risk agents for 30 days; tests
- [x] T018 [US7 scenarios 3 and 4] A disabled connector disables its tools for every agent with a degraded notice; revoking an agent's tokens; tests
- [x] T019 Pages: approval detail, org unit policy, Security Center, in English and Arabic, RTL; e2e with axe
- [x] T020 OpenAPI; R2 implementation notes; spec evidence

## R3: SIEM export, MCP tools and L4 bounded autonomy

- [x] T021 [FR-016, spec 026 US4] SIEM export of agent events: `agent.run_finished` for every run that is not an evaluation run, the agent event catalogue in `docs/security/siem-schema.md`; tests
- [x] T022 [FR-006, edge case] MCP servers as tool sources behind `ai.agents.mcp`: `mcp_server` connections, tool discovery, approval per tool, schema changes back to pending, calls through the connector runtime; tests including cross-tenant and entitlement
- [x] T023 [US1 scenario 5, FR-015, FR-024] L4 bounded autonomy behind `ai.agents.autonomous`: agent tasks in published processes, dual approval of the process version, limits that stop the run and raise an incident; tests
- [ ] T024 Pages (MCP tools on the connection page, autonomous step approval), OpenAPI, R3 implementation notes, spec evidence

