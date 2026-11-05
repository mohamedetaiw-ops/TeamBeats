# Feature Specification: Page Composition: Layouts, Templates, Component Registry and Page Builder

| Field | Value |
|---|---|
| Spec ID | 032-page-composition |
| Area | experience |
| Status | Proposed |
| Version | 0.1 |
| Owner | Mohamed |
| Approver | Mohamed |
| Target release | R1 (page definition model, effective configuration resolver, responsive layout engine with the 10 layouts, the 12 page templates with visual variants, template preview, locks, distribution and lifecycle, component manifest registry, visual page builder with keyboard composition, autosave, undo and history, previews by device, direction and persona, accessibility and translation checks, error isolation, publish and rollback, performance and isolation baseline), R2 (approval routes, impact reports, fragments, import and export, scheduled releases, feature controls, resizable regions, independent component deployment), R3 (tenant-built components in a sandbox, template recommendations) |
| Last updated | 2026-11-05 |
| Requirement IDs | CX-LAY-001 to CX-LAY-012, CX-TMP-001 to CX-TMP-010, CX-TMP-012, CX-EDT-001 to CX-EDT-015, CX-EXT-001 to CX-EXT-014, CX-GOV-001 to CX-GOV-012, CX-NFR-001 to CX-NFR-012, EXP-001, EXP-008 |
| Use cases | UC-04 (page part) |
| Depends on | 002 (org units), 004 (authorization), 005 (entitlements), 006 (audit), 007 (localization and accessibility), 008 (shell and personalization), 021 (analytics), 024 (process engine, for approval routes), 026 (security controls), 033 (themes and inheritance) |

Source: [Composable Intranet Experience requirements](../../docs/product/composable-experience-requirements.md). IDs from that document carry a `CX-` prefix here.

## 1. Problem and outcome

**Problem:** Every customer wants pages that look and behave like their own intranet: a subsidiary home, a department portal, a campaign landing page, a dashboard of tiles. If pages are coded per customer, the product forks. If the page layer knows about specific widgets, every new module or component forces changes to templates, the builder and the renderer. Business administrators also need to compose pages without a developer, and auditors need to know who changed a page, when and why.

**Outcome:** Pages are versioned, declarative definitions made of a layout, regions and placements of registered components. Any component that ships a valid manifest can be placed in any compatible region without changes to templates, layouts or the builder. Administrators pick from 12 page templates and 10 layouts, compose pages in a visual builder that works fully with keyboard, preview them by device, direction, organization and persona, and publish them through approval with impact reports, version history and one-step rollback. A failing component never takes down the page or the shell.

## 2. Scope

**In scope:**
- Experience configuration model: page, layout, template, region, placement, fragment, component manifest and policy as separately versioned entities, resolved into an effective configuration per request with a provenance trace.
- Responsive layout engine: 12-column adaptive grid, container-aware placements, region constraints, breakpoint ordering, safe areas, fallbacks.
- Layout catalogue L01 to L10 and template catalogue T01 to T12 (section 6 of the source), each with spacious, balanced and compact visual variants.
- Template lifecycle: draft, release, distribution to scopes, deprecation, migration, import and export.
- Component registry: manifests, capability descriptors, configuration schemas, compatibility ranges, trust tiers, lifecycle.
- Visual page builder: picker, placement operations, schema-driven property forms, keyboard composition, previews, autosave, undo and redo, history, fragments, validation.
- Governance of experience artifacts: ownership, draft and publish, approval, scheduling, impact reports, rollback, audit, delegated publishing, scoped feature controls.
- Cross-cutting rendering quality: Core Web Vitals targets, lazy loading, asset budgets, layout stability, failure isolation, browser and device coverage, cache isolation, frontend monitoring.

**Out of scope:**
- What any business component shows or does: news feeds (009), documents (011), services (016), agents (020) and so on. Those modules own their components, data and actions, and register them here through manifests.
- Themes, design tokens, brands and organization inheritance of visual identity (spec 033). This spec consumes resolved tokens.
- The global shell, navigation, personalization by employees and interaction standards (spec 008).
- Content lifecycle of articles and content pages, sites, campaigns (spec 009). A content page of spec 009 is a page definition whose main region holds the page body placement.
- WCAG and localization rules themselves (spec 007). This spec applies them to layouts and the builder.

## 3. Actors and permissions

| Actor | What they can do in this feature | Permission / scope required |
|---|---|---|
| Platform operator | Register platform components and baseline templates and layouts; set platform defaults | Platform role (outside tenant roles) |
| Tenant administrator | Enable components and templates for the tenant, distribute templates, set tenant page policy and feature controls | `component.enable`, `template.distribute`, `experience.feature_control` at tenant scope |
| Organization administrator | Manage pages, templates and global page areas for their scope | `page.edit`, `page.publish`, `template.edit` at org or subtree scope |
| Page editor (new built-in role) | Compose and edit drafts, preview, request publication | `page.edit`, `placement.add`, `placement.remove` at site or org scope |
| Page publisher (new built-in role) | Approve, schedule, publish and roll back pages | `page.publish` (and `page.approve` where a route asks for it) at site or org scope |
| Template designer | Create and release templates, set region locks | `template.edit` at the template's scope |
| Security officer, compliance officer | Read configuration history and publication audit | `experience.history_view` |
| Employee | View pages they are permitted to see; personalize delegated regions (spec 008) | Tenant membership and the page's view permission |

Permission keys follow spec 004 FR-013. They are scoped to tenant, org unit, subtree, site or page and evaluated by the central authorization service. Placement visibility rules are targeting, not access (spec 004 US4).

## 4. User scenarios

### User Story 1: Compose a page from any registered components (Priority: P1, R1)

Sara, a page editor for Acme Gulf, creates a "Plant safety" page from the T04 Overview template with the L06 hero and grid layout. She adds a hero banner, a document list and a KPI tile from the picker into compatible regions, fills their properties in forms generated from each component's manifest, previews the page as a plant operator on a phone in Arabic, fixes a missing image description the checker found, and submits it. Her publisher approves and it goes live.

**Why this priority:** this is the core promise of the experience layer: new pages without developers and without coupling to specific components.

**Independent test:** register two unrelated reference component packs; build the same page from each pack using only configuration; publish both; confirm neither template nor layout nor builder changed.

**Acceptance scenarios:**
1. **Given** a template and layout, **When** the editor creates a page, **Then** a page definition is created from configuration only, with template and layout provenance, and no source change or deployment. *(CX-LAY-001, CX-TMP-006)*
2. **Given** the picker, **Then** it lists only components that are registered, enabled for the tenant, entitled by package, compatible with the selected region and permitted for the editor, each with category, description, preview, required permissions and version. Ineligible components are shown disabled with the reason. *(CX-EDT-002, CX-EXT-003)*
3. **Given** a selected placement, **Then** its property form is generated from the manifest schema with defaults and validation. **Given** a new property added in a new manifest version, **Then** it appears in the form without a builder release. *(CX-EDT-004, CX-EXT-004)*
4. **Given** the same component moved from a narrow side region to a wide main region, **Then** it renders correctly in both, based on its container size, and its data is not changed. *(CX-LAY-003, CX-EDT-012)*
5. **Given** a placement in a region whose policy does not allow its category, **When** the editor or the API tries to add it, **Then** it is refused with a reason naming the region rule. *(CX-LAY-011, CX-EDT-011)*
6. **Given** a draft, **When** an employee opens the page, **Then** they see the last published version only. *(CX-GOV-003)*

### User Story 2: Keyboard and single-pointer composition (Priority: P1, R1)

Omar uses a keyboard and a screen reader. He builds a page and reorders placements without dragging.

**Independent test:** build, reorder and publish a page with keyboard only and with single-pointer only; run the screen reader script of spec 007.

**Acceptance scenarios:**
1. **Given** any placement, **Then** add, remove, move before or after, move to region, duplicate and replace are available from an action menu and keyboard shortcuts, with no drag needed. *(CX-EDT-003, CX-EDT-005)*
2. **Given** a move by keyboard, **Then** focus stays on the moved placement and the new position is announced. *(CX-EDT-005)*
3. **Given** a sequence of placement and styling changes, **When** Omar presses undo repeatedly, **Then** each change is reversed in order, and redo replays them. *(CX-EDT-009)*

### User Story 3: Responsive, safe layouts (Priority: P1, R1)

**Independent test:** render every reference template at 320, 375, 768, 1024, 1440 and 1920 CSS pixels in LTR and RTL; run the layout validator on a set of deliberately broken layouts.

**Acceptance scenarios:**
1. **Given** any of the 10 layouts, **When** rendered at each reference width, **Then** there is no clipping, overlap or horizontal page scroll, and primary actions are not covered by the shell, sticky controls or device safe areas. *(CX-LAY-002, CX-LAY-010, CX-NFR-008)*
2. **Given** a breakpoint-specific order, **When** previewed, **Then** visual order, DOM order, keyboard order and screen reader order match. An order that would break this is refused. *(CX-LAY-007, CX-LAY-008)*
3. **Given** a layout setting the current breakpoint does not support (for example three columns at 320 px), **Then** the documented fallback applies and the page is never blank or clipped. *(CX-LAY-012)*
4. **Given** a layout with a zero-size region, an unsupported span, overflow, nesting deeper than allowed, or two placements claiming the same cell, **When** the editor saves or publishes, **Then** it is blocked and each problem points to the offending placement. *(CX-LAY-009, CX-EDT-011)*
5. **Given** a full-bleed, contained, fixed-width or fluid section, **Then** each works with the same registered component. *(CX-LAY-006)*

### User Story 4: A failing component does not break the page (Priority: P1, R1)

**Independent test:** fault injection: one component throws during render, one times out, one manifest is removed from the registry.

**Acceptance scenarios:**
1. **Given** a component that throws, **Then** only its placement shows the shared error state with a retry where safe, the shell and other placements keep working, and a redacted error with release version and correlation ID is recorded. *(CX-EXT-008, CX-NFR-005, CX-NFR-009)*
2. **Given** a component that is unregistered or discontinued, **Then** its placements render a "no longer available" state to viewers, editors see a replacement prompt, and publishing new pages with it is blocked. *(CX-NFR-012)*
3. **Given** invalid theme tokens from spec 033, **Then** the renderer falls back to the last valid resolved theme and records the issue. *(CX-NFR-012)*

### User Story 5: Templates as governed, distributable assets (Priority: P1, R1)

**Acceptance scenarios:**
1. **Given** the 12 templates, **Then** an admin can create a functional page from each without code, and switch between at least three visual variants (spacious, balanced, compact) that change presentation only. *(CX-TMP-002, CX-TMP-004)*
2. **Given** the template chooser, **Then** it shows desktop, tablet and mobile previews with a text description of the structure, and the admin can compare candidates before applying. *(CX-TMP-005)*
3. **Given** a page created from template version 1 and customized, **When** the template releases version 2, **Then** the page is not changed; the owner sees that a newer version exists and can apply it with a diff preview. *(CX-TMP-006, CX-TMP-009)*
4. **Given** a template distributed to Acme Gulf only, **Then** Acme Gulf editors see it in the chooser, and an Acme Africa editor can neither see nor instantiate it through UI or API. *(CX-TMP-008)*
5. **Given** a region marked protected in the template, **Then** page editors without template rights and employees cannot remove it, but can edit placements in their authorized slots. *(CX-TMP-007)*
6. **Given** a deprecated template version, **Then** pages that use it keep working; new pages cannot use it; owners get migration guidance. *(CX-TMP-009)*
7. **Given** a change of layout on an existing page, **Then** placements are kept in compatible regions and any placement without a compatible region is listed for the editor to place before saving. *(CX-TMP-003)*

### User Story 6: Publish with approval, impact report and rollback (Priority: P1, R1 publish and rollback, R2 approval and impact)

**Acceptance scenarios:**
1. **Given** a site or scope with an approval route, **When** an editor submits, **Then** the approver gets a task (spec 016 inbox, route on spec 024) with a visual and structural diff, and can approve or return with a comment. An editor never approves their own change. *(CX-GOV-004, CX-EDT-010)*
2. **Given** a change to a shared layout, global page area or template, **Then** before publishing the publisher sees affected organizations, audiences, pages and the number of personal layouts touched, and can cancel. *(CX-GOV-007, CX-EDT-013)*
3. **Given** a published page, **When** a publisher rolls back, **Then** the exact earlier release is restored as a new release, end-user personal layouts are kept, and the action is audited. *(CX-GOV-005, CX-GOV-010)*
4. **Given** a scheduled release, **Then** it goes live and expires at the tenant-local times set. *(CX-GOV-006)*
5. **Given** a delegated editor scoped to Acme Gulf, **When** they call the publish API for an Acme Africa page, **Then** it is denied with not-found semantics and audited. *(CX-GOV-011)*
6. **Given** two editors changing the same page, **Then** the second save gets a conflict warning with both versions kept and a merge or overwrite choice; nothing is lost silently. *(CX-GOV-008, CX-EDT-008)*

### User Story 7: Register and upgrade a component package (Priority: P1, R1 registry, R2 independent deployment)

**Acceptance scenarios:**
1. **Given** a new package with a valid manifest, **When** it is registered and passes validation (schema, compatibility, accessibility metadata, trust tier), **Then** it appears in the picker for tenants that enable it. *(CX-EXT-001)*
2. **Given** an upgrade outside the declared compatibility range, **Then** it is blocked with the reason. **Given** a supported upgrade, **Then** the manifest migration hook converts existing placement configuration and pages that do not use the component are untouched. *(CX-EXT-009, CX-EXT-012)*
3. **Given** a component asks for a context value it did not declare (for example the user's email), **Then** the runtime does not supply it. *(CX-EXT-007)*
4. **Given** a page layout change, **Then** the component's service endpoints and permissions are unchanged. *(CX-EXT-006)*
5. **Given** a people filter placement that publishes `filterChanged` and a directory list placement that subscribes to it, **When** the filter is removed from the page, **Then** the list still renders unfiltered; **Given** a placement that emits an event it did not declare, **Then** no subscriber receives it. *(FR-067, FR-068)*

### User Story 8: Cross-tenant isolation through the experience layer (Priority: P1, R1)

**Acceptance scenarios:**
1. **Given** a user who switches from tenant A to tenant B, **Then** no cached route, page definition, theme asset or component configuration from A is shown. *(CX-NFR-011)*
2. **Given** a theme asset URL or page ID from tenant A, **When** requested in tenant B's session, **Then** not found. *(CX-NFR-011)*
3. **Given** a configuration field that contains script, an event handler attribute, a `javascript:` URL or an embed from a host not on the allowlist, **Then** save is refused. *(CX-EXT-014, CX-NFR-010)*

### Edge cases and exception flows
- A page references a component the tenant's package no longer includes: viewers see the "not available" state; the API refuses new placements of it (constitution XXI).
- A region's maximum is lowered in a new template version while pages exceed it: existing pages keep working; editors must resolve before the next publish.
- A singleton component (one per page) is duplicated: blocked with a message.
- Autosave fails because the network dropped: the builder shows "not saved", keeps edits locally, and retries; confirmed saves are never lost. *(CX-EDT-008, CX-NFR-006)*
- A placement's visibility rule matches nobody: publish warns, and the region falls back to its default (spec 008 FR-030).
- An approver is on leave: the delegate from spec 016 gets the task.
- A scheduled release fails validation at go-live (for example a component was retired meanwhile): it does not go live, the publisher is notified, and the current release stays.

## 5. Functional requirements

Model and resolution
- **FR-001**: Pages MUST be stored as versioned declarative definitions of layout, regions and placements. Shell, page, template, layout, theme, preference, component manifest and policy MUST be separately versioned entities. *(CX-LAY-001)*
- **FR-002**: The system MUST compute an effective configuration per request by resolving platform, tenant, organization (parent then child), site or page, audience and permitted user layers, separately for each policy domain (visual tokens, structure, visibility, content eligibility, authorization), with locked values always winning. It MUST store published snapshots and expose a per-value provenance trace to authorized admins. *(source section 2.3, CX-ORG-003 with spec 033)*
- **FR-003**: Page and layout definitions MUST NOT contain business data, secrets or credentials. *(source section 2.2)*

Layout engine
- **FR-004**: The layout engine MUST provide a 12-column adaptive grid with configurable spans, gutters and spacing that passes reference tests at 320, 375, 768, 1024, 1440 and 1920 CSS pixels. *(CX-LAY-002)*
- **FR-005**: Placements MUST size from their container, not from the viewport. *(CX-LAY-003)*
- **FR-006**: The first release with the layout engine MUST ship the 10 layouts L01 to L10 of the source, usable independently of templates. *(CX-LAY-004, CX-TMP-003)*
- **FR-007**: Authorized editors SHOULD be able to resize regions within declared constraints; resizing MUST persist and MUST NOT create overlap. *(CX-LAY-005, R2)*
- **FR-008**: Sections MUST support contained, full-bleed, fixed-width and fluid modes. *(CX-LAY-006)*
- **FR-009**: Admins MUST be able to set breakpoint-specific order and visibility per region, and the engine MUST keep DOM, reading and keyboard order meaningful; changes that break logical order MUST be refused. *(CX-LAY-007, CX-LAY-008)*
- **FR-010**: The validator MUST block unsupported spans, zero-size placements, overflow, nesting beyond the limit, conflicting placements, duplicate singletons and forbidden regions on save and on publish, pointing to the offending placement. *(CX-LAY-009, CX-EDT-011)*
- **FR-011**: The engine MUST account for the shell, page padding, sticky controls and device safe areas. *(CX-LAY-010)*
- **FR-012**: Regions MUST be able to declare allowed component categories or capabilities, minimum and maximum placements, sizes and personalization rights, without naming concrete components. *(CX-LAY-011)*
- **FR-013**: Each layout MUST define an accessible fallback for settings a breakpoint cannot honour. *(CX-LAY-012)*

Templates
- **FR-014**: Templates MUST define structure, placement rules, theme references and optional placeholders, and MUST NOT require any specific component. An architecture test MUST fail if a template, layout or the builder references a concrete component ID. *(CX-TMP-001, CX-EXT-002)*
- **FR-015**: The template catalogue MUST include the 12 templates T01 to T12 of the source, each usable with zero or more placements. *(CX-TMP-002)*
- **FR-016**: Every template MUST support at least the spacious, balanced and compact visual variants, plus light, dark and high-contrast modes where the tenant's theme allows, through tokens only. *(CX-TMP-004)*
- **FR-017**: The template chooser MUST show desktop, tablet and mobile previews with a text description of the structure, and allow compare before apply. *(CX-TMP-005)*
- **FR-018**: Instantiating a template MUST copy its configuration into an editable page that records template ID and version; later template versions MUST NOT change existing pages unless their owner applies them. *(CX-TMP-006)*
- **FR-019**: Template designers MUST be able to mark regions mandatory, optional, protected or removable by named delegated roles. Protection MUST be enforced by the API. *(CX-TMP-007)*
- **FR-020**: Platform and tenant admins MUST be able to distribute templates to selected organizations, subtrees or sites; other scopes MUST NOT see or instantiate them. *(CX-TMP-008)*
- **FR-021**: Templates MUST have draft, released, deprecated and retired states with versioned releases, migration guidance and controlled replacement; pages on a deprecated version MUST keep working. *(CX-TMP-009)*
- **FR-022**: Authorized users SHOULD be able to export and import templates in a documented JSON format that carries structure and policies and never secrets, tenant IDs or personal data. Import MUST validate as a new draft. *(CX-TMP-010, R2)*
- **FR-023**: The system COULD recommend templates from site purpose, viewport and content density. Recommendations MUST be ignorable and MUST NEVER auto-publish or change permissions. *(CX-TMP-012, R3)*

Component registry and contract
- **FR-024**: Components MUST be registered through a versioned manifest independent of page and template source, carrying the fields of source section 7.2: identity and version, display metadata, configuration schema, placement capabilities, responsive behavior, context dependencies, theme contract, localization and accessibility metadata, error and lifecycle modes, permission and entitlement declaration, trust tier and telemetry hooks, plus the events it publishes and subscribes to, the actions it accepts, and its data contract (the module endpoints it reads). *(CX-EXT-001, CX-EXT-003)*
- **FR-025**: Placement properties MUST come from the manifest's typed schema with defaults, validation and migration hooks; the builder MUST render property inputs from the schema without component-specific builder code. *(CX-EXT-004, CX-EDT-004)*
- **FR-026**: Components MUST style themselves only through the public semantic token contract of spec 033 and MUST NOT rely on private brand assumptions. *(CX-EXT-005)*
- **FR-027**: Data retrieval and action authorization MUST stay inside the owning module's service contracts. Moving, hiding or reconfiguring a placement MUST NOT change any data, endpoint or permission. *(CX-EXT-006, CX-EDT-012)*
- **FR-028**: The runtime MUST pass each component only the context values its manifest declares from an approved list: tenant ID, org unit ID, site ID, page ID, locale, direction, theme mode, density, device class and allowed user attributes. *(CX-EXT-007)*
- **FR-029**: Each placement MUST render inside an error boundary so that a failure affects only that placement. *(CX-EXT-008, CX-NFR-005)*
- **FR-030**: Manifests MUST declare platform compatibility ranges, deprecation and migration; out-of-range upgrades MUST be blocked with a reason; supported upgrades MUST migrate configuration. *(CX-EXT-009)*
- **FR-031**: Components MUST carry a trust tier. Platform components (built and reviewed by TeamBeats) run in the main page. Tenant-built or third-party components (R3) MUST run in a sandboxed frame with no access to the session, the DOM of the page or other tenants, network access limited to declared hosts, and least privilege. *(CX-EXT-010, ADR-0012)*
- **FR-032**: Components SHOULD emit standardized lifecycle and interaction events through the shared event contract of spec 021, with no analytics vendor coupling. *(CX-EXT-011)*
- **FR-033**: Approved component packages SHOULD be deployable and updatable without rebuilding page definitions; pages that do not use an upgraded component MUST be untouched. *(CX-EXT-012, R2)*
- **FR-034**: The platform MUST keep placement configuration, transient runtime state and user preference state apart. Resetting preferences MUST NEVER delete business data. *(CX-EXT-013)*
- **FR-035**: No configuration field, template field, style value or embed MUST allow executable content. Rich text and embeds MUST be sanitized server side; embeds MUST come from the tenant allowlist in sandboxed frames. *(CX-EXT-014, CX-NFR-010)*

Visual builder
- **FR-036**: Authorized users MUST create and change pages in a visual builder without editing markup or source code. *(CX-EDT-001)*
- **FR-037**: The picker MUST show eligible components with category, description, preview, required permissions and version, and disable ineligible ones with an understandable reason. *(CX-EDT-002)*
- **FR-038**: The builder MUST support add, remove, move, duplicate and replace placements within allowed regions, keeping valid settings, each with undo. *(CX-EDT-003)*
- **FR-039**: Every placement operation MUST have a non-drag alternative (move before or after, region picker, action menu) and the builder MUST be fully keyboard operable. *(CX-EDT-005, CX-ACC-002, CX-ACC-003)*
- **FR-040**: The builder MUST preview the current draft at desktop, tablet and mobile sizes in LTR and RTL, with the same renderer as the published page. *(CX-EDT-006)*
- **FR-041**: The builder MUST preview as an organization, audience, language and device using approved test personas; preview MUST NOT sign in as a real user or show data the editor is not permitted to see, and MUST report which permissions the persona assumes. *(CX-EDT-007, CX-ORG-008 with spec 033)*
- **FR-042**: Drafts MUST autosave with a visible state, and concurrent edits MUST raise an optimistic concurrency warning with both versions kept. *(CX-EDT-008, CX-GOV-008)*
- **FR-043**: The builder MUST support undo and redo within a session and a revision history across sessions for structural and visual changes. *(CX-EDT-009)*
- **FR-044**: Authorized admins MUST be able to update global page areas and shared layouts with a list of affected pages before publishing. *(CX-EDT-013)*
- **FR-045**: Editors SHOULD be able to save sections or region patterns as governed fragments with owner, scope and version, and apply them to new pages without copying component code. *(CX-EDT-014, R2)*
- **FR-046**: The builder MUST show accessibility findings per page, region and placement (focus order, missing names and descriptions, contrast from the resolved theme) with remediation guidance, and block publish on blocking findings. *(CX-ACC-011, CX-ACC-012 with spec 007)*
- **FR-047**: The builder SHOULD show translation status for page labels, placement text and navigation labels per enabled locale, and block publish when the tenant policy requires a locale that is incomplete. *(CX-LOC-007 with spec 007)*

Governance
- **FR-048**: Built-in roles MUST cover platform operator, tenant administrator, organization administrator, brand manager (design administrator, spec 033), page editor, page publisher and employee, with permission tests for create, edit, preview, publish and roll back per role. *(CX-GOV-001)*
- **FR-049**: Every template, layout, fragment, page and component enablement MUST record an owner and an effective scope; an inventory report MUST filter by tenant, organization and owner. *(CX-GOV-002)*
- **FR-050**: No structural change MUST alter the live page until publish is authorized. Published versions MUST be immutable. *(CX-GOV-003, CX-EDT-010)*
- **FR-051**: Approval MUST be configurable for shared templates, global page areas, public pages and any page scope the tenant marks, running on spec 024 with a change diff for the approver. *(CX-GOV-004)*
- **FR-052**: Every artifact MUST keep immutable releases; rollback MUST restore the exact released configuration as a new release, without data loss and without erasing personal layouts, within the RTO in section 15. *(CX-GOV-005, CX-GOV-010)*
- **FR-053**: Authorized admins SHOULD schedule publication and expiry at tenant-local times. *(CX-GOV-006)*
- **FR-054**: Publishing a shared artifact MUST compute affected organizations, audiences, pages and personal layouts and show them before confirmation. *(CX-GOV-007)*
- **FR-055**: Audit MUST record actor, time, scope, before and after difference, approvals, reason when policy requires one, and rollback reference for every publish, rollback, distribution, enablement and lock change, so that an export reconstructs the change. *(CX-EDT-015, CX-GOV-009)*
- **FR-056**: Delegated editors MUST publish only within granted scopes; the API MUST enforce this independently of the UI. *(CX-GOV-011)*
- **FR-057**: Selected experience capabilities SHOULD be switchable per tenant, organization or audience through governed experience feature controls. A control MUST NOT enable anything the tenant's package does not include (spec 005). *(CX-GOV-012)*

Performance, quality and resilience
- **FR-058**: Pages MUST target p75 LCP of 2.5 s or less, INP of 200 ms or less and CLS of 0.1 or less, by mobile and desktop segments, on agreed reference profiles. *(CX-NFR-001)*
- **FR-059**: Out-of-view or low-priority placements MUST defer heavy resources while keeping accessible placeholders. *(CX-NFR-002)*
- **FR-060**: Images, icons, fonts and scripts MUST use optimized delivery and caching within published performance budgets checked in CI. *(CX-NFR-003)*
- **FR-061**: Late content MUST reserve space or transition without disruptive layout shift. *(CX-NFR-004)*
- **FR-062**: Loss of connectivity SHOULD show a clear offline or degraded state and keep unsaved builder edits locally until reconnection, within the documented offline scope. *(CX-NFR-006)*
- **FR-063**: The experience MUST support the current and previous major versions of Chrome, Edge, Safari and Firefox, and mouse, keyboard, touch and assistive technology input on representative hardware. *(CX-NFR-007, CX-NFR-008)*
- **FR-064**: Frontend performance and failure events MUST be correlated with tenant, org unit, release and a non-identifying page ID, without user content. *(CX-NFR-009, CX-MET-006 with spec 021)*
- **FR-065**: Cache keys and offline stores MUST include tenant, org scope and the security context where content depends on them. *(CX-NFR-011)*
- **FR-066**: The renderer MUST recover from missing manifests, discontinued components, invalid tokens and network failures with a safe fallback and a usable shell. *(CX-NFR-012)*

Component interaction
- **FR-067**: Placements on a page MUST communicate only through a page-scoped event channel, using events and actions declared in their manifests with typed payload schemas. The runtime MUST drop and log undeclared events and payloads that fail validation. A payload MUST NOT carry data the receiving placement could not read itself under the same session. *(constitution XXX)*
- **FR-068**: A component MUST NOT import, call or read the state of another component. Removing, hiding or failing any placement MUST leave the others working, with subscribers falling back to their unlinked state. An architecture test MUST fail on imports between component packages. *(constitution XXX)*

## 6. Business and validation rules
- Non-negotiable rule from the source: templates and layouts MAY reference capabilities, regions, placement constraints and metadata, and MUST NOT depend on the existence, identity, internals or code of a particular component.
- Contract tests use two deliberately unrelated reference component packs; every template must pass the swap test with both (S02 of the source).
- Default limits: 40 placements per page, nesting depth 3 (section inside region inside layout), 12 regions per layout. Tenant can lower, not raise beyond platform maximums.
- An editor never approves their own change. Separation of duties applies when the route requires it.
- Rollback target RTO for a broken critical page: 5 minutes from the publisher's action.
- Platform components only until R3. Tenant-built components need ADR-0012's sandbox and a security review before any tenant can enable them.

## 7. Tenant and organization scope
- Tenant-owned data: pages, page versions, tenant templates and fragments, component enablement, feature controls, publication history.
- Platform-owned data: baseline templates and layouts, component manifests of platform components. They are read-only to tenants and copied by reference, never edited per tenant.
- Organization scoping: pages, templates and fragments belong to a tenant, org unit, subtree or site scope. Visibility and edit rights follow spec 004.
- Cross-tenant denial cases: read or publish a page by ID from another tenant; instantiate a template distributed in another tenant; fetch a theme asset or component configuration with another tenant's ID; reuse a cached route after tenant switch; import a template export that carries another tenant's IDs (IDs are stripped and rebound).
- Sibling, parent and child cases: a sibling org admin cannot edit or publish; a parent admin does not edit child pages without subtree scope; a child admin cannot remove a region protected by the parent template (S04).

## 8. Data, ownership and classification

| Entity | System of record | Classification | Retention | Personal data? |
|---|---|---|---|---|
| Page definition and versions | Product | Internal | Life of page plus 2 years for released versions | Editor IDs |
| Template, layout, fragment | Product (platform or tenant) | Internal | Life of tenant | No |
| Component manifest | Product (platform registry) | Internal | While any page references a version | No |
| Effective configuration snapshot | Product | Internal | Same as the release it explains | No |
| Builder autosave draft | Product | Internal | Until published or 90 days idle | Editor ID |
| Frontend error event | Product (telemetry) | Internal, redacted | 30 days | No (pseudonymous session key) |

## 9. Package entitlements

| Capability key | Workplace | Workplace Plus | Enterprise & Government |
|---|---|---|---|
| `experience.page_builder` (visual builder, all 12 templates and 10 layouts) | on | on | on |
| `experience.fragments` (reusable governed fragments, template import and export) | off | on | on |
| `experience.approval_routes` (multi-step page approval and impact reports) | off (one-step publish with rollback) | on | on |
| `experience.max_custom_templates` (limit) | 5 | 50 | unlimited |
| `experience.feature_controls` (scoped experience switches) | off | on | on |
| `experience.custom_components` (sandboxed tenant components, R3) | off | off | on |

The layout engine, accessibility, localization, error isolation, rollback and audit are in every package and have no key (constitution XXIV). `experience.page_builder` replaces the former `content.page_builder` key of spec 009.

## 10. UX, localization and accessibility
- States: empty page, draft, in review, scheduled, published, deprecated template, component not available, component error, offline, no permission.
- Builder is WCAG 2.2 AA itself (spec 007): every drag has a non-drag path, focus is restored after panels and dialogs, live regions announce moves and validation.
- Previews cover LTR and RTL; layouts use logical properties only (CX-LOC-010 in spec 007).
- Validation messages name the placement and region and say how to fix.

## 11. AI behavior and data policy
No AI in R1 and R2. The R3 template recommendations (FR-023) use only page metadata, never page content or user data, and go through the AI Gateway if a model is used.

## 12. Integrations and events
- Publishes: `PagePublished`, `PageRolledBack`, `TemplateReleased`, `TemplateDeprecated`, `ComponentRegistered`, `ComponentRetired`.
- Consumes: `EntitlementsChanged` (spec 005), `OrganizationUnitMoved` (spec 002, for scope recalculation and impact checks), `ThemePublished` (spec 033), approval results (spec 024).

## 13. Audit, telemetry and analytics

| Event | Type | Key fields |
|---|---|---|
| page.created / submitted / approved / returned / published / scheduled / rolled_back | audit | page, version, scope, actor, diff reference, route step, reason |
| template.released / distributed / deprecated / imported | audit | template, version, scopes, actor |
| region.lock_changed | audit | template or page, region, old and new lock, actor |
| component.registered / enabled / disabled / upgraded / retired | audit | component, version, tenant, actor |
| experience.feature_control_changed | audit | control, scope, old and new value |
| placement.render_failed | telemetry | component, version, release, page key, correlation ID |
| page.web_vitals | telemetry | page key, template, layout, device class, LCP, INP, CLS |
| page.viewed, placement.interacted | analytics | page key, template, layout, component category (spec 021 schema) |

## 14. Security and abuse cases
- Script injection through properties, styles, URLs or embeds: schema types forbid raw HTML, URLs are scheme-checked, styles are token references only, content security policy blocks inline script (FR-035).
- A compromised editor publishes a defacing change to the tenant home: approval routes for global areas, rate limit on publishes, one-step rollback, audit.
- Persona preview used to see data as someone else: personas are synthetic, data placements in preview run with the editor's own permissions (FR-041).
- A malicious component manifest declares wide context: only approved context keys exist; platform review of manifests; R3 tenant components are sandboxed.
- Placement visibility used to hide a component and call its API anyway: hidden placements never grant or remove access; the module's API authorizes every call.

## 15. Non-functional requirements
- Effective configuration resolution: p95 under 30 ms server side from cache, under 150 ms cold.
- Page render server time p95 under 800 ms for a page with 20 placements (matches spec 009).
- Publish of a shared template to 1,000 inheriting pages completes impact calculation within 60 seconds.
- Rollback completes and serves the restored version within 5 minutes (RTO).
- Real-user monitoring and synthetic tests per tenant, org and release (S10).

## 16. Success criteria
- **SC-001**: A trained business admin creates, previews and publishes a compliant page from any of the 12 templates without developer help in under 20 minutes.
- **SC-002**: The swap test passes for every template with two unrelated component packs (S02).
- **SC-003**: Fault injection on any one component leaves navigation and all other placements usable (S07).
- **SC-004**: Page construction and reordering pass keyboard-only, single-pointer and screen reader runs (S09).
- **SC-005**: p75 LCP, INP and CLS targets met on reference profiles for every reference template (S10).

## 17. Open questions
- RESOLVED 2026-11-05 (Mohamed): the visual page builder and all 12 templates ship in R1 pilot.
- [NEEDS CLARIFICATION: will tenants be allowed to upload their own component code?] Default: not before R3, Enterprise & Government only, sandboxed per ADR-0012.

## 18. Traceability

| Requirement ID | Covered by |
|---|---|
| CX-LAY-001 | FR-001, US1 |
| CX-LAY-002 | FR-004, US3 |
| CX-LAY-003 | FR-005, US1 |
| CX-LAY-004 | FR-006 |
| CX-LAY-005 | FR-007 |
| CX-LAY-006 | FR-008, US3 |
| CX-LAY-007, CX-LAY-008 | FR-009, US3 |
| CX-LAY-009 | FR-010, US3 |
| CX-LAY-010 | FR-011, US3 |
| CX-LAY-011 | FR-012, US1 |
| CX-LAY-012 | FR-013, US3 |
| CX-TMP-001 | FR-014 |
| CX-TMP-002 | FR-015, US5 |
| CX-TMP-003 | FR-006, US5 |
| CX-TMP-004 | FR-016, US5 |
| CX-TMP-005 | FR-017, US5 |
| CX-TMP-006 | FR-018, US1, US5 |
| CX-TMP-007 | FR-019, US5 |
| CX-TMP-008 | FR-020, US5 |
| CX-TMP-009 | FR-021, US5 |
| CX-TMP-010 | FR-022 |
| CX-TMP-012 | FR-023 |
| CX-EDT-001 | FR-036 |
| CX-EDT-002 | FR-037, US1 |
| CX-EDT-003 | FR-038, US2 |
| CX-EDT-004 | FR-025, US1 |
| CX-EDT-005 | FR-039, US2 |
| CX-EDT-006 | FR-040 |
| CX-EDT-007 | FR-041 |
| CX-EDT-008 | FR-042, US6 |
| CX-EDT-009 | FR-043, US2 |
| CX-EDT-010 | FR-050, US6 |
| CX-EDT-011 | FR-010, US1, US3 |
| CX-EDT-012 | FR-027, US1 |
| CX-EDT-013 | FR-044, US6 |
| CX-EDT-014 | FR-045 |
| CX-EDT-015 | FR-055 |
| CX-EXT-001 | FR-024, US7 |
| CX-EXT-002 | FR-014 |
| CX-EXT-003 | FR-024, US1 |
| CX-EXT-004 | FR-025, US1 |
| CX-EXT-005 | FR-026 |
| CX-EXT-006 | FR-027, US7 |
| CX-EXT-007 | FR-028, US7 |
| CX-EXT-008 | FR-029, US4 |
| CX-EXT-009 | FR-030, US7 |
| CX-EXT-010 | FR-031 |
| CX-EXT-011 | FR-032 |
| CX-EXT-012 | FR-033, US7 |
| CX-EXT-013 | FR-034 |
| CX-EXT-014 | FR-035, US8 |
| CX-GOV-001 | FR-048 |
| CX-GOV-002 | FR-049 |
| CX-GOV-003 | FR-050, US1 |
| CX-GOV-004 | FR-051, US6 |
| CX-GOV-005 | FR-052, US6 |
| CX-GOV-006 | FR-053, US6 |
| CX-GOV-007 | FR-054, US6 |
| CX-GOV-008 | FR-042, US6 |
| CX-GOV-009 | FR-055 |
| CX-GOV-010 | FR-052, US6 |
| CX-GOV-011 | FR-056, US6 |
| CX-GOV-012 | FR-057 |
| CX-NFR-001 | FR-058 |
| CX-NFR-002 | FR-059 |
| CX-NFR-003 | FR-060 |
| CX-NFR-004 | FR-061 |
| CX-NFR-005 | FR-029, US4 |
| CX-NFR-006 | FR-062 |
| CX-NFR-007 | FR-063 |
| CX-NFR-008 | FR-063, US3 |
| CX-NFR-009 | FR-064, US4 |
| CX-NFR-010 | FR-035, US8 |
| CX-NFR-011 | FR-065, US8 |
| CX-NFR-012 | FR-066, US4 |
| EXP-001 (configurable pages) | FR-001, FR-036 |
| EXP-008 (responsive) | FR-004, FR-063 |
| Constitution XXX (component interaction) | FR-067, FR-068, US7 |

## Review checklist
- [x] No implementation details beyond what ADR-0012 records
- [x] Every FR is testable and traces to a requirement ID
- [x] Cross-tenant and org boundary cases are listed
- [x] Entitlement behavior is defined
- [x] Audit events are defined
- [ ] No open [NEEDS CLARIFICATION] items that change scope
