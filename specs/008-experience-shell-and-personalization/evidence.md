# Spec 008 evidence (R1)

The spec stays Approved, not Implemented. R1 builds the shell, the shared states and message rules, menus and information architecture, quick navigation, the employee home with add, hide, reorder and reset in delegated regions, the preference profile, audience targeting with a default, and the interaction standards. Still open in R1: the compact density and personal mode choices in the web app read the comfortable default, and visibility rules on placements are refused at save, so the renderer has none to evaluate (see the spec 008 and 032 notes). The R2 and R3 parts are not built: the command entry point (FR-008), focus mode (FR-009), favorites and recents (FR-017), help and tours (FR-020), resize and layout presets (FR-025 resize, FR-026), device-specific choices (FR-032), preference export (FR-034), suggestions (FR-035) and behavioral personalization (FR-033, which stays off). The spec 008 sections of `docs/architecture/r1-implementation-notes.md` hold the defaults, deviations and gaps.

| Requirement | Status | Evidence |
|---|---|---|
| FR-001 configurable shell independent of pages | Met | `ShellNavigationTests.Shell_has_a_default_layout_and_publishing_it_changes_no_page`, `Shell_definition_is_validated_and_needs_shell_edit`, `shell.spec.ts`, `admin-shell.spec.ts` |
| FR-002 shared labels, controls and states | Met | `components/ui` primitives used by every page; `interaction.spec.ts` |
| FR-003, FR-004 clear primary task, progressive disclosure | Met by design | Page layouts in `components/ui`; checked in `interaction.spec.ts` |
| FR-005 position, filters and input kept | Met for R1 | Forms keep entered values on error: `interaction.spec.ts` |
| FR-006 central states | Met | State views in `components/ui`: `interaction.spec.ts` |
| FR-007 message priority and dismissal | Met | `interaction.spec.ts`; critical announcements in the spec 009 evidence |
| FR-008 command entry point | R2 | Not built |
| FR-009 focus mode | R2 | Not built |
| FR-010 settings in one place on every page | Met | Settings panel in the shell: `shell.spec.ts` |
| FR-011 navigation configured apart from pages | Met | `ShellNavigationTests.Each_menu_publishes_on_its_own_and_lists_only_pages_the_caller_may_open` |
| FR-012 accessible menus | Met | `shell.spec.ts` (keyboard, closing on Escape and focus loss), `shell-reachability.spec.ts` (landmarks and menu states for screen readers), axe in English and Arabic |
| FR-013 labels and slugs apart from templates, redirects | Met | `ShellNavigationTests.Old_slugs_redirect_and_breadcrumbs_and_section_menus_follow_the_ia`, `Ia_entries_are_validated` |
| FR-014 breadcrumbs follow the IA | Met | `ShellNavigationTests.Old_slugs_redirect_and_breadcrumbs_and_section_menus_follow_the_ia` |
| FR-015 active organization shown | Met for R1 | The shell shows the organization indicator; organization menus stay in their scope: `ShellNavigationTests.Organization_admin_edits_only_their_own_organization_menus_and_entries`. An end to end check of the indicator is open |
| FR-016 search and quick navigation to permitted destinations | Met | `ShellNavigationTests.Each_menu_publishes_on_its_own_and_lists_only_pages_the_caller_may_open`; search results in the spec 012 evidence |
| FR-017 favorites and recents | R2 | Not built |
| FR-018 menus at mobile widths | Met | `shell-reachability.spec.ts` |
| FR-019 safe recovery for missing or forbidden destinations | Met | `ShellNavigationTests.Missing_unpublished_and_not_permitted_destinations_give_the_same_recovery_answer`, `Home_page_is_set_in_the_shell_and_shown_only_when_published_and_of_this_tenant` |
| FR-020 help and tours | R2 | Not built |
| FR-021 persisted preference profile | Met | `PersonalizationTests.Preference_profile_accepts_only_schema_keys_and_values`, `Reset_one_and_reset_all_keep_the_preference_profile` |
| FR-022 admins define what may be personalized | Met | `PersonalizationTests.Policy_needs_the_package_for_pages_beyond_home_and_stays_in_the_admins_scope`, `Hide_move_and_region_maximum_follow_the_policy_and_survive_reload` |
| FR-023 add components to delegated regions only | Met | `PersonalizationTests.Personal_placements_go_only_into_delegated_regions_with_eligible_components_and_coworkers_are_unaffected` |
| FR-024 protected placements enforced by the API | Met | `PersonalizationTests.Protected_placements_cannot_be_hidden_removed_or_moved_through_the_api` |
| FR-025 reorder by pointer and keyboard, survives reload | Met; resize is R2 | `PersonalizationTests.Hide_move_and_region_maximum_follow_the_policy_and_survive_reload`, `home-personalization.spec.ts` (keyboard only) |
| FR-026 layout presets | R2 | Not built |
| FR-027 undo and reset | Met | `PersonalizationTests.Reset_one_and_reset_all_keep_the_preference_profile`, `home-personalization.spec.ts` |
| FR-028 audience targeting with persona preview | Met | `PersonalizationTests.Targeting_preview_shows_who_matches_and_what_each_persona_sees` |
| FR-029 visibility apart from data authorization | Met | `PersonalizationTests.People_who_match_no_audience_get_the_default_and_hiding_neither_grants_nor_removes_access` |
| FR-030 default variant | Met | As FR-029 |
| FR-031 precedence | Met | `PersonalizationTests.Precedence_locks_and_package_then_audience_then_admin_default_then_user_choice` |
| FR-032 device-specific choices | R2 | All R1 preferences are cross-device |
| FR-033 behavioral personalization off | Met (R3 feature not built) | Nothing in R1 personalizes from behavior |
| FR-034 preference export | R2 | Not built |
| FR-035 suggestions | R2 | Not built |
| FR-036 preference record fields, no other person's data | Met | `PersonalizationTests.Layouts_are_per_user_and_per_tenant_and_need_a_viewable_published_page`, `Operations_that_no_longer_apply_are_skipped_and_reported_and_return_with_the_policy` |
| FR-037 to FR-048 interaction standards | Met for R1 | `interaction.spec.ts` (states, action hierarchy, form errors and selection; confirmation and undo for destructive actions; message priority; both densities at 200% zoom; reduced motion). Target sizes (FR-047) and inline hints (FR-046) have no automated check yet |
| FR-049 employee home with protected and personal regions | Met | `PersonalizationTests.Home_template_delegates_its_personal_region_in_every_package_and_keeps_the_top_region_protected`, `home-v2.spec.ts` |
| FR-050 tool box utilities as platform components | Met | `ExperienceValidationTests.Tool_box_utilities_register_as_platform_components_needing_no_capability_and_addable_with_defaults`, `WeatherTests`, `toolbox.spec.ts` |
| FR-051 My calendar and My mail on the default home | Met | `MailCalendarTests.The_default_home_shows_My_calendar_and_My_mail_in_the_personal_region_while_their_features_are_on`, `The_default_home_suggests_nothing_without_the_entitlement_or_the_feature` |
| Cross-tenant | Met | `ShellNavigationTests.Another_tenants_menus_entries_and_scopes_are_not_found`, `PersonalizationTests.Layouts_are_per_user_and_per_tenant_and_need_a_viewable_published_page` |
| SC-001 consistent action conventions | Met for R1 | `interaction.spec.ts` across the platform, content and reference packs |
| SC-002 personal reorder leaves coworkers unchanged | Met | `PersonalizationTests.Personal_placements_go_only_into_delegated_regions_with_eligible_components_and_coworkers_are_unaffected` |
| SC-003 locked regions cannot be removed | Met | `PersonalizationTests.Protected_placements_cannot_be_hidden_removed_or_moved_through_the_api` |
| SC-004 find a service page within 60 seconds | Pilot | Measured in pilot usability tests |
