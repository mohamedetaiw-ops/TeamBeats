# Feature Specification: TeamBeats Design Language System

| Field | Value |
|---|---|
| Spec ID | 036-design-language-system |
| Area | experience |
| Status | Approved |
| Version | 1.0 |
| Owner | Mohamed |
| Approver | Mohamed |
| Target release | R1 (design guidelines and technical guidelines, three selectable designs Beats, Atlas and Pulse, design picker in the brand manager, component and template library with the living gallery, design consistency gate, design test suite with accessibility, responsive, target size and visual regression checks, enforcement in the constitution and specs), R2 (design review workflow in the theme lifecycle of spec 033, cross-browser automation, quarterly design audit report), R3 (tenant design extensions through reviewed token extensions) |
| Last updated | 2026-11-09 |
| Approved | 2026-11-09 by Mohamed (standing approval to proceed) |
| Requirement IDs | DLS-001 to DLS-024 (new); CX-BRD-003, CX-BRD-006, CX-BRD-009 (with spec 033); CX-ACC-001 to CX-ACC-005 (with spec 007) |
| Use cases | UC-01 (tenant setup, design choice), every employee and admin journey (look and feel) |
| Depends on | 007 (localization and accessibility), 008 (shell), 032 (templates and components), 033 (tokens, brands, resolver, brand manager), ADR-0014, ADR-0019, ADR-0020 |
| Used by | Every spec with a user interface: its UX section references this system instead of describing visuals from scratch |

Guidelines: [docs/design-system](../../docs/design-system/README.md).

## 1. Problem and outcome

**Problem:** TeamBeats grew screen by screen. Without one written design language, each feature chose its own spacing, wording, icons and states, contrast was checked only in places, and customers could change colors but not choose a look that fits them (a ministry wants something formal, a retail group something bold). Designers had no shared rules to test against and developers had no gate to stop drift.

**Outcome:** One design language system, modeled on national design systems such as the UAE Design System, with design guidelines (typography, colour, icons, imagery, content, layout, actions, accessibility), technical guidelines (frontend practice, security, performance, consistency), ready components and templates, and a testing strategy for designers and developers. TeamBeats ships three complete designs that tenant administrators choose from; the current v2 look (Beats) is the default. The build enforces the system, and the constitution makes it binding.

## 2. Scope

**In scope:**
- Design and technical guidelines in `docs/design-system`.
- Three designs: Beats (default), Atlas, Pulse, each a complete accessible token set plus a navigation style.
- Design choice per brand in the brand manager (spec 033), inherited and lockable like any token.
- Shared component library and gallery: buttons, text boxes, text areas, selects, cards, badges, avatars, alerts, images, tabs, icons, states, skeletons, confirmations, selection bar, messages.
- Page template patterns mapped to the spec 032 templates.
- Automated consistency gate and design test suite; manual design testing process.
- Enforcement: constitution principle XXXI, spec template, CLAUDE.md, references in specs 007, 008 and 033.

**Out of scope:**
- Customer-specific designs written in code. Customers adjust a design through tokens (spec 033).
- Native mobile app design (spec 030 adopts these guidelines when it starts).
- Marketing website design.

## 3. Actors and permissions

| Actor | What they can do in this feature | Permission / scope required |
|---|---|---|
| Tenant administrator, brand manager | Choose the design for the tenant brand, adjust tokens on top, publish | `theme.edit`, `theme.publish` (spec 033) |
| Organization administrator | Choose a design for their subtree when organization branding is on and the design is not locked | `theme.edit`, `theme.publish` at org scope, `experience.org_branding` |
| Employee | Sees the effective design; chooses mode, density, text size and motion (spec 008) | Tenant membership |
| Designers, UX writers, researchers | Apply the guidelines and run design tests | Not a product role |
| Developers | Build with the components and pass the gates | Not a product role |

## 4. User scenarios

### User Story 1: Choose a design for the workspace (Priority: P1, R1)

A ministry's tenant administrator opens Administration, Brand, sees three design cards with live miniatures, chooses Atlas, checks the preview and contrast table, and saves. Every employee then sees the formal green design with top navigation, in Arabic and English, light and dark.

**Independent test:** set `design.preset` to each design on a test tenant through the brand API; the home and gallery show the design's attributes, fonts and accent, with no serious accessibility violation.

**Acceptance scenarios:**
1. **Given** the brand manager, **When** it opens, **Then** the three designs show with name, description, navigation style and a miniature drawn from the design's own resolved tokens, and the current design is marked. *(DLS-001)*
2. **Given** an administrator chooses a design, **Then** the editor replaces the token values changed so far with the design, keeps the brand name and images, reruns the checks and shows the preview; nothing reaches employees until saved. *(DLS-002)*
3. **Given** a saved design, **Then** pages render with that design's tokens and navigation style, and the brand version and audit record name the change. *(DLS-003)*
4. **Given** a tenant on Atlas sets its own accent color, **Then** the accent wins over the design and every other value stays Atlas. *(DLS-004)*
5. **Given** the tenant locked `design.preset`, **When** an organization chooses another design, **Then** the child choice is ignored and the impact view says so. *(DLS-005)*

### User Story 2: Build a screen with the system (Priority: P1, R1)

A developer builds a new page from a template and the shared components; the build fails when they use a raw colour, an inline SVG icon, an image without alternative text or a literal string.

**Acceptance scenarios:**
1. **Given** a component outside the system's rules, **When** `npm run verify` runs, **Then** it fails with the rule and the fix. *(DLS-010)*
2. **Given** the gallery, **Then** it shows every shared component, the icon set, the colour and type tokens and the template patterns in the tenant's active design. *(DLS-008)*

### User Story 3: Test the design (Priority: P1, R1)

**Acceptance scenarios:**
1. **Given** a pull request, **When** CI runs the design suite, **Then** each design in each language passes axe with no serious or critical violation, has no horizontal scrolling at 375, 768 and 1440 px, has no control under 24 by 24 px, and matches the reviewed gallery baselines. *(DLS-012 to DLS-015)*
2. **Given** a feature's first build, **Then** the designer and developer run the post-development review with generated screenshots and record the outcome on the pull request. *(DLS-017)*

### Edge cases and exception flows
- An older brand names a design that no longer exists: the default design applies and the token appears in the invalid token log (spec 033 fallback).
- A tenant changes an Atlas token so a pair fails contrast: the existing spec 033 blocking or warning rules apply.
- A font file fails to load: the fallback stack of the script renders.
- A design is chosen at organization scope without `experience.org_branding`: refused as any organization override is.

## 5. Functional requirements

- **DLS-001**: The brand manager MUST present the three designs with a live miniature, description and navigation style, and mark the current one.
- **DLS-002**: Choosing a design MUST be a token change (`design.preset`) validated, checked, versioned, audited and reversible like any brand change (spec 033 FR-012).
- **DLS-003**: The theme resolver MUST apply the design's values between the platform defaults and the tenant brand (spec 033 FR-030).
- **DLS-004**: Brand and organization values MUST win over the design's values.
- **DLS-005**: `design.preset` and `layout.navigation` MUST be lockable and inheritable like any token.
- **DLS-006**: Each design MUST define every colour in light and dark mode and pass every contrast and text size check of spec 033 FR-009 in strict mode.
- **DLS-007**: The three designs MUST differ in palette, typography, shape, elevation and navigation style, and MUST NOT differ in behavior, content, permissions or information architecture.
- **DLS-008**: The web app MUST provide shared components for actions, text boxes, selections, cards, badges, avatars, alerts, images, tabs, icons and states, and a gallery showing them in the active design.
- **DLS-009**: Components MUST read only `--tb-*` tokens; per-design structural rules MUST live in one file.
- **DLS-010**: The build MUST fail on raw colours or sizes, literal font families or sizes, inline SVG outside the icon set, images without alternative text, literal user-facing strings and missing translations.
- **DLS-011**: Images MUST render with a fixed aspect ratio and lazy loading below the fold.
- **DLS-012**: CI MUST run axe on the home and gallery in every design and launch language, failing on serious or critical violations.
- **DLS-013**: CI MUST check for horizontal scrolling at phone, tablet and desktop widths in every design and direction.
- **DLS-014**: CI MUST check that interactive controls in the gallery are at least 24 by 24 CSS pixels.
- **DLS-015**: CI MUST compare gallery sections against reviewed visual baselines per design and direction.
- **DLS-016**: The design guidelines MUST cover typography, colour, icons, imagery, content, layout, actions and accessibility; the technical guidelines MUST cover frontend practice, security, performance and consistency.
- **DLS-017**: The testing strategy MUST define user-centric, accessibility, content, responsive, post-development and consistency tests with owners and gates.
- **DLS-018**: Every spec with a user interface MUST reference the design system in its UX section and list any new component or token it needs.
- **DLS-019**: New components, tokens or designs MUST update the guidelines, gallery and tests in the same change.
- **DLS-020**: Fonts MUST be self-hosted from the approved list and loaded per script on demand (ADR-0019, ADR-0020).
- **DLS-021**: All three designs MUST be available in every package.
- **DLS-022**: The design suite SHOULD write review screenshots on request for the post-development review.
- **DLS-023**: R2: theme drafts (spec 033 US2) SHOULD show the design review checklist before approval.
- **DLS-024**: R2: cross-browser runs (Firefox, WebKit) SHOULD join the design suite once the browser matrix decision in `docs/testing/e2e-plan.md` is made.

## 6. Business and validation rules
- `design.preset` takes `beats`, `atlas` or `pulse`; `layout.navigation` takes `rail`, `top` or `sidebar`. Other values are refused with `validation.theme.choice_invalid`.
- A new tenant, and any brand that sets no design, uses Beats.
- Choosing a design in the editor replaces the brand's token values; brand name, assets and contrast policy stay.

## 7. Tenant and organization scope
- The design is a token of the tenant brand profile and of organization overrides; it follows spec 033 section 7 for isolation, locks and inheritance.
- Cross-tenant denial: the same cases as spec 033 (theme CSS and assets of another tenant are not found).

## 8. Data, ownership and classification
No new data. The design is stored as a token in the brand version (spec 033 section 8).

## 9. Package entitlements
All three designs are in every package (smallest safe default: the design is part of tenant branding, which spec 033 puts in every package). Organization-level design choice needs `experience.org_branding`.

## 10. UX, localization and accessibility
- The guidelines in `docs/design-system` are the UX rules for every screen.
- Design names, descriptions and picker text are localized in Arabic and English.
- All three designs meet WCAG 2.2 AA in both directions and modes.

## 11. AI behavior and data policy
Not applicable. The AI mark (four point star) is reserved for AI features in every design.

## 12. Integrations and events
Design changes publish through `ThemePublished` like any brand change.

## 13. Audit, telemetry and analytics
`theme.published` audit records include the new brand version; the design is visible in the version's tokens. Invalid design values increment `theme.invalid_token`.

## 14. Security and abuse cases
- A design is a fixed, reviewed token set: it cannot add CSS, scripts or remote resources.
- Per-design CSS reads only tokens; fonts come only from the app's own origin under the CSP.

## 15. Non-functional requirements
- Theme CSS stays under 30 KB compressed per brand and mode with the new tokens.
- Fonts of designs a tenant does not use are not downloaded.
- Switching design causes no layout shift on load (theme in the head before paint).

## 16. Success criteria
- 100 percent of shared components appear in the gallery and pass the design suite in three designs and two languages.
- Zero serious or critical axe violations on the home and gallery in every design.
- A tenant administrator can switch design in under one minute without help.

## 17. Open questions
- Should Firefox and WebKit join the design suite in R1 or R2? Default: R2 with the browser matrix decision.
- Should tenants be able to submit a fourth design? Default: no; they adjust one of the three through tokens; revisit with R3 token extensions.

## 18. Traceability
| Requirement | Tests |
|---|---|
| DLS-002 to DLS-007 | `tests/Unit/DesignPresetTests.cs`, `ThemeEngineTests.cs`, `WebTokensFileTests.cs` |
| DLS-001, DLS-003, DLS-012 to DLS-015, DLS-022 | `tests/Acceptance/specs/design-system.spec.ts` |
| DLS-008 to DLS-011 | `src/Web/scripts/check-design.mjs`, `check-tokens.mjs`, stylelint, ESLint |
| DLS-016, DLS-017 | `docs/design-system` |

## Review checklist
- [x] No implementation detail beyond what the contract needs
- [x] Acceptance scenarios testable
- [x] Tenant, organization and package rules stated
- [x] Accessibility and localization covered
