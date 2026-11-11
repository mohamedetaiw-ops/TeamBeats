# Spec 019 evidence (R2)

The spec stays Approved, not Implemented. Every R2 user story is built: connect and match, joiners, movers and leavers, form prefill, My payslips and HR leave balances. The R3 part (named HR packs, `hr.named_packs`) is not built. The spec 019 sections of `docs/architecture/r2-implementation-notes.md` hold the defaults, deviations and gaps. Test classes are in `tests/Integration` unless marked unit or e2e. The e2e suite runs in English and Arabic with axe.

| Requirement | Status | Evidence |
|---|---|---|
| FR-001 connector runtime of spec 017 and the canonical HR contract only | Met | `HrSyncTests` (REST and SFTP packs through the runtime), `HrMappingTests` (unit), `ArchitectureTests.Modules_reference_only_building_blocks_and_contracts` (other modules see only `Integrations.Contracts`) |
| FR-002 ordered matching rule, conflicts never matched | Met | `HrSyncTests.The_first_sync_matches_by_email_then_number_lists_conflicts_and_unmatched_and_keeps_hand_made_links` |
| FR-003 HR fields read-only and labelled with their source | Met | `HrSyncTests.The_first_sync_matches_...` (profile fields from HR, edit refused) |
| FR-004 change threshold holds the run for an HR administrator | Met | `HrSyncTests.A_run_over_the_change_threshold_is_held_until_an_hr_administrator_confirms_or_discards_it` |
| FR-005 effective dates, future changes held and applied on the date in the tenant time zone | Met | `HrLifecycleTests.Movers_wait_for_their_org_code_leavers_lose_access_on_time_and_rehires_reopen_only_when_allowed`, `HrLifecyclePlanTests` (unit) |
| FR-006 lifecycle events consumed by 002, 003, 010 and 022 | Met | `HrLifecycleTests.Movers_wait_...` (membership, manager, account), spec 022 `JourneyTests.A_hire_starts_one_onboarding_journey_...` |
| FR-007 org codes map to units, unmapped codes hold only their change | Partly met | `HrLifecycleTests.Movers_wait_...`. Creating units from HR (off by default) is not offered |
| FR-008 leavers disabled at the end of the last working day, immediate terminations within 15 minutes, sessions revoked | Met | `HrLifecycleTests.Movers_wait_...`, `HrLifecyclePlanTests` (unit) |
| FR-009 prefill uses server values for read-only fields and respects classification | Met | `HrPrefillTests.Forms_fill_the_requesters_hr_fields_keep_read_only_values_and_confirm_stale_ones_with_hr`, `FormValidatorTests` (unit) |
| FR-010 payslips only for their owner, after step-up, no override | Met | `HrSyncTests.People_read_only_their_own_payslips_live_with_step_up_and_a_watermarked_audited_pdf` |
| FR-011 downloads audited and watermarked; never indexed, cached, sent to models or put in notifications | Met | `HrSyncTests.People_read_only_their_own_payslips_...`, `HrSyncTests.The_watermark_is_plain_text_in_a_standard_font_and_a_broken_pdf_is_not_handed_out`; payslips are no search source, tool, action or event |
| FR-012 failed reads show the failure, not stale data | Met | `HrSyncTests.People_read_only_their_own_payslips_...` (payroll down), `HrPrefillTests.Forms_fill_...` (HR down) |
| FR-013 leave balance source as a versioned tenant setting | Met | `HrLeaveSourceTests` (4 tests) |
| FR-014 manager loops and invalid structures refused per record | Met | `HrSyncTests.The_first_sync_matches_...` (loops, bad status), `HrMappingTests` (unit) |
| FR-015 unmatched records retried for 30 days, then listed as stale | Met | `HrSyncTests.Unmatched_records_are_retried_then_listed_as_stale_and_hr_fields_show_only_where_profile_visibility_allows` |
| FR-016 REST pack configured from a mapping, SFTP pack on the same contract | Met | `HrSyncTests.The_sftp_pack_reads_the_newest_export_with_mapped_columns`, `HrMappingTests` (unit), `hr-integration.spec.ts` |
| FR-017 HR fields on profiles follow spec 010 visibility | Met | `HrSyncTests.Unmatched_records_are_retried_...` (grade, cost centre and employee number for HR in scope only; a colleague and a sibling unit's HR officer see the job title only) |
| FR-018 every applied change traceable to its run and source record | Met | `HrSyncTests.The_first_sync_matches_...` (last run per record), `HrLifecycleTests.Movers_wait_...` (`hr.change_applied` with run and record) |
| US7 HR leave balances, last known balance when HR fails | Met | `HrLeaveSourceTests.With_HR_as_the_source_...`, `When_HR_is_down_the_last_known_balance_shows_...` |
| Full sync of 20,000 employees under 30 minutes | Met | `HrSyncTests.A_full_sync_of_twenty_thousand_employees_finishes_well_within_thirty_minutes` (first run and a run with 1,600 changes, each under 10 minutes; the whole test, seeding included, takes about 30 seconds locally) |
| Payslip list under 2 seconds p95 when payroll answers within 1 second | Met | `HrSyncTests.The_payslip_list_loads_p95_under_two_seconds_when_payroll_takes_close_to_a_second` (payroll held at 900 ms on every read) |
| SC-001 99% of joiners ready on their first day | Not proven | Needs a pilot tenant; joiners are prepared the lead time ahead (`HrLifecycleTests`) |
| SC-002 leavers disabled by the end of their last working day in 100% of test cases | Met | `HrLifecycleTests.Movers_wait_...`, `HrLifecyclePlanTests` (unit) |
| Cross-tenant, org boundary and package denial | Met | `HrSyncTests.Settings_permissions_the_package_and_the_tenant_boundary_hold`, the other tenant cases in `HrSyncTests.The_first_sync_matches_...`, and the plan cases in every class above |
