# Spec 014 evidence (R1)

The spec stays Approved, not Implemented, until the assistant panel in the shell (T007) is built and the R2 stories (US5 to US8, FR-015 to FR-018, FR-022) are done. Defaults, deviations and gaps are in the spec 014 section of `docs/architecture/r1-implementation-notes.md`.

| Requirement | Status | Evidence |
|---|---|---|
| FR-001 from every page and as a full page | Full page met; shell panel pending (T007) | `assistant.spec.ts` (en and ar, axe) |
| FR-002 retrieval as the user before the prompt | Met | `AssistantTests.Answers_cite_only_passages_of_the_turn_that_the_person_may_read` |
| FR-003 passage citations, and no evidence said so | Met | `AssistantTests.Answers_cite_only_passages_...`, `AssistantPromptTests.Only_markers_of_passages_in_the_turn_survive_in_first_appearance_order`, `assistant.spec.ts` |
| FR-004 gateway with the highest classification of the context | Met | `AssistantPromptTests.The_highest_classification_of_the_context_decides`, `AssistantTests.Answers_cite_only_passages_...` (confidential content withheld by policy) |
| FR-005 no tools; actions only from routed agents | Met | `AgentRoutingTests.The_assistant_routes_to_one_agent_the_person_may_use_and_records_the_decision_in_the_run`, `An_agents_action_card_reaches_the_conversation_and_the_kill_switch_empties_routing` |
| FR-006 routing only to usable agents, recorded | Met | `AgentRoutingTests.The_assistant_routes_to_one_agent_...`, `People_outside_the_audience_get_no_routing_and_cannot_pick_the_agent` |
| FR-007 text actions on readable items or own text, labelled | Met | `AssistantTests.Text_actions_work_only_on_items_the_person_may_read_or_on_their_own_text`, `AgentRoutingTests.A_contextual_action_reads_the_item_with_the_persons_permissions_and_cites_it`, `assistant.spec.ts` |
| FR-008 per user history, deletable, tenant retention | Met | `AssistantTests.History_and_feedback_belong_to_their_user_and_end_with_retention` |
| FR-009 feedback with the execution reference | Met | `AssistantTests.History_and_feedback_belong_to_their_user_and_end_with_retention` |
| FR-010 retrieved content as untrusted data | Met | `AssistantPromptTests.Passages_are_numbered_and_cannot_close_the_envelope`; the `assistant.safety` suite scores instructions hidden in passages (`EvaluationAssistantTests`) |
| FR-011 degraded states keyed by the gateway result | Met | `AssistantTests.Provider_failures_show_as_degraded_turns_and_are_kept_in_history` |
| FR-012 answer in the user's language | Met | `AssistantTests.An_arabic_question_gets_an_arabic_answer_and_streams`, `AssistantPromptTests.The_answer_language_follows_the_question`; `language_correctness` in `assistant.grounded_answers` |
| FR-013 routing and RAG changes pass the spec 034 gate | Met for model routes | `EvaluationRouteGateTests.A_change_to_the_assistant_route_runs_the_assistant_suites_on_the_proposed_model_and_applies_when_they_pass`, `AiGatewayTests.A_change_to_the_assistant_route_is_held_for_the_gate_...`; routing and retrieval settings ship in code in R1, so they change only with a release, see the notes |
| FR-014 no external images, outside links as text | Met | Answers render as plain text with citation links (`assistant.spec.ts`) |
| FR-019 search summaries only from 012's passages | Not here | Search summaries belong to spec 012 |
| FR-020 payslips and anonymous engagement never in context | Met by absence | Specs 019 and 023 are not built, so nothing of theirs is indexed for retrieval |
| FR-021 model, prompt version and retrieval settings recorded | Met | `AssistantTests.Answers_cite_only_passages_...` (prompt version `knowledge.v1`) |
| FR-015 to FR-018, FR-022 | R2 | |
