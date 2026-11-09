# Spec 025 evidence (R1)

The spec stays Approved, not Implemented, until its R2 parts are built: scheduled reviews (US5, FR-012), comprehension checks (US6, FR-013), exemptions (US7, FR-014), control mapping and evidence packs (US8, FR-015, FR-016), onboarding attestation (FR-017), fixed attestation cycles (the second half of FR-008) and controlled document policies (the second half of FR-003). Defaults, deviations and gaps of each R1 slice are in the spec 025 sections of `docs/architecture/r1-implementation-notes.md`.

| Requirement | Status | Evidence |
|---|---|---|
| FR-001 register per tenant, filterable, managed with `policy.manage` | Met | `PolicyApprovalTests.The_register_checks_its_entries_and_only_compliance_officers_change_them`, `policy-authoring.spec.ts` |
| FR-002 lifecycle, audited transitions, published versions read only | Met | `PolicyApprovalTests.A_version_runs_its_route_with_compare_and_comments_then_the_next_takes_effect_on_its_date_and_supersedes_the_last` |
| FR-003 structured pages with numbered, linkable clauses | Met for structured pages | `PolicyRulesTests.Clauses_are_numbered_from_their_order_and_levels`, `New_clauses_get_keys_and_anchors_that_follow_the_key`, `policies.spec.ts`; controlled document policies are a later slice |
| FR-004 routes on the engine, inbox tasks, clause comments, no self approval | Met | `PolicyApprovalTests.A_version_runs_its_route_...`, `Owners_editors_and_submitters_never_approve_and_every_language_is_approved_together` |
| FR-005 binding language, variants approved together | Met | `PolicyApprovalTests.Owners_editors_and_submitters_never_approve_and_every_language_is_approved_together`, `PolicyRulesTests.A_version_is_complete_only_with_every_clause_in_every_locale_and_the_binding_one` |
| FR-006 campaigns with audience, deadline, text, reminders, escalation, late joiners, leavers, exclusions | Met | `PolicyCampaignTests` (all) |
| FR-007 immutable attestation with hash, no delegation | Met | `PolicyCampaignTests.A_campaign_asks_its_audience_and_each_attestation_is_a_fixed_record_...`, `PolicyRulesTests.The_attestation_hash_is_sha256_hex`; the comprehension result comes with US6 (R2) |
| FR-008 re-attestation on material change | Met for material change | `PolicyCampaignTests.Late_joiners_get_their_own_due_date_leavers_are_marked_and_a_material_revision_leaves_the_choice_to_compliance`; fixed cycles are R2 |
| FR-009 completion by unit, location, manager and person within scope | Met | `PolicyComplianceTests` (both) |
| FR-010 assistant answers only from readable versions in force, cites clauses, says when nothing covers it | Met | `PolicyQuestionTests.The_assistant_cites_the_policy_clause_in_force_names_each_policy_that_applies_and_answers_nothing_else`, `AssistantPromptTests.Policy_passages_are_cited_by_title_version_and_clause_and_conflicts_are_left_to_the_owner` |
| FR-011 policy-scoped Q&A on each policy page | Met for the policy itself | `PolicyQuestionTests.Ask_about_this_policy_keeps_the_conversation_to_that_policy_for_readers_only`, `policies.spec.ts` (en and ar, axe); linked documents come with controlled document policies |
| FR-018 clause anchors in search, superseded versions excluded | Met | `PolicyQuestionTests.The_assistant_cites_the_policy_clause_in_force_...` (units per clause with anchors and version; no superseded unit after a revision) |
| Section 7 cross-tenant and boundary cases | Met | `PolicyApprovalTests.A_unit_policy_stays_inside_its_unit_...`, `PolicyCampaignTests.Without_unlimited_campaigns_...`, `PolicyComplianceTests.Org_scoped_officers_...`, `PolicyQuestionTests` (another tenant cannot scope to a policy or read the conversation; an Egypt employee never sees the Gulf policy) |
| Section 9 `policy.core`, `policy.campaigns`, `ai.policy_qa` | Met | `PolicyCampaignTests.Without_unlimited_campaigns_one_is_open_at_a_time_...`; `ai.policy_qa` is demanded for a policy-scoped conversation and decides the "Ask about this policy" entry (`rights.ask`), and is on in every package, so its off path is not testable today |
| Pages (T011) | Met | `policies.spec.ts`, `policy-authoring.spec.ts` (en and ar, axe) |
