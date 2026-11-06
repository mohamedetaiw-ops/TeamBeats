# Implementation Plan: Themes and White Labeling

**Spec:** `specs/033-themes-and-white-labeling/spec.md` (v1.0, Approved for R1) | **Date:** 2026-11-06
**Shared base:** ADR-0012 point 5 (design tokens), spec 032 plan (`Experience` module, resolver, cache keys), ADR-0014 (styling)

## Summary
Brand profiles live in the `Experience` module as typed semantic tokens with light and dark modes and two densities. A brand is assigned to the tenant and optionally overridden per org unit, inheriting parent then child with locks winning. The theme resolver compiles the effective token set per brand, scope, mode and density into CSS custom properties served from a tenant-scoped, content-hashed URL. Token values are typed, so free CSS cannot enter. Contrast and focus checks run on every save. The spec 001 logo and primary color seed the tenant's first brand.

R1 slices, in order:
1. **Backend:** token schema and typed validation, brand profiles and versions, assignments and overrides per org unit, locks, theme resolver with provenance, contrast checks, compiled CSS endpoint, seeding from tenant settings, audit and events.
2. **Frontend:** the web app reads only semantic token variables; shell and placements switch theme by scope without layout shift; brand manager page with contrast table and a live preview of sample components.

Theme draft, approval, schedule and rollback (FR-012), high contrast mode, preview as organization, starter wizard, delegated experience administration, cross-organization journeys, custom domains, token export, drift report, localized branding and token extensions are R2.

## Constitution check
| Principle | Pass | Evidence |
|---|---|---|
| I, II tenant isolation | Yes | Brand tables have `tenant_id` and forced RLS; CSS and asset URLs carry the tenant and are checked against the session; cache keys carry tenant, scope and brand version |
| III authorization | Yes | `theme.edit`, `theme.publish`, `config_lock.manage` at tenant or org scope; theme data never feeds authorization (FR-017) |
| XXI entitlements | Yes | `experience.max_brands` limit; org overrides and locks need `experience.org_branding` |
| XXVI security | Yes | Typed token values, no `url()`, no expressions, fonts only from the approved list; SVG assets sanitized as spec 001 does |
| VII accessibility | Yes | Contrast 4.5:1 and 3:1, focus indicator and minimum text size checks block shell and navigation failures by default |

## Token model (FR-003, FR-006, FR-007)
- Token schema shipped in code: semantic names grouped as `color.*` (surface, text, accent, border, state, focus), `font.*` (family per script, size scale, weight scale, line height), `space.*`, `radius.*`, `border.*`, `shadow.*`, `motion.*` (duration, easing) and `state.*`.
- Types: `color` (hex or rgb only), `dimension` (px or rem within bounds), `font_family` (approved list per script: Latin, Arabic, Urdu Nastaliq, Devanagari, with fallbacks), `number`, `duration` (ms within bounds), `easing` (named curves), `alias` (`{color.accent.base}`). Anything else is refused at the API with the token name.
- Modes `light` and `dark`; densities `comfortable` and `compact` override only `space.*` and `font.size.*`. Stored in a structure compatible with DTCG 2025.10 (`$value`, `$type`, aliases, modes as extensions) so R2 export is a projection.
- Platform default tokens are the base layer and always complete, so a partial brand never leaves a token unset.

## Data model (schema `experience`)
- `brand` (tenant): `tenant_id`, `brand_id`, `name`, `current_version`, `created_at`. Limited by `experience.max_brands`.
- `brand_version` (tenant): `tenant_id`, `brand_id`, `version`, `tokens jsonb`, `assets jsonb` (logo, lockup, favicon, default image, sign-in image as asset references), `created_by`, `created_at`. Immutable once written; R1 publishes on save, R2 adds drafts and approval.
- `brand_assignment` (tenant): `tenant_id`, `scope_org_unit_id` (null means tenant), `brand_id`, `overrides jsonb` (token values the org changes), `changed_by`, `changed_at`.
- `experience_lock` (tenant): `tenant_id`, `scope_org_unit_id`, `setting` (token name or shell setting key), `locked_by`, `locked_at`. Default lock set on first enable: legal footer, security banners, accessibility minimums, sign-in presentation.
- Assets reuse the tenant asset store and validator of spec 001 (PNG, JPEG, sanitized SVG, 1 MB), with tenant-prefixed paths.

## Resolver (FR-004, FR-018, FR-019, FR-026)
Order: platform default, tenant brand, org unit assignments from root to the target unit (by immutable IDs from `ITenantOrgHierarchy`), then user-allowed choices (mode and density, spec 008). A locked token keeps the value of the scope that locked it; a child override of a locked token is refused at the API and ignored by the resolver if it exists from earlier data. Output: a flat map of token name to value per mode and density, and a provenance map of token name to the scope and brand version that set it, shown only to `config_history.view` holders. Cache key `(tenant_id, scope_org_unit_id, brand versions, lock version)`; invalidated on `ThemePublished`, `BrandAssignmentChanged`, `ExperienceLockChanged` and `OrganizationUnitMoved`.

## Validation (FR-009, business rules)
For each mode: text on surface pairs at 4.5:1, large text and essential UI cues at 3:1, focus indicator at 3:1 against adjacent colors, minimum body size 14 px. Findings name the token pair and ratio in text. Shell and navigation pairs block, page accent pairs warn, per the default policy; a tenant can make both block. Invalid token values from older data at render time fall back to the last valid resolved theme and record `theme.invalid_token` (spec 032 FR-066).

## API (v1)
- `GET /api/v1/experience/brands`, `POST /api/v1/experience/brands`, `GET /api/v1/experience/brands/{id}`, `PUT /api/v1/experience/brands/{id}` (new version, validation findings in the answer), `POST /api/v1/experience/brands/{id}/check` (findings without saving).
- `PUT /api/v1/experience/brand-assignments/{scope}` (tenant or org unit ID), `PUT /api/v1/experience/locks/{scope}`.
- `GET /api/v1/experience/theme?scope=` effective tokens and, when allowed, provenance.
- `GET /api/v1/experience/theme.css?scope=&mode=&density=&v=` compiled CSS custom properties, `Cache-Control: private, max-age` with the hash in `v`; the tenant comes from the session, and a scope of another tenant answers 404.

## Seeding (business rule from spec 001)
When the module first runs for a tenant, the spec 001 logo and primary color become version 1 of a default brand assigned at tenant scope. From then on the tenant settings page links to the brand manager, and the theme reads only brands. The tenant settings contract gains nothing; seeding reads `ITenantSettingsReader`.

## Permissions and entitlements
- New permissions: `theme.edit`, `theme.publish`, `config_lock.manage` (and `domain.manage` reserved for R2, not added yet). `config_history.view` comes from spec 032.
- New built-in role `brand_manager` (`theme.edit`); tenant administrator gets all, organization administrator gets `theme.edit` and `theme.publish` (used at org scope only when `experience.org_branding` is on).
- New capability keys: `experience.max_brands` (1, 5, unlimited), `experience.org_branding`, `experience.custom_domain`, `experience.token_extensions`.

## Audit and events
Audit `theme.published`, `brand.assigned`, `brand.unassigned`, `experience.lock_changed`. Events `ThemePublished`, `BrandAssignmentChanged`, `ExperienceLockChanged`. Telemetry `theme.invalid_token`, `theme.font_fallback_used`.

## Frontend
- The root layout links the theme CSS for the current scope, mode and density. Every style uses `var(--tb-*)` names generated from the token schema; a lint rule refuses raw color and pixel literals outside the token file.
- Brand manager page in `features/brand`: token groups, contrast table with ratios in text, asset upload, live preview of shell and sample placements in both modes and directions.

## Tests
- Unit: typed token validation (refuses `url()`, `expression`, unknown fonts, out-of-range sizes), alias resolution and cycles, contrast math against known pairs, resolver order and locks.
- Integration: two org units in one tenant get different tokens on the same page (S01); child cannot override or unlock a parent lock through the API; sibling cannot read another sibling's assignment; another tenant's theme CSS or asset is not found; brand limit by package; seeding from tenant settings; theme CSS stays under 30 KB compressed.
