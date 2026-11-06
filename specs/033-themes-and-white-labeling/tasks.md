# Tasks: Themes and White Labeling

Depends on 032 (`Experience` module), 001 (tenant logo and color), 002 (org hierarchy), 004, 005, 006. R1 covers the token engine and resolver, tenant brands, org overrides, assets, light and dark modes, typography by script, contrast validation, scope model, locks, effective configuration trace and stable IDs. Theme lifecycle, high contrast, preview as organization, wizard, domains and export are R2.

## Phase 1: Foundations
- [ ] T001 Migration: `brand`, `brand_version`, `brand_assignment`, `experience_lock` with forced RLS
- [ ] T002 Token schema with types, modes and densities; platform default tokens; approved font list per script
- [ ] T003 Permissions `theme.edit`, `theme.publish`, `config_lock.manage`; built-in role `brand_manager`; capability keys of section 9

## Phase 2: Brand profiles and validation (P1)
- [ ] T004 [P] Tests: typed values only (no `url()`, expressions or unknown fonts); alias cycles refused; contrast findings for normal text, large text, UI cues and focus with ratios in text; shell pairs block and accent pairs warn by default; brand count limited by `experience.max_brands`
- [ ] T005 Brand create, update as a new version, check endpoint, asset references with the spec 001 validator; audit `theme.published` and `ThemePublished`

## Phase 3: Scopes, inheritance and locks (P1)
- [ ] T006 [P] Tests: two org units get different tokens on the same template (S01); child override of a locked token refused through the API; parent without subtree scope cannot edit a child override; sibling cannot read another sibling's assignment; org overrides need `experience.org_branding`; provenance only with `config_history.view`
- [ ] T007 Assignments and overrides per scope, locks with the default lock set, resolver with provenance and cache invalidation on events

## Phase 4: Delivery (P1)
- [ ] T008 [P] Tests: theme CSS of another tenant's scope is not found; CSS under 30 KB compressed per brand and mode; invalid older token falls back to the last valid theme with `theme.invalid_token`
- [ ] T009 Theme and theme CSS endpoints; seeding the default brand from spec 001 tenant settings; tenant settings page links to the brand manager

## Phase 5: Frontend
- [ ] T010 Web app reads only `--tb-*` variables generated from the token schema, with a lint rule against raw colors and sizes; theme switches by scope, mode and density with no layout shift
- [ ] T011 Brand manager page with token groups, contrast table, asset upload and live preview in both modes and directions, in English and Arabic

## Final phase
- [ ] T012 OpenAPI, R1 implementation notes, spec evidence
