# Implementation Plan: Experience Shell, Navigation, Employee Home and Personalization

**Spec:** `specs/008-experience-shell-and-personalization/spec.md` (v1.0, Approved for R1) | **Date:** 2026-11-06
**Shared base:** spec 032 plan (`Experience` module, page model, renderer), spec 033 plan (tokens), ADR-0012, ADR-0014

## Summary
The shell, navigation, preference profile and personal layouts join the `Experience` module. The shell is a configuration record with regions (identity, global navigation, organization indicator, search and quick navigation, notifications, task badge, contextual actions, breadcrumbs, settings, utilities) rendered by the web app around every page, so moving the search box or adding a utility is a publish, not a deployment. Navigation is a set of independent menus over an information architecture of labelled nodes with slugs and redirects. The employee home is a spec 032 page from the default home template, with a protected region and a delegated personal region. A personal layout is a list of operations (add, hide, remove, move) over the published page, stored per user and page, so it survives page releases and can be reset without touching the page or other preferences.

R1 slices, in order:
1. **Shared states and interaction primitives** in `components/ui`: state views (loading, success, empty, error, permission denied, offline, unsupported), message rules (critical persists, low priority never blocks), buttons with action hierarchy, confirm and undo, form field errors, skeletons, density and reduced motion. These come first because the renderer and every placement use them.
2. **Shell and navigation backend:** shell configuration and menus with publish, IA nodes with slugs and redirects, breadcrumbs, quick navigation over permitted pages, the recovery answer for missing or inaccessible destinations.
3. **Preferences and personal layouts backend:** preference profile, personalization policy per page and region, personal layout operations with protection enforced by the API, reset one and reset all, targeting with a default variant.
4. **Shell and home frontend:** the shell frame, menus, settings entry (language, mode, density, accessibility, personalization), personalization mode on the home with keyboard alternatives and undo, and the tool box utilities (weather, world clock, time zone converter) as platform components.

Resize, presets, device-specific choices, preference export, favorites and recents, the command entry point, focus mode, help and tours, and suggestions are R2. Behavioral personalization is R3 and stays off.

## Constitution check
| Principle | Pass | Evidence |
|---|---|---|
| I, II tenant isolation | Yes | Shell, navigation, policies, preference and personal layout tables carry `tenant_id` with forced RLS; client caches are keyed by tenant and cleared on switch |
| III authorization | Yes | `shell.edit`, `navigation.edit`, `personalization.policy`, `personal_layout.edit` checked centrally; quick navigation lists only pages the caller may view; visibility rules are targeting, not access (FR-029) |
| XXI entitlements | Yes | Home personalization in every package; personal regions on other pages need `experience.personalization`; audience variants need `experience.audience_variants` |
| XXX thin frontend | Yes | Protection, policy and precedence are decided by the API; the frontend shows the result and sends intents |
| Privacy | Yes | Preferences hold no business data (FR-036); no behavior tracking in R1 (FR-033) |

## Data model (schema `experience`)
- `shell_config` (tenant): `tenant_id`, `version`, `definition jsonb` (region order and which utilities are on), `published_at`, `published_by`. Immutable versions; the latest is live.
- `nav_menu` (tenant): `tenant_id`, `menu_id`, `kind` (`global`, `organization`, `section`, `contextual`), `scope_org_unit_id`, `version`, `items jsonb` (node references and order). Each menu publishes on its own (FR-011).
- `ia_node` (tenant): `tenant_id`, `node_id`, `parent_id`, `label` (resource key or localized text per locale), `slug`, `page_id` or `url`, `owner_org_unit_id`. `slug_redirect` (tenant): `tenant_id`, `old_slug`, `node_id`, `created_at` (FR-013).
- `personalization_policy` (tenant): `tenant_id`, `page_id`, `region_id`, `audience` (rule reference or all), `rights` (`add`, `hide`, `remove`, `reorder`), `max_personal_placements` (default 12). Default for new tenants: the home personal region delegated to all employees.
- `preference_profile` (tenant): `tenant_id`, `user_id`, `schema_version`, `values jsonb` (language, mode, density, reduced motion, text scale), `updated_at`. Only keys of the preference schema are accepted (FR-021, FR-036).
- `personal_layout` (tenant): `tenant_id`, `user_id`, `page_id`, `schema_version`, `operations jsonb`, `updated_at`. Primary key `(tenant_id, user_id, page_id)`.
- Visibility rules: `audience_rule` (tenant): `tenant_id`, `rule_id`, `conditions jsonb` (role, group, org unit, locale, approved attributes), evaluated with the spec 004 condition evaluator for targeting only.

## Personal layout model (FR-022 to FR-027, FR-031)
- Operations: `add { placementId, componentRef, regionId, props, position }`, `hide { placementId }`, `remove { placementId }` (only personal placements), `move { placementId, regionId, position }`.
- Apply order at render: published page, then audience rules, then the personal operations allowed by the current policy, with locks and protected regions always winning (FR-031). Operations that no longer apply (placement gone, region no longer delegated, component retired) are skipped and reported to the user, not deleted, so they return if the policy returns.
- The API validates each save: the region is delegated for the user's audience, the component is enabled, entitled and permitted for the user, the region maximum holds, protected placements are never hidden or removed (S04). Refusals are 422 with the reason; another user's layout is not found.
- Undo keeps the last 20 operations per page in the session (frontend command stack); reset one deletes the page's row; reset all deletes the user's rows and leaves the preference profile alone.

## Shell, navigation and recovery (FR-001, FR-011 to FR-019)
- `GET /api/v1/experience/shell` returns the live shell definition, the menus for the caller's scope with only nodes whose pages the caller may view, the organization indicator when the caller can switch scopes, and badge sources. Components of other modules (notifications, task badge, mail badge) are registered utilities; the shell holds only their placement.
- Breadcrumbs come from the IA parents of the current node. A renamed or moved node keeps a redirect from each old slug.
- `GET /api/v1/experience/resolve?path=` maps a path to a page or redirect. Missing, unpublished and not permitted all answer the same recovery result with links to home and search, so existence is never revealed (FR-019).
- Quick navigation: `GET /api/v1/experience/quick-nav?q=` over IA nodes and published pages the caller may view, until search (spec 012) is built; then it delegates to search.
- Admin: `PUT` shell, menus and IA nodes with publish, audit `shell.published`, `navigation.published`, events `ShellPublished`, `NavigationPublished`.

## API for preferences and personalization (v1)
- `GET` and `PUT /api/v1/me/preferences`.
- `GET /api/v1/me/pages/{pageId}/layout`, `PUT /api/v1/me/pages/{pageId}/layout` (whole operation list with `If-Match`), `DELETE /api/v1/me/pages/{pageId}/layout` (reset one), `DELETE /api/v1/me/layouts` (reset all).
- `GET /api/v1/me/pages/{pageId}/add-panel?region=` eligible components with reasons, from the same eligibility rules as the spec 032 picker plus personal rights.
- Admin: `PUT /api/v1/experience/pages/{pageId}/personalization-policy`, audience rules and a targeting preview with synthetic personas.

## Permissions and entitlements
- New permissions: `shell.edit`, `navigation.edit`, `personalization.policy`, `personal_layout.edit`, `help.edit` (reserved for R2 tours, added now so roles are stable).
- Employee gets `personal_layout.edit`; tenant administrator gets all; organization administrator gets `navigation.edit` and `personalization.policy` at org scope.
- New capability keys: `experience.personalization`, `experience.audience_variants`, `experience.guided_tours`, `experience.behavioral_personalization`.

## Frontend (ADR-0014)
- `components/ui`: state views, message centre with priority rules, button hierarchy, confirm and undo, field errors with live announcements, skeletons sized from the manifest, density classes, reduced motion. All labels are resource keys.
- `features/shell`: shell frame from the shell definition, accessible menus (Escape, outside click, focus leaving), mobile navigation with the same labels, organization indicator, breadcrumbs, settings panel.
- `features/personalization`: personalization mode store (Zustand) with the command stack, add panel, move up, move down and move to region actions, live region announcements, reset with confirmation.
- `components/placements/toolbox-weather`, `toolbox-world-clock`, `toolbox-time-converter`: platform manifests and components. The weather placement reads through a backend connector port; until a weather provider decision is made it shows the unsupported state (recorded as an open decision).

## Tests
- Integration: shell publish changes no page; menus publish independently; quick navigation and menus hide pages the caller cannot view; recovery answer is the same for missing, unpublished and not permitted; redirects after rename; personal add only in delegated regions with eligible components; protected placement hide or remove refused through the API; coworkers unaffected; reset one and reset all; precedence collision tests (FR-031); default variant when no audience matches; another tenant's or user's layout not found.
- Frontend: menu keyboard and screen reader behavior, personalization by keyboard only, undo order, density and reduced motion, axe in `ar` and `en`, mobile reachability.
