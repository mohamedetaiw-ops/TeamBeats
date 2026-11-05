# Feature Specification: Brands, Themes, White Labeling and Multi-Organization Experience

| Field | Value |
|---|---|
| Spec ID | 033-themes-and-white-labeling |
| Area | experience |
| Status | Proposed |
| Version | 0.1 |
| Owner | Mohamed |
| Approver | Mohamed |
| Target release | R1 (semantic token engine and theme resolver, tenant brand profiles, organization brand overrides, brand assets, light and dark modes, typography by script, contrast validation, scope model, locks, effective configuration trace, stable IDs), R2 (theme draft, approval, schedule and rollback, high contrast mode, impact analysis, preview as organization, starter wizard, delegated experience administration, cross-organization journeys, custom domains, token export, drift report, localized branding, reviewed token extensions) |
| Last updated | 2026-11-05 |
| Requirement IDs | CX-BRD-001 to CX-BRD-015, CX-ORG-001 to CX-ORG-011, CX-TMP-011, CX-LOC-006, ADM-001 (branding) |
| Use cases | UC-01 (tenant setup, branding part) |
| Depends on | 001 (tenant settings), 002 (org units), 004 (authorization), 005 (entitlements), 006 (audit), 007 (localization and accessibility), 011 (brand asset portal), 024 (approval routes), 032 (page composition) |

Source: [Composable Intranet Experience requirements](../../docs/product/composable-experience-requirements.md). IDs from that document carry a `CX-` prefix here.

## 1. Problem and outcome

**Problem:** A group with several subsidiaries wants one intranet in which each subsidiary looks like itself: its own logo, colors, fonts and navigation, while group rules (legal footer, accessibility, security banners) stay fixed. Today this means cloning the frontend or writing custom CSS per customer, which breaks on every upgrade, leaks one brand into another and often fails contrast. Admins also cannot tell why a page looks the way it does when several levels of settings apply.

**Outcome:** Brand and visual identity are data. Each tenant keeps one or more brand profiles expressed as semantic design tokens and assets. Organizations inherit and override approved properties within parent locks, so two subsidiaries show different themes in the same tenant at the same time. Visual inheritance, structural inheritance and security inheritance are separate, so a theme change can never grant access. Every effective value can be explained, every theme change can be previewed, approved, scheduled and rolled back, and inaccessible choices are caught before they ship. A new organization gets a working branded experience from a starter wizard without a developer.

## 2. Scope

**In scope:**
- Semantic design tokens for color, typography, spacing, radii, borders, shadows, motion and component states, with aliases and modes (light, dark, high contrast) and density (comfortable, compact).
- Brand profiles per tenant; organization, nested organization and site overrides; user-allowed choices (mode and density, spec 008).
- Brand assets for the shell: logos and lockups, favicon, default imagery, sign-in imagery, per locale where needed.
- Typography policy by writing system (Latin, Arabic, Urdu Nastaliq, Devanagari) with fallbacks.
- Validation: contrast, focus indicator, minimum text sizes, mandatory affordances; drift report.
- Scope model and inheritance for experience configuration: tenant, organization, nested organization, site and audience, with protected (locked) parent settings, delegated experience administration, scoped page catalogues and sharing, cross-organization journeys, reparenting safeguards.
- Effective configuration trace and preview as organization, audience, language and device.
- Theme lifecycle: draft, preview, approval, schedule, release, rollback.
- White labeling: custom domain, app title, browser metadata, sign-in presentation; no platform brand leakage.
- Starter configuration wizard for a new organization.
- Token export and import in the Design Tokens Community Group 2025.10 format.

**Out of scope:**
- Curated asset collections, usage rights and renditions for employees (brand asset portal, spec 011). Shell brand assets here may be picked from that portal.
- Page structure and templates (spec 032). This spec owns the scope model they inherit through.
- Content audience targeting rules for content items (spec 009) and personal preferences (spec 008).
- Finished customer brand designs. TeamBeats ships a neutral default brand only.

## 3. Actors and permissions

| Actor | What they can do in this feature | Permission / scope required |
|---|---|---|
| Platform operator | Maintain platform default tokens and the token schema; register fonts | Platform role |
| Tenant administrator | Create brand profiles, assign them to scopes, set locks, delegate experience administration, configure domains | `theme.edit`, `theme.publish`, `config_lock.manage`, `domain.manage` at tenant scope |
| Brand manager (design administrator) | Edit tokens, assets and variants within allowed scope; preview; submit | `theme.edit` at tenant, org or site scope |
| Organization administrator | Apply and override approved brand properties for their scope; manage their scope's pages and navigation | `theme.edit`, `theme.publish` at org or subtree scope, if delegated |
| Approver | Approve theme and shared configuration releases | Assigned by route (spec 024) |
| Security officer, compliance officer | Read theme and configuration history | `config_history.view` |
| Employee | Choose mode and density where allowed (spec 008) | Tenant membership |

A brand manager has no automatic right to change business permissions (source section 3). `theme.*` grants nothing outside visual configuration.

## 4. User scenarios

### User Story 1: Two subsidiaries, two brands, one tenant (Priority: P1, R1)

Acme Group's tenant admin creates two brand profiles, "Acme Gulf" (teal, Latin and Arabic fonts) and "Acme Africa" (orange), assigns each to its org subtree, and locks the group legal footer and security banner. Employees of each subsidiary see their own logo, palette and navigation on the same page template at the same moment. Group pages shared with both keep the group brand.

**Why this priority:** multi-organization white labeling is the headline of the experience layer and source scenario S01.

**Independent test:** assign two brands to two sibling units; render the same template for a user in each; compare tokens and assets; confirm no source change and no leakage between them.

**Acceptance scenarios:**
1. **Given** two brand profiles, **When** the tenant admin switches the active assignment of a unit, **Then** the unit's pages show the new brand without code changes. *(CX-BRD-001)*
2. **Given** units A and B with different brands, **Then** both render their own themes simultaneously in the same tenant. *(CX-BRD-002, S01)*
3. **Given** a theme switch, **Then** registered components that were never told about the brand change appearance through the public token contract. *(CX-BRD-003)*
4. **Given** values set at platform, tenant, organization and site, **Then** the effective token value is deterministic and the trace names the layer that won and why. *(CX-BRD-004, CX-ORG-003)*
5. **Given** the tenant locked the legal footer, **When** the Acme Gulf admin tries to remove or change it in the UI or through the API, **Then** it is refused. *(CX-ORG-004, S04)*

### User Story 2: Theme change with preview, approval and rollback (Priority: P1, R2)

**Acceptance scenarios:**
1. **Given** a brand manager edits tokens, **When** they open preview, **Then** they see the change on the standard shell, sample templates and sample components, in light, dark and high-contrast, at three viewports, in LTR and RTL, with text, contrast and state checks. *(CX-BRD-008, CX-BRD-006)*
2. **Given** a color pair below 4.5:1 for normal text, 3:1 for large text or 3:1 for essential UI cues, or a focus indicator that fails WCAG 2.2, **Then** the editor warns or blocks according to tenant policy; blocking is the default for shell and navigation tokens. *(CX-BRD-009, CX-ACC-005)*
3. **Given** a submitted theme, **Then** it is invisible to employees until approved and published, can be scheduled, and every release is versioned. *(CX-BRD-012)*
4. **Given** a parent theme change, **When** the publisher opens the impact report, **Then** it lists inheriting scopes, pages, audience variants and overridden values, and the publisher can preview a representative child scope and cancel. *(CX-ORG-009, source journey 6.4)*
5. **Given** a published theme, **When** the publisher reverts it, **Then** the previous identity returns, page placements do not change, and employees' personal layouts are kept. *(CX-BRD-012, S08)*

### User Story 3: Brand assets and typography that work everywhere (Priority: P1, R1)

**Acceptance scenarios:**
1. **Given** a logo upload, **Then** format, dimensions and size are validated (PNG, JPEG, sanitized SVG, max 1 MB, as spec 001), and the asset is previewed in light, dark, mobile, desktop and RTL shells. *(CX-BRD-005)*
2. **Given** an Arabic, Urdu or Hindi page, **Then** the theme's font stack for that script is used with a fallback that has no missing glyphs. *(CX-BRD-007, CX-LOC-009)*
3. **Given** locale-specific branding (an Arabic lockup and title), **When** the user switches locale, **Then** the logo and title switch with it. *(CX-LOC-006, R2)*
4. **Given** a white-labeled tenant, **Then** loaders, empty states, access denied and error pages in the shell use the tenant's theme and assets, and no TeamBeats brand appears. *(CX-BRD-011)*

### User Story 4: Scope model, delegation and isolation (Priority: P1, R1 scopes and locks, R2 delegation and sharing)

**Acceptance scenarios:**
1. **Given** a tenant with nested units, **Then** experience configuration can be set at tenant, organization, nested organization, site and audience scope without changing component definitions. *(CX-ORG-001)*
2. **Given** a change in visual inheritance (for example a unit now inherits a different brand), **Then** no data permission changes. *(CX-ORG-002)*
3. **Given** a delegate for Acme Gulf pages and brand, **When** they try to edit Acme Africa or a parent-locked value, **Then** it is refused. *(CX-ORG-005)*
4. **Given** pages, templates and navigation groups owned by Acme Gulf, **Then** Acme Africa users do not see them unless a sharing policy grants it. *(CX-ORG-006)*
5. **Given** a page shared from the group to both subsidiaries, **Then** each user sees it with organization-aware navigation, a visible owner label and the data their own permissions allow; permissions are never merged. *(CX-ORG-007)*
6. **Given** a unit renamed, **Then** its saved layouts, assignments and personalization still apply because they reference immutable IDs. *(CX-ORG-011)*

### User Story 5: Reparenting an organization (Priority: P1, R1)

**Acceptance scenarios:**
1. **Given** a tenant admin moves "Acme Qatar" from Acme Gulf to Acme Group (spec 002), **When** they confirm the move, **Then** they first see the experience impact: brand, locks, templates, navigation and shared pages that will change. *(CX-ORG-010)*
2. **Given** the move, **Then** pages, assets and preference data private to Acme Gulf are not copied into or exposed to Acme Qatar; the unit inherits from its new parent only after confirmation. *(CX-ORG-010)*

### User Story 6: New organization from the starter wizard (Priority: P1, R2)

**Acceptance scenarios:**
1. **Given** a tenant admin creates an organization, **When** they run the starter wizard, **Then** they pick a brand preset, default shell, navigation pattern, language and starter pages from templates, and get an editable, working experience without a developer. *(CX-TMP-011, S12)*
2. **Given** the wizard result, **Then** it is a draft until published, and everything it created is editable and owned by the new organization's admin.

### User Story 7: White-label domain and sign-in (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** two tenants with verified custom domains, **Then** each domain resolves to its own tenant, assets, app title, browser metadata and sign-in presentation. *(CX-BRD-010)*
2. **Given** a domain not yet verified by DNS record, **Then** it is not served.

### User Story 8: Governance reports and portable tokens (Priority: P3, R2)

**Acceptance scenarios:**
1. **Given** the drift report, **Then** it lists non-compliant asset usage, conflicting overrides and inaccessible theme elements with scope, token and remediation. *(CX-BRD-015)*
2. **Given** a token export, **When** it is reimported, **Then** aliases and modes keep their meaning (DTCG 2025.10 format). *(CX-BRD-014)*
3. **Given** a reviewed token extension (an extra brand accent for campaigns), **Then** it applies within its scope only and cannot override another tenant or a protected global behavior. Free CSS is never accepted. *(CX-BRD-013)*

### Edge cases and exception flows
- A brand profile is deleted while assigned: refused until no scope uses it.
- An organization override references a token the tenant later removes: falls back to the tenant value and appears in the drift report.
- A high-contrast mode is not defined by the brand: the platform high-contrast mode is used.
- A font file fails to load: the fallback stack renders; the event is recorded.
- A parent locks a value a child already overrides: the child override is ignored from the parent's release, and the impact report lists it.
- A custom domain certificate expires: the tenant's default subdomain still works and the tenant admin is alerted.

## 5. Functional requirements

- **FR-001**: Each tenant MUST maintain one or more named brand profiles with assets and theme modes, and assign them to scopes without code changes. *(CX-BRD-001)*
- **FR-002**: Organizations MUST be able to override approved brand properties within parent policy and their authorized scope. *(CX-BRD-002)*
- **FR-003**: Color, type, spacing, radii, borders, shadows, motion and component state styles MUST be expressed as semantic tokens; components consume them only through the public contract (spec 032 FR-026). *(CX-BRD-003)*
- **FR-004**: Theme resolution MUST follow platform default, tenant, organization (parent then child), site, then user-allowed choices, with locks winning, and MUST be deterministic. *(CX-BRD-004)*
- **FR-005**: Admins MUST manage logos, lockups, favicon, default imagery and shell assets with format, dimension and size validation. *(CX-BRD-005)*
- **FR-006**: Brands MUST support light, dark and high-contrast modes, and comfortable and compact density, where technically appropriate. *(CX-BRD-006, CX-INT-007 with spec 008)*
- **FR-007**: Typography MUST define families, weight scales and fallbacks per writing system, including Arabic, Urdu and Devanagari. *(CX-BRD-007, CX-LOC-009)*
- **FR-008**: Brand editors MUST preview changes against standard shells, template patterns and sample components with text, contrast and state checks before release. *(CX-BRD-008)*
- **FR-009**: Token editing MUST validate contrast (4.5:1 normal text, 3:1 large text and essential UI cues), focus indicators, minimum text sizes and mandatory affordances, and warn or block per tenant policy. *(CX-BRD-009, CX-ACC-005)*
- **FR-010**: Tenants SHOULD be able to configure verified custom domains, app title, browser metadata and sign-in presentation. *(CX-BRD-010, R2)*
- **FR-011**: Shell loaders, empty states and access errors MUST use the effective theme; no platform brand MAY appear in white-label journeys. *(CX-BRD-011)*
- **FR-012**: Theme changes MUST be draftable, previewable, approvable, schedulable, versioned and reversible, and reverting MUST NOT change page placements or personal layouts. *(CX-BRD-012)*
- **FR-013**: Advanced styling SHOULD be possible only through reviewed token extensions scoped to the tenant or organization. Unrestricted CSS MUST NOT be accepted. *(CX-BRD-013)*
- **FR-014**: Tokens SHOULD export and import in DTCG 2025.10 format or a documented mapping without losing aliases and modes. *(CX-BRD-014)*
- **FR-015**: Admin tools SHOULD report non-compliant assets, conflicting overrides and inaccessible theme elements with scope, token and remediation. *(CX-BRD-015)*
- **FR-016**: Experience configuration MUST support tenant, organization, nested organization, site and audience scopes. *(CX-ORG-001)*
- **FR-017**: Brand inheritance, structural inheritance and security policy inheritance MUST be modeled separately; no visual or structural change MAY grant or remove a data permission. *(CX-ORG-002)*
- **FR-018**: The effective configuration and the source of each value MUST be visible to authorized admins (resolver in spec 032 FR-002). *(CX-ORG-003)*
- **FR-019**: Parent admins MUST be able to lock shell, navigation, legal, accessibility and security settings; children MUST NOT loosen them through UI or API. *(CX-ORG-004)*
- **FR-020**: Tenant admins MUST be able to delegate page, brand, template or navigation administration per scope; delegates MUST NOT edit neighbouring scopes or parent-locked values. *(CX-ORG-005)*
- **FR-021**: Organizations MUST own or inherit pages, templates and navigation groups under explicit sharing policies; cross-organization visibility needs an approved share. *(CX-ORG-006)*
- **FR-022**: Shared experiences SHOULD show organization-aware navigation, owner provenance and brand continuity without merging permissions. *(CX-ORG-007)*
- **FR-023**: Authorized admins MUST preview the effective experience for a chosen organization, audience, language and device, matching the published result for the same configuration. *(CX-ORG-008)*
- **FR-024**: Before a parent change is published, the system MUST list inheriting descendants and affected pages and allow cancel. *(CX-ORG-009)*
- **FR-025**: Moving an org unit MUST show experience impact and require confirmation, and MUST NOT copy or expose pages, assets or preference data the unit should not get. *(CX-ORG-010)*
- **FR-026**: All experience configuration MUST reference tenant and org units by immutable IDs, never names, slugs or URL labels. *(CX-ORG-011)*
- **FR-027**: A starter wizard MUST provision brand preset, shell, navigation pattern, language and starter pages for a new organization as an editable draft. *(CX-TMP-011)*
- **FR-028**: Organizations SHOULD configure locale-specific logos, imagery and titles. *(CX-LOC-006)*

## 6. Business and validation rules
- Locks are always stronger than overrides and user choices. Security and accessibility requirements cannot be unlocked by any tenant setting.
- Default lock set for new tenants: legal footer, security banners, accessibility minimums, sign-in presentation. Tenant admin may lock more.
- Contrast failures on shell and navigation tokens block by default; on page accent tokens they warn by default. Tenants can make both block, never both warn.
- A tenant has at most `experience.max_brands` brand profiles.
- Theme approval is required for tenant-scope themes by default; organization-scope themes publish with one approval step.
- Asset rules from spec 001 apply (PNG, JPEG, sanitized SVG, max 1 MB).
- The logo and colors in spec 001 tenant settings seed the tenant's default brand profile when this feature is first enabled. From then on the theme resolver reads only brand profiles, and the spec 001 tenant settings page links to the brand manager instead of editing the values, so there is one source of truth.

## 7. Tenant and organization scope
- Tenant-owned data: brand profiles, tokens, assets, assignments, locks, delegations, domains, theme releases.
- Organization scoping: assignments and overrides per org unit, subtree and site; locks set by a parent apply to its subtree.
- Cross-tenant denial cases: fetch another tenant's asset URL or token set; serve a custom domain that maps to another tenant; import a token file that names another tenant; preview as an organization of another tenant.
- Sibling, parent and child cases: a sibling cannot read or override another sibling's brand draft; a child cannot change a parent lock; a parent admin without subtree scope cannot edit a child's override.

## 8. Data, ownership and classification

| Entity | System of record | Classification | Retention | Personal data? |
|---|---|---|---|---|
| Brand profile, tokens, modes | Product | Internal | Life of tenant, releases kept 2 years after replacement | No |
| Shell brand assets | Product (object storage, tenant prefix) | Internal | Life of tenant | No |
| Locks, delegations, sharing policies | Product | Internal | Life of tenant | Delegate IDs |
| Custom domain and certificate | Product (platform) | Internal | While configured | No |
| Effective configuration trace | Product | Internal | Same as the release | No |

## 9. Package entitlements

| Capability key | Workplace | Workplace Plus | Enterprise & Government |
|---|---|---|---|
| `experience.max_brands` (limit) | 1 | 5 | unlimited |
| `experience.org_branding` (organization overrides and locks) | off | on | on |
| `experience.custom_domain` | off | on | on |
| `experience.token_extensions` (reviewed token extensions) | off | off | on |

Tenant branding, dark mode, high contrast, contrast validation and theme rollback are in every package. Delegated experience administration uses `org.delegated_admin` (spec 005).

## 10. UX, localization and accessibility
- Theme editor shows every color pair with its contrast ratio and pass or fail in text, not color alone.
- Preview switcher: organization, audience, language, direction, device, mode, density.
- Impact report and trace views are tables usable by keyboard and screen reader.
- All editor strings are localized; previews include pseudo-localized long strings.

## 11. AI behavior and data policy
Not applicable. No AI in this spec.

## 12. Integrations and events
- Publishes: `ThemePublished`, `ThemeRolledBack`, `ExperienceLockChanged`, `BrandAssignmentChanged`.
- Consumes: `OrganizationUnitMoved`, `OrganizationUnitDeactivated` (spec 002), `EntitlementsChanged` (spec 005).
- Asset picker can read the brand asset portal (spec 011) through its contract.

## 13. Audit, telemetry and analytics

| Event | Type | Key fields |
|---|---|---|
| theme.drafted / submitted / approved / published / scheduled / rolled_back | audit | brand, scope, version, actor, diff reference |
| brand.assigned / unassigned | audit | brand, scope, actor |
| experience.lock_changed | audit | scope, setting, old and new value, actor |
| experience.delegation_changed | audit | delegate, scope, areas |
| domain.added / verified / removed | audit | domain, actor |
| theme.font_fallback_used, theme.invalid_token | telemetry | brand, scope, script or token |

## 14. Security and abuse cases
- CSS injection through token values (for example `url()` or `expression`): token values are typed (color, dimension, font family from the approved list, duration) and rejected otherwise.
- SVG logo with script: sanitized or rejected (spec 001).
- Domain takeover: domains need DNS verification; removed domains stop serving at once.
- Brand impersonation across tenants: assets and domains are tenant scoped; sign-in page shows the tenant's verified name.
- Using a theme to hide security banners: banners are locked by default and rendered in a reserved shell area themes cannot hide.

## 15. Non-functional requirements
- Effective theme resolved and cached per scope; theme change visible to users within 60 seconds of publish.
- Theme CSS payload under 30 KB compressed per brand and mode; fonts subset per script and loaded only for the locales in use.
- Brand switch must not cause layout shift above CLS 0.1.

## 16. Success criteria
- **SC-001**: Two organizations in one tenant show different logos, fonts, palettes and navigation on the same template with no source change (S01).
- **SC-002**: A brand update to a parent is published, its impact inspected and rolled back without losing any personal layout (S08).
- **SC-003**: A new organization goes from creation to a published branded home in under one hour using the wizard with no developer (S12).
- **SC-004**: Zero platform brand leakage in white-label journey tests.

## 17. Open questions
- [NEEDS CLARIFICATION: are custom domains needed in shared SaaS, or only in dedicated installations?] Default: both from R2, with DNS verification and managed certificates.
- [NEEDS CLARIFICATION: should organization admins publish their own theme without tenant approval?] Default: one approval step by default; tenant can remove it.

## 18. Traceability

| Requirement ID | Covered by |
|---|---|
| CX-BRD-001 | FR-001, US1 |
| CX-BRD-002 | FR-002, US1 |
| CX-BRD-003 | FR-003, US1 |
| CX-BRD-004 | FR-004, US1 |
| CX-BRD-005 | FR-005, US3 |
| CX-BRD-006 | FR-006, US2 |
| CX-BRD-007 | FR-007, US3 |
| CX-BRD-008 | FR-008, US2 |
| CX-BRD-009 | FR-009, US2 |
| CX-BRD-010 | FR-010, US7 |
| CX-BRD-011 | FR-011, US3 |
| CX-BRD-012 | FR-012, US2 |
| CX-BRD-013 | FR-013, US8 |
| CX-BRD-014 | FR-014, US8 |
| CX-BRD-015 | FR-015, US8 |
| CX-ORG-001 | FR-016, US4 |
| CX-ORG-002 | FR-017, US4 |
| CX-ORG-003 | FR-018, US1 |
| CX-ORG-004 | FR-019, US1 |
| CX-ORG-005 | FR-020, US4 |
| CX-ORG-006 | FR-021, US4 |
| CX-ORG-007 | FR-022, US4 |
| CX-ORG-008 | FR-023 |
| CX-ORG-009 | FR-024, US2 |
| CX-ORG-010 | FR-025, US5 |
| CX-ORG-011 | FR-026, US4 |
| CX-TMP-011 | FR-027, US6 |
| CX-LOC-006 | FR-028, US3 |
| ADM-001 (tenant branding) | FR-001, FR-005; extends spec 001 US2 |

## Review checklist
- [x] No implementation details
- [x] Every FR is testable and traces to a requirement ID
- [x] Cross-tenant and org boundary cases are listed
- [x] Entitlement behavior is defined
- [x] Audit events are defined
- [ ] No open [NEEDS CLARIFICATION] items that change scope
