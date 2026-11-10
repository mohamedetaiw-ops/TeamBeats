# Spec 033 evidence (R1)

The spec stays Approved, not Implemented. R1 builds the token engine and resolver, tenant brands, org overrides, brand assets, light and dark modes, typography by script, contrast validation, scopes, locks, the effective configuration trace and stable IDs. Still open in R1: screens for org overrides and locks (the API holds them), the experience impact preview before an org unit move (US5), and the compact density choice in the web app, which waits for the spec 008 personal mode setting. The R2 parts are not built: the theme draft, approval, schedule and rollback (FR-012), high contrast mode, impact analysis (FR-024), preview as organization (FR-023), the starter wizard (FR-027), custom domains (FR-010), token export (FR-014), the drift report (FR-015 in full), localized branding (FR-028) and reviewed token extensions (FR-013 in full). The spec 033 sections of `docs/architecture/r1-implementation-notes.md` hold the defaults, deviations and gaps.

| Requirement | Status | Evidence |
|---|---|---|
| FR-001 named brands assigned to scopes | Met | `ThemeTests.Two_sibling_units_render_their_own_brand_at_the_same_time`, `First_use_seeds_the_default_brand_from_tenant_settings_and_package_limits_apply` |
| FR-002 org overrides within parent policy | Met (API only) | `ThemeTests.Parent_locks_refuse_child_overrides_and_permanent_locks_cannot_be_removed` |
| FR-003 semantic tokens consumed only through the contract | Met | `ThemeEngineTests.Platform_defaults_are_complete_and_pass_every_check`, `WebTokensFileTests.Web_tokens_file_matches_the_token_schema`, `Every_token_has_a_custom_property_in_both_modes`, the `check:tokens` and Stylelint gates in `npm run verify` |
| FR-004 deterministic resolution with locks winning | Met | `ThemeEngineTests.Resolution_follows_platform_tenant_parent_child_and_locks_win`, `ThemeTests.Two_sibling_units_render_their_own_brand_at_the_same_time` |
| FR-005 brand assets with validation | Met | `ThemeTests.Brand_assets_are_validated_stored_per_tenant_and_readable_by_members_only`, `Asset_upload_needs_theme_edit_and_refuses_unsafe_or_large_files`, `brand-manager.spec.ts` |
| FR-006 light, dark, comfortable and compact | Met for R1; high contrast is R2 | `ThemeTests.Theme_css_is_compiled_per_scope_mode_and_density_and_cached_by_hash`, `theme.spec.ts` |
| FR-007 typography per writing system | Met | Approved font lists per script in the token schema: `ThemeEngineTests.Untyped_or_out_of_range_values_are_refused_with_the_token_name`, `Typed_values_are_accepted` |
| FR-008 preview before release | Met for R1 | `ThemeTests.Check_returns_the_preview_properties_of_both_modes`, `brand-manager.spec.ts` (both modes and directions). Shell previews come with the spec 008 shell |
| FR-009 contrast, focus and size checks | Met | `ThemeEngineTests.Contrast_ratio_matches_known_pairs`, `Shell_pairs_block_and_accent_pairs_warn_unless_strict`, `Body_text_below_fourteen_px_blocks`, `ThemeTests.Token_values_are_typed_and_contrast_failures_block_on_the_shell_and_warn_on_accents` |
| FR-010 custom domains and sign-in presentation | R2 | Not built |
| FR-011 shell states use the effective theme | Met for R1 | Every web style reads `--tb-*` tokens (`check:tokens`), and the tenant theme is linked before first paint: `theme.spec.ts` |
| FR-012 theme lifecycle | R2 | R1 publishes on save as a new version |
| FR-013 no unrestricted CSS | Met; reviewed extensions are R2 | `ThemeEngineTests.Untyped_or_out_of_range_values_are_refused_with_the_token_name`, `ThemeEngineTests.Css_holds_only_typed_custom_properties_and_stays_under_thirty_kilobytes_compressed` |
| FR-014 token export and import | R2 | Not built |
| FR-015 reports on inaccessible theme elements | Partly met | Contrast findings name the token pair, mode, ratio and required ratio on every save; the drift report is R2 |
| FR-016 scopes | Met for tenant and org units | `ThemeTests.Two_sibling_units_render_their_own_brand_at_the_same_time`; site and audience scopes come with spec 032 sites |
| FR-017 visual and permission inheritance kept apart | Met by design | Theme code never calls authorization to grant; permissions to edit themes are checked like any other: `ThemeTests.Delegated_admins_cannot_reach_neighbouring_or_child_scopes_they_hold_no_grant_for` |
| FR-018 effective configuration and sources | Met | Provenance only for `config_history.view`: `ThemeTests.Parent_locks_refuse_child_overrides_and_permanent_locks_cannot_be_removed` |
| FR-019 parent locks | Met | `ThemeTests.Parent_locks_refuse_child_overrides_and_permanent_locks_cannot_be_removed` |
| FR-020 delegated administration per scope | Met for brands | `ThemeTests.Delegated_admins_cannot_reach_neighbouring_or_child_scopes_they_hold_no_grant_for`, `Brand_manager_edits_brands_but_publishing_a_brand_in_use_needs_theme_publish` |
| FR-021, FR-022 page ownership and shared journeys | Spec 032 and R2 | See the spec 032 notes |
| FR-023 preview as organization | R2 | Not built |
| FR-024 descendants listed before a parent change | R2 | Not built |
| FR-025 org unit move impact | Partly met | The resolver follows the new parent once the move is saved; the impact preview is open |
| FR-026 immutable IDs | Met | Assignments, overrides and locks key on tenant and unit IDs; `ThemeTests.Two_sibling_units_render_their_own_brand_at_the_same_time` |
| FR-027 starter wizard | R2 | Not built |
| FR-028 localized branding | R2 | Not built |
| Cross-tenant | Met | `ThemeTests.Another_tenants_theme_css_assignments_and_brands_are_not_found` |
| Invalid older tokens | Met | `ThemeEngineTests.Invalid_values_from_older_data_fall_back_to_the_inherited_value` |
| SC-001 two organizations, different brands | Met for brands | As FR-001; navigation per organization is spec 008 |
| SC-002 rollback without losing layouts | R2 | Waits for the theme lifecycle |
| SC-003 wizard in under one hour | R2 | Waits for the wizard |
| SC-004 no platform brand leakage | Met for R1 | Signed-in pages carry the tenant theme from first paint: `theme.spec.ts`; full white-label journeys come with custom domains |
