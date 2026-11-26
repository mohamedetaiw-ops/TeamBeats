# Implementation Plan: Page Composition

**Spec:** `specs/032-page-composition/spec.md` (v1.0, Approved for R1) | **Date:** 2026-11-06, R3 added 2026-11-26
**Shared base:** ADR-0012 (composable experience), ADR-0013 (ports inside modules), ADR-0014 (frontend architecture), `docs/architecture/module-map.md` (`Experience` module)

## Summary
Pages become versioned declarative records in a new `Experience` module. A page names a platform template and layout by ID and version, and holds regions with placements of registered components. The backend validates every save and publish (layout rules, region policy, manifest property schema, executable content), keeps immutable releases, and resolves an effective configuration per request from cache. The Next.js renderer turns that configuration into a CSS grid page where each placement is an isolated boundary that loads its code from the component registry. Specs 008 and 033 build on the same module: 008 adds the shell, navigation and personal layouts, 033 adds brands and tokens.

R1 slices, in order:
1. **Backend foundation:** module, schema, layout and template catalogue (L01 to L10, T01 to T12), component registry with manifests and two reference packs, validator, pages with draft, publish and rollback, effective structure resolver, audit and events.
2. **Renderer:** `lib/experience` (resolver client, registry, placement decorator, event channel), the 10 layouts with container queries and logical properties, the reference packs in `components/placements`, shared states from spec 008.
3. **Builder:** template chooser with previews, picker, schema-driven property forms, keyboard composition, command stack for undo and redo, autosave with optimistic concurrency, previews by device and direction, accessibility and translation checks, publish and rollback.
4. **Hardening:** fault injection, performance budgets in CI, contract tests with both reference packs against every template.

Approval routes, impact reports (FR-044 and FR-054 beyond the affected page list), fragments, scheduling, import and export, feature controls and resizing are R2 and not planned here.

## Constitution check
| Principle | Pass | Evidence |
|---|---|---|
| I, II tenant isolation | Yes | Every tenant table has `tenant_id` with forced RLS; cache keys carry tenant, scope and release; the platform catalogue holds no tenant data |
| III authorization | Yes | `page.*`, `template.*`, `component.enable` checked by the central service at the page's org scope; visibility rules are targeting only |
| XIV module boundaries | Yes | Other modules register components by manifest and serve their own data; `Experience` never reads their tables |
| XXI entitlements | Yes | Builder endpoints check `experience.page_builder`; custom template count checks `experience.max_custom_templates`; a manifest's declared capability hides components the package does not include |
| XXVI security | Yes | Typed property schema, no raw HTML, URL scheme checks, CSP; executable content refused at the API |
| XXX thin frontend | Yes | Region rules, protection and validation live in the API; the builder only shows what the API returns |

## Module and data model (schema `experience`)
- `Experience` module with `Experience.Contracts` (component registration, effective configuration reader, events). Migrations `0001_experience.sql` onward. Region policy is read from the template version the page records, so a page editor cannot loosen it.
- Platform catalogue, shipped in code as embedded JSON and loaded at start: layouts L01 to L10 and templates T01 to T12, each with an ID, a version and regions. Platform data is read-only and has no `tenant_id`, which is listed in the RLS exemption list.
- Component manifests are platform data too: the registry is built in memory at start from the manifests that modules contribute through `IComponentRegistration`, validated, and never stored per tenant. Platform only until R3 (FR-031).
- `component_enablement` (tenant): `tenant_id`, `component_id`, `enabled`, `changed_by`, `changed_at`. Only tenant choices are stored, so a component with no row is enabled.
- `page` (tenant): `tenant_id`, `id`, `org_unit_id` (scope, immutable ID per spec 033 FR-026), `slug`, `owner_id`, `template_id`, `template_version`, `current_release`, `created_at`.
- `page_draft` (tenant): one working draft per page, `revision` as the optimistic concurrency token (sent as the `ETag`), replaced on save.
- `page_release` (tenant): `tenant_id`, `page_id`, `release`, `definition jsonb`, `published_by`, `published_at`, `reason`, `rollback_of`. The application role may only insert and read, so a release can never change (FR-050, FR-052).
- `template` (tenant, added with T011, for tenant copies of platform templates with locks): `tenant_id`, `template_id`, `version`, `state`, `definition jsonb`, `distribution uuid[]` (org units). Limited by `experience.max_custom_templates`.

Page definition (JSON, stored in `page_version.definition`): `templateRef`, `layoutRef`, `variant` (`spacious`, `balanced`, `compact`), `sections[]` with `mode` (`contained`, `full_bleed`, `fixed`, `fluid`), and `regions[]` each with `id`, `policy` (allowed categories, min and max placements, protection, personalization), `order` per breakpoint and `placements[]` with `id`, `componentRef` (`id@major`), `props`, `visibilityRuleRef`. It holds no business data, secrets or user data (FR-003).

## Component manifest (FR-024 to FR-031, FR-067 to FR-069)
- Manifest JSON fields: `id`, `version`, `publisher`, `compatibility` (platform range), `display` (resource keys for name and description, category, icon), `properties` (typed property schema), `placement` (allowed categories, min and max width in columns, singleton, duplicate), `responsive` (container breakpoints, fallback), `context` (subset of the approved list of FR-028), `theme` (token names used), `a11y` (role, name source), `states` (supports empty, error, unavailable), `requires` (capability keys, permission keys, connection feature), `trust` (`platform`), `events` (`publishes`, `subscribes` with payload schemas), `actions`, `data` (module endpoints read), `migrations` (from version, mapping).
- Property schema: a restricted subset of JSON Schema that the module validates itself: `string`, `text` (localized plain text), `number`, `integer`, `boolean`, `enum`, `url` (https and relative only), `image` (asset reference), `color_token` and `list` of these, with `default`, `required`, `minimum`, `maximum`, `minLength`, `maxLength`. No raw HTML type exists. Using our own validator for this subset avoids a new JSON Schema dependency (rule 6); it is listed as a deviation from ADR-0012 point 3 in the R1 implementation notes.
- Manifest validation at registration: structure, known context keys only, known token names, resource keys present in `en` and `ar`, compatibility range, unique ID and version. A failed manifest is not registered and the start-up log names the reason.
- Two reference packs for contract tests, `reference.alpha` (text block, link list, KPI tile, filter publisher) and `reference.beta` (image banner, item list subscriber, counter), unrelated to each other and to any product module. They ship in the dev host only.

## Validator (FR-010, FR-012, FR-019, FR-035, business rules)
Runs on every draft save and again on publish. Each finding has a code, a severity (`blocking` or `warning`), the region ID and the placement ID. Checks: layout and template exist and are not retired for new pages; every region exists in the layout; region policy (allowed categories, min and max placements, protected regions present); span within 1 to 12 and allowed by the layout; no two placements in the same cell; nesting depth at most 3; at most 40 placements and 12 regions (tenant may lower); singleton components once; component registered, enabled and not retired; props valid against the manifest schema; no script, `javascript:` or `data:` URLs, event handler attributes or embed hosts outside the tenant allowlist; breakpoint order keeps DOM and reading order (order values must keep relative order of placements within a region the same as DOM order unless the layout declares a safe reorder); every visibility rule has a default for users who match none.

## Lifecycle and API (v1)
- `GET /api/v1/experience/catalogue` (layouts, templates with text descriptions and preview hints), `GET /api/v1/experience/components` (picker: eligible components with disabled reasons for the caller and a region).
- `POST /api/v1/experience/pages` from a template (FR-018), `GET /api/v1/experience/pages/{id}` (draft and release info), `PUT /api/v1/experience/pages/{id}/draft` with `If-Match` (409 with both versions on conflict, FR-042), `POST /api/v1/experience/pages/{id}/validate`, `POST /api/v1/experience/pages/{id}/publish`, `POST /api/v1/experience/pages/{id}/rollback` (to a release number, as a new release), `GET /api/v1/experience/pages/{id}/releases`.
- `GET /api/v1/experience/render?slug=` returns the effective configuration of the latest release for the caller: structure, placements whose components are available and visible to the caller, unavailable placeholders for retired ones, the theme reference (spec 033) and the provenance trace only for callers with `config_history.view`.
- Component enablement: `PUT /api/v1/experience/components/{id}/enablement`.
- A page of another tenant, or outside the caller's scope for editing, answers 404 (FR-056, US8).

## Effective configuration (FR-002, FR-065)
Structure domain only in slice 1; tokens come in spec 033. The resolver takes the release snapshot, applies template locks, removes placements whose component is disabled, retired or not entitled, and leaves visibility rules for the request step. Snapshot cache: `IMemoryCache` keyed `(tenant_id, page_id, release_number)`, invalidated on `PagePublished`, `PageRolledBack`, `ComponentRetired` and `EntitlementsChanged`. Per-request visibility is evaluated after the cache and never stored in it.

## Permissions and entitlements
- New permissions (spec 004 catalogue): `page.view`, `page.edit`, `page.publish`, `template.edit`, `template.distribute`, `component.enable`, `config_history.view`.
- New built-in roles `page_editor` (`page.view`, `page.edit`) and `page_publisher` (`page.view`, `page.edit`, `page.publish`); tenant and organization administrator get all page permissions; employee gets `page.view`.
- New capability keys: `experience.page_builder`, `experience.max_custom_templates`, `experience.fragments`, `experience.approval_routes`, `experience.feature_controls`, `experience.custom_components`, with package values from spec section 9.

## Audit and events
Audit `page.created`, `page.published`, `page.rolled_back`, `component.enabled`, `component.disabled`, `template.released`, `region.lock_changed` with before and after references. Events `PagePublished`, `PageRolledBack`, `ComponentRegistered`, `ComponentRetired`, `TemplateReleased` through the outbox.

## Frontend (ADR-0014)
- `lib/experience`: typed client for the render API, the registry (`componentRef` to a lazy import from `components/placements/<id>`), the placement decorator (error boundary, size-reserving placeholder, visibility, telemetry), the page-scoped event channel that checks declared events and payload schemas, and the context provider that passes only declared context keys.
- Layouts: CSS grid with 12 columns, `container-type: inline-size` on regions, logical properties only, the fallback rules of FR-013, reference widths 320 to 1920.
- Builder in `features/page-builder`: Zustand store for the draft document, command stack (`apply` and `revert`) for undo, redo and autosave, action menu and shortcuts for every operation, live region announcements, preview frame at three sizes in LTR and RTL.
- TanStack Query, Radix UI, Zustand and Tailwind arrive with the builder slice as ADR-0014 allows; the Tailwind lint spike decides between Tailwind and CSS modules.

## Tests
- Backend: validator unit tests per rule; page lifecycle (draft never visible, release immutable, rollback as a new release, conflict on stale `If-Match`); picker eligibility (not enabled, not entitled, wrong region, missing permission); cross-tenant and sibling scope denials as 404; executable content refused; architecture test that templates, layouts and the builder never name a concrete component ID (FR-014); manifest validation.
- Frontend: renderer with both reference packs on all 12 templates (swap test S02), fault injection (throw, timeout, missing manifest), event channel drops undeclared events, axe in `ar` and `en`, Playwright at 320 and 1920 widths.

## R3 (2026-11-26)
R3 adds template recommendations (FR-023) and tenant-built components in a sandbox (FR-031, ADR-0012 point 6), in that order.

R3 slice 1, **template recommendations (FR-023):**
- `GET /api/v1/experience/template-recommendations?purpose=&viewport=&density=` for anyone who may edit pages somewhere (`page.edit` coverage), under `experience.page_builder`. Rules only on page metadata, no model call and no page content or user data (section 11): each purpose has three fitting platform templates in order, a phone viewport lifts one column layouts (L01, L06, L07), and density picks the variant (light spacious, medium balanced, heavy compact) and lifts templates with fitting region counts. Up to three results, each with its variant and reasons. Templates switched off for the tenant (feature controls) are never recommended.
- The new page screen gains "Help me choose": three choices mark the fitting template cards with a Recommended badge and the reasons; every template stays on offer and nothing is chosen for the editor, picking a marked card takes its variant.

Defaults (spec silent):
- Purposes: general, news, department, project, knowledge, services, campaign, dashboard, onboarding. Viewports: desktop, tablet, phone. Density: a little, some, a lot.
- Only platform page templates (T01 to T12) are scored, since a tenant template has no declared purpose; the employee home template is not a page template.

R3 slice 2, **tenant-built components in a sandbox (FR-031):**
- Under `experience.custom_components` (Enterprise and Government). A tenant administrator (`component.enable` for the whole tenant) submits a component: a manifest subset (id `tenant.<slug>`, version, names in English and Arabic, typed properties, declared hosts, context keys from the approved list of FR-028, events it publishes) and one HTML document of at most 256 KB with its script and style inline. Versions are immutable.
- A platform operator reviews each version on the platform host (security review, ADR-0012) and approves or rejects it with a reason; only an approved version can be enabled, and only then does it appear in the picker. Audited on both sides.
- The document is served by the API at `/api/v1/experience/sandbox/{id}/{version}` for signed in members of the tenant, with its own policy: `sandbox allow-scripts` (an opaque origin, so no cookies, session, storage or page DOM), `default-src 'none'`, inline script and style only, `connect-src` and `img-src` limited to the declared HTTPS hosts, `frame-ancestors 'self'`, no referrer. The page renders it in an `iframe sandbox="allow-scripts"` and talks to it only by `postMessage`: the host sends the declared context values, the theme tokens and the placement properties; the frame may send a height and its declared events, checked against the manifest and dropped otherwise.
- At most three sandboxed placements per page (ADR-0012 consequence). Each placement keeps the error boundary and a size reserving placeholder.

