# Spec 032 evidence (R1)

The spec stays Approved, not Implemented. R1 builds the page model, the resolver, the 10 layouts and 12 templates with variants, template preview, locks, distribution and lifecycle, the manifest registry, the builder with keyboard composition, autosave, undo, previews by device and direction, accessibility and translation checks, error isolation, publish and rollback, and the performance and isolation baseline. Still open in R1: persona previews in the builder (FR-041, waiting on spec 008 audiences in the builder), a draft revision history across sessions (FR-043), the retired template state with migration guidance (FR-021), page and navigation label translation checks (FR-047), and asset size budgets in CI (FR-060). The R2 and R3 parts are not built: approval routes (FR-051), impact reports (FR-044, FR-054), fragments (FR-045), import and export (FR-022), scheduled releases (FR-053), feature controls (FR-057), resizable regions (FR-007), independent component deployment (FR-033), sandboxed tenant components (FR-031) and template recommendations (FR-023). The spec 032 sections of `docs/architecture/r1-implementation-notes.md` hold the defaults, deviations and gaps.

| Requirement | Status | Evidence |
|---|---|---|
| FR-001 versioned declarative pages | Met | `PageCompositionTests.Page_is_created_from_configuration_with_provenance_and_audited`, `Draft_is_never_visible_and_publish_makes_an_immutable_release` |
| FR-002 effective configuration | Met | `PageCompositionTests.Page_is_created_from_configuration_with_provenance_and_audited`; themes in the spec 033 evidence |
| FR-003 no business data or secrets in definitions | Met | `ExperienceValidationTests.Properties_are_checked_against_their_types`, `Markup_and_event_handlers_are_executable` |
| FR-004, FR-005, FR-008, FR-011, FR-013 grid, container sizing, section modes, fallbacks | Met | `page-renderer.spec.ts` (all 12 templates at 320 to 1920 px in both directions, no horizontal scroll, fallbacks by container width) |
| FR-006 the 10 layouts | Met | `PageCompositionTests.Catalogue_has_ten_layouts_the_twelve_templates_and_the_home_template_and_every_template_makes_an_empty_page` |
| FR-007 resizable regions | R2 | Not built |
| FR-009 order and visibility per breakpoint | Partly met | DOM order is reading and keyboard order: `page-renderer.spec.ts`. Per-breakpoint order settings are not offered yet |
| FR-010 validator | Met | `PageCompositionTests.Validator_blocks_bad_placements_and_names_each_one` |
| FR-012 region rules | Met | `PageCompositionTests.Validator_blocks_bad_placements_and_names_each_one`, `ExperienceValidationTests.Catalogue_templates_match_their_layouts_and_use_only_known_categories` |
| FR-014, FR-015 templates without required components, the 12 templates | Met | `PageCompositionTests.Catalogue_has_ten_layouts_the_twelve_templates_and_the_home_template_and_every_template_makes_an_empty_page` |
| FR-016 visual variants and modes | Met | `TemplateTests.Template_input_is_validated_and_variants_and_locks_stay_within_the_base_template`; modes in the spec 033 evidence |
| FR-017 template chooser previews | Met | `page-builder.spec.ts`, `templates.spec.ts` |
| FR-018 instantiation records template and version | Met | `TemplateTests.Version_two_leaves_existing_pages_alone_until_applied_and_a_deprecated_version_keeps_pages_working_but_blocks_new_ones` |
| FR-019 region protection enforced | Met | `PageCompositionTests.Publish_needs_required_placements`; personal regions in the spec 008 evidence |
| FR-020 distribution | Met | `TemplateTests.Template_distributed_to_the_gulf_is_offered_there_and_can_be_neither_seen_nor_used_elsewhere` |
| FR-021 template lifecycle | Partly met | Draft, released and deprecated: `TemplateTests.Version_two_leaves_existing_pages_alone_until_applied_and_a_deprecated_version_keeps_pages_working_but_blocks_new_ones`. Retired and migration guidance are open |
| FR-022 export and import | R2 | Not built |
| FR-023 recommendations | R3 | Not built |
| FR-024, FR-025 manifests and typed properties | Met | `ExperienceValidationTests.Manifests_outside_the_contract_are_refused`, `Properties_are_checked_against_their_types`, `Defaults_fill_properties_a_new_manifest_version_added` |
| FR-026 components style only through tokens | Met | The `check:tokens` and Stylelint gates in `npm run verify` |
| FR-027 data and authorization stay with the owning module | Met | `PageCompositionTests.Picker_refuses_components_whose_permission_the_editor_lacks`; each component calls its module's API as the viewer |
| FR-028 only declared context | Met | `PageCompositionTests.Render_passes_only_declared_context_applies_defaults_and_shows_unavailable_components` |
| FR-029 error boundary per placement (SC-003) | Met | `page-renderer.spec.ts` (a throwing, slow and missing component beside working ones) |
| FR-030 compatibility ranges and upgrades | Met | `ExperienceValidationTests.Upgrades_replace_the_same_major_and_never_downgrade`, `Migration_hooks_register_only_to_the_next_major_of_registered_components`, `PageCompositionTests.Component_upgrade_runs_the_migration_hook_on_drafts_that_use_it_and_leaves_other_pages_alone` |
| FR-031 trust tiers | Met for platform components | Only platform components exist in R1; sandboxed tenant components are R3 |
| FR-032 lifecycle events | Partly met | Render failures and web vitals: `PageTelemetryTests`. Interaction events through spec 021 are open |
| FR-033 independent component deployment | R2 | Not built |
| FR-034 configuration, runtime and preference state apart | Met | Spec 008 `PersonalizationTests.Reset_one_and_reset_all_keep_the_preference_profile` |
| FR-035 no executable content | Met | `ExperienceValidationTests.Only_https_and_site_relative_urls_are_safe`, `Markup_and_event_handlers_are_executable` |
| FR-036 to FR-039 visual builder, picker, operations, keyboard alternatives | Met | `page-builder.spec.ts` in English and Arabic, `PageCompositionTests.Picker_lists_registered_components_and_explains_ineligible_ones` |
| FR-040 previews by device and direction | Met | `PageCompositionTests.Preview_draws_the_saved_draft_for_its_editors_only_in_the_chosen_language`, `page-builder.spec.ts` |
| FR-041 persona preview | Open | Waits for spec 008 audiences in the builder; the targeting preview exists in spec 008 |
| FR-042 autosave and concurrency | Met | `PageCompositionTests.Stale_draft_save_is_a_conflict_that_keeps_both_versions`, `page-builder.spec.ts` |
| FR-043 undo, redo and history | Partly met | Undo and redo in a session: `page-builder.spec.ts`. Releases and rollback exist; a draft revision history is open |
| FR-044, FR-054 affected pages before publishing | R2 | Not built |
| FR-045 fragments | R2 | Not built |
| FR-046 accessibility findings | Met | `PageCompositionTests.Validator_blocks_bad_placements_and_names_each_one`, `page-builder.spec.ts` |
| FR-047 translation status | Partly met | `PageCompositionTests.Text_missing_in_one_enabled_language_is_a_translation_warning_that_does_not_block`; page and navigation labels are open |
| FR-048 built-in roles | Met | Page editor and brand manager roles; `PermissionBackfillTests` |
| FR-049 owner and scope | Met | `PageCompositionTests.Page_list_shows_only_pages_the_editor_may_edit_in_their_own_tenant`; an inventory report is open |
| FR-050 no live change before publish, immutable releases | Met | `PageCompositionTests.Draft_is_never_visible_and_publish_makes_an_immutable_release` |
| FR-051 approval | R2 | Not built |
| FR-052 rollback as a new release | Met | `PageCompositionTests.Rollback_restores_an_earlier_release_as_a_new_release` |
| FR-053 scheduling | R2 | Not built |
| FR-055 audit | Met | `PageCompositionTests.Page_is_created_from_configuration_with_provenance_and_audited`, `Delegated_editor_works_only_inside_their_scope_and_outside_is_not_found_and_audited` |
| FR-056 delegated scope enforced by the API | Met | `PageCompositionTests.Delegated_editor_works_only_inside_their_scope_and_outside_is_not_found_and_audited` |
| FR-057 feature controls | R2 | Not built |
| FR-058 LCP, INP, CLS (SC-005) | Met on the two default profiles | `performance.spec.ts` (all 12 templates, mobile and desktop, without throttling; agreed profiles are open) |
| FR-059, FR-061 deferred resources and reserved space | Met | Suspense placeholders keep their space: `page-renderer.spec.ts`, CLS in `performance.spec.ts` |
| FR-060 asset budgets in CI | Open | Needs a production build in CI |
| FR-062 offline builder edits | Met | Unsaved edits kept in local storage and saved on reconnect: `page-builder.spec.ts` |
| FR-063 browsers and input | Partly met | Playwright runs Chromium; other browsers are not in CI |
| FR-064 telemetry correlated without user identity | Met | `PageTelemetryTests.Render_failures_and_web_vitals_are_recorded_for_the_session_tenant_and_free_text_is_dropped` |
| FR-065 cache keys by tenant and scope | Met | Theme CSS and render answers keyed by tenant and scope; `PageCompositionTests.Pages_of_another_tenant_are_not_found` |
| FR-066 safe fallbacks | Met | `PageCompositionTests.Render_passes_only_declared_context_applies_defaults_and_shows_unavailable_components`, `page-renderer.spec.ts` |
| FR-067, FR-068 page-scoped events, no component imports | Met | `page-renderer.spec.ts` (events between unrelated components), `check:placements` in `npm run verify` |
| FR-069 connection features in manifests | Met | `ExperienceValidationTests.My_mail_and_My_calendar_register_as_personal_components_needing_Microsoft_365_and_their_feature`, `A_manifest_naming_a_connection_feature_in_another_form_is_rejected` |
| Cross-tenant | Met | `PageCompositionTests.Pages_of_another_tenant_are_not_found`, `Layout_change_refuses_unknown_templates_and_is_not_found_outside_scope_and_tenant`, `TemplateTests.Custom_templates_stop_at_the_plan_limit_and_another_tenant_never_finds_them` |
| SC-001 a page in under 30 minutes without help | Pilot | Measured in pilot usability tests |
| SC-002 swap test with two packs | Met | `page-renderer.spec.ts` (both reference packs on all 12 templates) |
| SC-003 fault injection | Met | As FR-029 |
| SC-004 keyboard, single pointer and screen reader | Met | `page-builder.spec.ts` |
| SC-005 performance targets | Met on the default profiles | As FR-058 |
