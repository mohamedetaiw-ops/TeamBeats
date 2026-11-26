# Spec 014 evidence (R1 and R2)

The spec stays Approved, not Implemented, until the assistant panel in the shell (T007) is done and R3 proactive suggestions follow. R2 covers US5 to US8, FR-015 to FR-018, FR-020 and FR-022. Defaults, deviations and gaps are in the spec 014 sections of `docs/architecture/r1-implementation-notes.md` and `docs/architecture/r2-implementation-notes.md`.

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
| FR-020 payslips and anonymous engagement never in context | Met | `AssistantGuestTests.Payslips_and_anonymous_engagement_are_never_a_search_source`; the `AiExcludedSources` deny list is enforced by the indexer and by retrieval for every AI use case |
| FR-021 model, prompt version and retrieval settings recorded | Met | `AssistantTests.Answers_cite_only_passages_...` (prompt version `knowledge.v1`) |
| FR-015 files scanned, classified, kept to their conversation, refused above the ceiling, deleted with it | Met | `AssistantFileTests.A_file_is_scanned_classified_and_cited_only_in_its_conversation`, `Infected_unreadable_unknown_and_above_policy_files_are_refused_with_the_reason`, `A_file_no_turn_used_goes_after_a_day_and_delete_all_takes_every_file`; `assistant.spec.ts` (en and ar, axe) |
| FR-016 voice through the gateway, confirmed before sending, audio not stored | Met | `AssistantVoiceTests.A_spoken_question_comes_back_as_text_to_edit_and_nothing_is_kept`, `An_answer_is_read_aloud_for_its_owner_only_without_citation_markers`, `Workplace_has_no_voice`; `assistant.spec.ts` (en and ar, axe) |
| FR-017 personal answers only from tools over the person's records, with source and time | Met | `AssistantPersonalToolsTests.My_HR_helper_reads_only_the_persons_own_leave_and_cites_it`; template suite `template.my_hr_helper` |
| FR-018 Teams assistant on the same contracts | Met; answers draw on Internal content at most | `TeamsAssistantTests.The_answer_and_its_sources_come_back_to_the_chat_and_a_follow_up_continues_the_conversation`, `Teams_answers_draw_on_internal_content_at_most`, `Unknown_ambiguous_and_unentitled_people_get_a_short_answer_and_nothing_else`, `Forged_chat_messages_are_refused` |
| FR-022 guests refused unless allowed, then only their spaces | Met | `AssistantGuestTests.Guests_are_refused_unless_allowed_and_then_cite_only_their_spaces` |

## R3

| Requirement | Status | Evidence |
|---|---|---|
| FR-023 suggestions without a model call, only for readable items allowed by the AI policy, switches for the person and the tenant | Met | `AssistantSuggestionTests` (3: readable only, withheld, picked turn kept to the item, other tenant; person and tenant switches, audit, package; page rules) |
| US9 panel, preferences and admin setting | Met | `assistant-suggestions.spec.ts` (en and ar, axe) |

