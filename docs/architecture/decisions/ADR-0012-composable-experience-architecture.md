# ADR-0012: Composable experience architecture

| Field | Value |
|---|---|
| Status | Proposed |
| Date | 2026-11-05 |
| Owners | Mohamed |
| Related specs | 007, 008, 009, 021, 026, 032, 033 |

## Context and constraints
The Composable Intranet Experience requirements (docs/product/composable-experience-requirements.md) make the experience layer mandatory: a configurable shell, 12 page templates and 10 layouts independent of any component, a visual builder, semantic design tokens with inheritance across tenant and organizations, employee personalization within delegated regions, and a component contract that lets any present or future component be placed without changing templates or the builder. Their non-negotiable rule is that templates and layouts never depend on a specific component.

The frame is already set: Next.js 16 frontend and ASP.NET Core .NET 10 modular monolith (ADR-0001), PostgreSQL with row level security (ADR-0002), the same code line on SaaS, dedicated and air-gapped on-premises installs, WCAG 2.2 AA, five locales with RTL, and no new infrastructure without a reason (constitution XV, XXII).

## Decision drivers
- No component knowledge in templates, layouts, the renderer or the builder.
- Deterministic, explainable resolution of settings across platform, tenant, organization, site, audience and user.
- Tenant isolation in every cache, asset URL and preview.
- Safe extensibility: no arbitrary code from administrators; third-party code only under a sandbox.
- Performance targets (p75 LCP 2.5 s, INP 200 ms, CLS 0.1) on the reference profiles.
- Works offline-installed with no external services.

## Options considered
1. **Headless CMS or a commercial page builder product.** Fast start for page editing. Brings its own content model, permissions and tenancy, duplicates spec 004 authorization and spec 006 audit, is often non-OSI or SaaS only, and cannot run air-gapped in all cases. Rejected.
2. **Code-defined pages per customer with theme CSS files.** Simple at first. Forks the product per customer, breaks the white-labeling and governance requirements, and puts unrestricted CSS in tenant hands. Rejected.
3. **Schema-driven experience engine inside the monolith (chosen).** Page, layout, template, fragment, shell, navigation, theme, preference, component manifest and policy are separately versioned records in an `Experience` module. A resolver merges layers per policy domain into an effective configuration, stores published snapshots with provenance, and caches by tenant, scope and release. The Next.js renderer turns the effective configuration into the page; components are loaded from a registry by manifest reference.

## Decision
1. **Experience module.** A new `Experience` module in the backend owns pages, templates, layouts, fragments, shell and navigation configuration, themes and tokens, personalization policy and preference records, and the component registry. Other modules register components and never read its tables (constitution XIV).
2. **Effective configuration resolver.** Resolution runs per policy domain (tokens, structure, visibility, content eligibility, authorization) in the order platform, tenant, organization parent to child, site or page, audience, permitted user choices, with locks always winning. Each publish stores an immutable snapshot and a provenance map. Authorization is never computed here; it stays in spec 004.
3. **Component manifests.** Each component ships a versioned JSON manifest (fields in spec 032 FR-024) validated by a JSON Schema at registration. Property forms in the builder are generated from the manifest schema. Placement configuration migrates through manifest migration hooks.
4. **Rendering.** Next.js server components render the shell and layout from the effective configuration. Each placement is an isolated boundary (error boundary, suspense placeholder sized from the manifest) and loads its code lazily. Layout uses CSS grid with container queries and logical properties only.
5. **Design tokens.** Tokens are stored in a DTCG 2025.10 compatible structure (aliases, modes) and compiled per brand, mode and density into CSS custom properties served from tenant-scoped, content-hashed URLs. Token values are typed; free CSS is not accepted. Components use only semantic token names.
6. **Trust tiers.** Until R3, only platform components (code reviewed and shipped by TeamBeats) can be registered. From R3, tenant-built components run in a sandboxed iframe on a separate origin with a message contract for context, theme tokens and events, a strict content security policy, network access only to declared hosts, and no access to the session or the host page.
7. **Approvals and history.** Page, template, shell and theme releases use the process engine (ADR-0010) for approval routes and the audit service (spec 006) for history. Scheduled releases run on the existing durable job runner.
8. **Caching.** Every cache key for configuration, snapshots, token CSS and rendered fragments includes tenant ID, scope ID and release; user-specific parts are never stored in shared caches.

## Consequences and risks
- One more module and a renderer contract to maintain, but no new infrastructure.
- Container queries and the current browser matrix (spec 032 FR-063) are compatible; no polyfills planned.
- The resolver is on the hot path of every page: it must hit cache, with the budgets in spec 032 section 15.
- Contract tests with two unrelated reference component packs become a permanent CI gate.
- Sandboxed tenant components (R3) cost an extra frame per placement; they are limited per page.

## Security and multi-tenancy implications
- Tenant ID comes from the session; asset and token URLs are tenant scoped and checked; no cross-tenant cache hit is possible by key design.
- Configuration fields are typed; executable content is rejected at the API (spec 032 FR-035, spec 026 FR-025).
- Preview uses synthetic personas and the editor's own permissions for data.
- Locks and protected regions are enforced by the API, not only the builder.

## Rollout and migration plan
- R1: Experience module, resolver, 10 layouts, 12 templates with variants, visual builder and previews, component registry for platform components, shell, navigation, home personalization, tokens with tenant and organization brands, rollback.
- R2: approval routes, impact reports, fragments, theme lifecycle, custom domains, experience analytics.
- R3: sandboxed tenant components, template recommendations, experiments.
- Existing tenant branding from spec 001 (logo, primary color) migrates into the tenant's first brand profile.
