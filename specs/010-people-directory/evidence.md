# Spec 010 evidence (R1)

The spec stays Approved, not Implemented, until photo upload and directory photos (part of T007) are done. The reporting line is wired into line manager approval and Team requests (016), manager lists (009, 025) and free and busy visibility (018).

| Requirement | Status | Evidence |
|---|---|---|
| PPL-001 profile per membership | Met | `PeopleDirectoryTests.Directory_sync_sets_profiles_managers_and_history_and_ignores_a_manager_who_is_not_a_member` |
| PPL-002 one source per field | Met | `PeopleDirectoryTests.Employees_edit_their_own_editable_fields_and_directory_fields_stay_read_only`, `HR_officers_in_scope_correct_administrator_managed_fields_and_localized_names` |
| PPL-003 visibility in the API and search | Met for API and search | `PeopleDirectoryTests.Visibility_rules_apply_in_profiles_and_search`; exports and agent tools come with 012 and 013 |
| PPL-004 reporting line from the directory, manual only without sync | Met | `Directory_sync_sets_profiles_...`, `Without_directory_sync_an_administrator_sets_managers_by_hand_with_history_and_audit` |
| PPL-005 reporting line contract | Met | `IReportingLine`; `Org_chart_and_my_team_follow_the_reporting_line` |
| PPL-006 search filters and Arabic and English matching | Met | `Search_matches_name_title_department_extension_and_email_with_Arabic_normalization_and_transliteration`, `People_of_another_tenant_are_never_found`, `PeopleSearchTextTests` |
| PPL-007 org chart, keyboard, list view, RTL | Met | `people.spec.ts` (chart and list, en and ar, axe) |
| PPL-008 My team paged with indirect reports | Met | `Org_chart_and_my_team_follow_the_reporting_line`, `people.spec.ts` |
| PPL-009 newcomers with opt-out | Met | `Newcomers_list_recent_hires_newest_first_and_respect_the_opt_out` |
| PPL-010 people component | Met | `PageCompositionTests.Picker_lists_registered_components_and_explains_ineligible_ones`, `The_people_component_reads_a_named_set_and_bad_ids_are_refused`, `people.spec.ts` |
| PPL-011 location dimension | Met | `Renaming_a_location_keeps_its_key`, `PeopleSearchTextTests.Location_keys_ignore_case_and_spacing` |
| PPL-012 events | Met | `Directory_sync_sets_profiles_...` (`ReportingLineChanged`, `LocationAdded` in the outbox), `Employees_edit_their_own_editable_fields_...` (`ProfileUpdated`) |
| PPL-013 photos | Open | Needs an image library decision (r1-implementation-notes) |
| PPL-014 free and busy visibility | Met | `MailCalendarTests.Free_and_busy_shows_status_blocks_only_for_a_visible_colleague` (no `people.view`, no free and busy) |
| PPL-015 contact actions from visible fields | Met | Profile page builds actions from returned fields only; `Visibility_rules_apply_in_profiles_and_search` |
| PPL-016 tenant fields | Met | `The_policy_is_for_administrators_custom_fields_need_the_capability_and_stale_versions_conflict` |
| PPL-017 search rate limit | Met | `people_search` limiter, 120 per minute per user |
| PPL-022 last synced values kept | Met | Profiles keep stored values between syncs; the sync time shows on `/admin/people` |
| Search p95 under 300 ms at 50,000 members | Met | `PeopleSearchPerformanceTests` (local run: p95 98 ms) |
