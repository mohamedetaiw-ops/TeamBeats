# Spec 013 evidence (R1)

The spec stays Approved, not Implemented. R1 covers User Stories 1 to 3, 4 (basic) and 8, FR-017 and FR-019. Staging still talks to the fake OpenRouter, because the OpenRouter key has not been provided; once it is stored as the Fly secret `Ai__OpenRouter__ApiKey` the real adapter is used. The R2 and R3 parts are not built: budgets and alerts (US5, FR-012), org unit policies (US7, FR-009), the private endpoint adapter (US6), the usage export (US4 scenario 4), cost analytics and the evaluator worker facade (FR-018). The spec 013 section of `docs/architecture/r1-implementation-notes.md` holds the defaults, deviations and gaps.

| Requirement | Status | Evidence |
|---|---|---|
| FR-001 every model call through the gateway, provider code only in the adapter | Met | `ArchitectureTests.Model_providers_are_reached_only_through_their_adapter` |
| FR-002 pipeline order | Met for R1 (budget step passes through until R2) | `AiGatewayTests.Defaults_per_use_case_serve_requests_and_a_model_outside_the_allowlist_sends_nothing`, `Classification_routing_keeps_Confidential_and_Restricted_away_from_shared_models_by_default`, `The_monthly_request_quota_refuses_further_calls_once_reached`, `Output_checks_streaming_embeddings_and_judge_calls` |
| FR-003 request context from the server | Met | Tenant and user from the session, acting unit, agent and version on the call record: `AiGatewayTests.Usage_is_recorded_per_call_and_shown_by_dimension_with_org_scope_and_the_five_person_minimum`; missing classification refused in `Classification_routing_keeps_Confidential_and_Restricted_away_from_shared_models_by_default` |
| FR-004 OpenRouter adapter behind a provider port | Met | `AiGateway.OpenRouter` is the only adapter; every gateway test goes through it against the fake OpenRouter. The private endpoint adapter is R2 |
| FR-005 model catalogue | Met | `AiGatewayTests.Policy_administrator_sees_the_catalogue_and_others_are_refused`, `Platform_operator_maintains_the_catalogue_and_tenants_cannot` |
| FR-006 allowlist, defaults, fallbacks, parameter limits | Met | `AiGatewayTests.Defaults_per_use_case_serve_requests_and_a_model_outside_the_allowlist_sends_nothing`, `Without_model_choice_the_platform_default_set_applies_and_cannot_be_changed`, `AiGatewayRulesTests.Policy_validation_keeps_routes_to_allowed_capable_models_and_Confidential_to_eligible_ones` |
| FR-007 Restricted never and Confidential not by default to shared providers (SC-001) | Met | `AiGatewayTests.Classification_routing_keeps_Confidential_and_Restricted_away_from_shared_models_by_default`, run on every build in the backend job |
| FR-008 fallback under the same policy | Met | `AiGatewayTests.A_failing_model_is_retried_once_then_the_next_eligible_model_answers` |
| FR-009 org unit policies only restrict | R2 | Not built |
| FR-010 logging off by default, time boxed windows, never Restricted | Met | `AiGatewayTests.Debug_windows_store_Public_and_Internal_text_encrypted_for_security_officers_only` |
| FR-011 metering by the six dimensions | Met | `AiGatewayTests.Usage_is_recorded_per_call_and_shown_by_dimension_with_org_scope_and_the_five_person_minimum`; per agent version costs for agent analytics in `AgentAnalyticsTests` |
| FR-012 budgets | R2 | Not built; the budget step passes through |
| FR-013 usage view in the viewer's scope | Met (basic) | `AiGatewayTests.Usage_is_recorded_per_call_and_shown_by_dimension_with_org_scope_and_the_five_person_minimum`, `ai-governance.spec.ts` |
| FR-014 provider credentials in the secret store | Met for R1 | The key is read from host configuration fed by the secret store (Fly secrets in staging) and never shown or logged. Which secret manager product is an open decision |
| FR-015 circuit breaker per provider | Met | `AiGatewayRulesTests.Circuit_opens_at_half_failures_over_twenty_calls_and_closes_after_a_good_trial`, `Old_calls_leave_the_window`. Opening and closing are logged for operator alerts; platform events are open |
| FR-016 typed results | Met | Denial reasons from `AiDenials` asserted throughout `AiGatewayTests` |
| FR-017 evaluation use case and judge rule | Met | `AiGatewayTests.Output_checks_streaming_embeddings_and_judge_calls` |
| FR-018 evaluator worker facade | R2 | Not built |
| FR-019 route changes held for the gate | Met | `AiGatewayTests.A_change_to_the_assistant_route_is_held_for_the_gate_and_applies_with_an_audited_override_once_the_gate_cannot_test_it`; gate decisions in the spec 034 evidence |
| Cross-tenant | Met | `AiGatewayTests.One_tenants_policy_never_applies_to_or_shows_in_another` |
| Overhead under 50 ms at p95 | Met | `AiGatewayOverheadTests.Gateway_overhead_stays_under_50_ms_at_p95` |
| Arabic token estimate | Met | `AiGatewayRulesTests.Token_estimate_does_not_undercount_Arabic` |
| SC-001 | Met | As FR-007 |
| SC-002 usage question in under 5 minutes | Met by design | The usage view groups by provider, unit and period with cost: `ai-governance.spec.ts` |
| SC-003 switch to a private endpoint without code change | R2 | Waits for the private endpoint adapter |
